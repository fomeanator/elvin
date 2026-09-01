using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Lvn.Content
{
    /// <summary>
    /// ПОЛОСА: сколько работ одного вида идёт разом — и кто заходит первым.
    ///
    /// <para>Правило «сколько сразу» стояло в трёх местах и в каждом по своей
    /// причине: двенадцать мест в сети (потоки HTTP/2), три места у распаковки
    /// (чтобы выгрузка в видеопамять не приезжала одним залпом), двенадцать/
    /// шесть/два у расписания главы (чтобы пара крупных файлов не заняла
    /// соединение). Три верных числа — и ни одно из них не знало ГЛАВНОГО:
    /// ждёт ли этот файл поверхность прямо сейчас.</para>
    ///
    /// <para>Из-за этого живая картинка стояла в очереди за фоновым прогревом
    /// НЕ ПО НЕВЕЗЕНИЮ, А ПО УСТРОЙСТВУ: мест ровно столько, кто первым попросил
    /// — того и место. Игрок при этом смотрел на пустую рамку.</para>
    ///
    /// <para><b>Полоса шириной W держит K мест для живого.</b> Фоновая работа
    /// физически не может занять больше <c>W-K</c>, поэтому живому всегда есть
    /// куда встать.</para>
    ///
    /// <para><b>Брони мало, когда живого много.</b> Бронь спасает ОДНО живое
    /// дело: она не даёт фону занять последние места. Но у актёра слоёв
    /// пять-восемь, и все они живые — третий такой запрос встаёт в очередь за
    /// фоном честно, по устройству полосы. Поэтому у полосы есть и второй
    /// приём: попросить фон УСТУПИТЬ место.</para>
    ///
    /// <para><b>Уступка — не отмена.</b> Тот, кого попросили, получает свой
    /// признак (<see cref="Pass.Yield"/>), обрывает работу и ЗАХОДИТ СНОВА,
    /// когда живое прошло. Для его вызывающего ничего не случилось: он не
    /// видит ни отмены, ни ошибки — только более долгую загрузку. Отмена
    /// самого вызывающего при этом остаётся отменой: это разные признаки, и
    /// путать их нельзя — иначе «игрок вышел из главы» станет «повторим
    /// позже».</para>
    ///
    /// <para>Уступивший теряет кусок, скачанный сверх последнего сохранённого:
    /// на диске остаётся то, что уже дописано в <c>.part</c>, и заход
    /// продолжается оттуда. Честнее было бы не терять ничего, но это стоило бы
    /// записи каждого пакета на диск — цена выше пропажи.</para>
    /// </summary>
    public sealed class LvnLane
    {
        private readonly SemaphoreSlim _all;        // все места полосы
        private readonly SemaphoreSlim _background; // места, доступные НЕ живому

        /// <param name="name">Как полоса зовётся в диагностике.</param>
        /// <param name="width">Сколько работ идёт разом.</param>
        /// <param name="keptForLive">Сколько мест из них фоновая работа занять
        /// не может. Ноль — полоса без брони: так ведут себя полосы, по которым
        /// живое не ходит вовсе.</param>
        public LvnLane(string name, int width, int keptForLive)
        {
            if (width < 1) throw new ArgumentOutOfRangeException(nameof(width));
            if (keptForLive < 0 || keptForLive >= width)
                throw new ArgumentOutOfRangeException(nameof(keptForLive),
                    "бронь должна быть меньше ширины: полоса, целиком отданная живому, "
                    + "останавливает фон навсегда");
            Name = name;
            Width = width;
            KeptForLive = keptForLive;
            _all = new SemaphoreSlim(width, width);
            _background = new SemaphoreSlim(width - keptForLive, width - keptForLive);
        }

        public string Name { get; }
        public int Width { get; }
        public int KeptForLive { get; }

        /// <summary>Сколько мест свободно прямо сейчас. Нужно ДИАГНОСТИКЕ и
        /// проверкам: «место вернулось на любом выходе» — правило, которое не
        /// видно ниоткуда, кроме этого числа, а стоит его нарушение
        /// остановки всех загрузок до перезапуска приложения.</summary>
        public int Free => _all.CurrentCount;

        /// <summary>Занять место. Ступень берётся у того, кто просит, а если он
        /// молчит — у окружения (<see cref="LvnRungScope"/>).</summary>
        public Task<Pass> EnterAsync(CancellationToken ct) => EnterAsync(LvnRungScope.Current, ct);

        public async Task<Pass> EnterAsync(LvnRung rung, CancellationToken ct)
        {
            bool live = rung == LvnRung.Live;
            if (!live)
            {
                await _background.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await _all.WaitAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    _background.Release();
                    throw;
                }
                var seat = new Seat();
                lock (_seats) _seats.Add(seat);
                return new Pass(this, seat);
            }

            // ЖИВОЕ ПРОСИТ ФОН УСТУПИТЬ — до того, как встать в очередь.
            // Просим ровно одного и самого давнего: он ближе всех к концу
            // работы, и его потеря меньше. Просить всех значило бы обрушить
            // фоновую очередь ради одного кадра.
            if (_all.CurrentCount == 0) AskOneToYield();
            await _all.WaitAsync(ct).ConfigureAwait(false);
            return new Pass(this, null);
        }

        private void AskOneToYield()
        {
            Seat victim = null;
            lock (_seats)
                foreach (var s in _seats)
                    if (!s.Asked) { victim = s; break; }
            if (victim == null) return;
            victim.Asked = true;
            try { victim.Yield.Cancel(); } catch { /* уже ушёл — не беда */ }
        }

        private void Leave(Seat seat)
        {
            _all.Release();
            if (seat == null) return;
            lock (_seats) _seats.Remove(seat);
            seat.Yield.Dispose();
            _background.Release();
        }

        /// <summary>Занятое фоном место, которое можно попросить вернуть.</summary>
        internal sealed class Seat
        {
            public readonly CancellationTokenSource Yield = new CancellationTokenSource();
            public bool Asked;
        }

        // Занятые фоном места, в порядке занятия: первый в списке — самый давний.
        private readonly List<Seat> _seats = new List<Seat>();

        /// <summary>Место в полосе. Освобождается выходом из <c>using</c> —
        /// парного <c>Release()</c> в <c>finally</c> писать больше не нужно, и
        /// забыть его больше нельзя.</summary>
        public readonly struct Pass : IDisposable
        {
            private readonly LvnLane _lane;
            private readonly Seat _seat;
            internal Pass(LvnLane lane, Seat seat) { _lane = lane; _seat = seat; }

            /// <summary>Срабатывает, когда место просят вернуть живому. Живое
            /// место не просят никогда — у него признак пустой.</summary>
            public CancellationToken Yield
                => _seat != null ? _seat.Yield.Token : CancellationToken.None;

            /// <summary>Место уже попросили вернуть. Отличать от отмены
            /// вызывающего обязан тот, кто ловит: уступка означает «зайти
            /// снова», отмена — «больше не нужно».</summary>
            public bool Yielded => _seat != null && _seat.Asked;

            public void Dispose() => _lane?.Leave(_seat);
        }
    }

    /// <summary>
    /// СТУПЕНЬ ТОГО, ЧТО СЕЙЧАС ДЕЛАЕТСЯ. Ответ на вопрос «увидят ли это
    /// сейчас» знает не загрузчик, а тот, кто его позвал: сцена ставит актёра в
    /// кадр — живое; прогрев каста набивает диск на будущее — запас.
    ///
    /// <para>Передать ступень отдельным доводом нельзя: <c>LoadSpriteAsync</c>
    /// — это дверь расширения (<c>ILvnAssets</c>), у неё восемь реализаций и
    /// сорок шесть зовущих, и менять её ради довода, который нужен трём из
    /// них, — цена не по работе. Поэтому ступень едет ОКРУЖЕНИЕМ: фоновая
    /// работа объявляет себя один раз на весь цикл, и всё, что она позовёт
    /// вглубь, наследует объявление.</para>
    ///
    /// <para><b>Умолчание — «живое».</b> Кто молчит, того считаем видимым.
    /// Объявляться обязан ФОН, и это правильная сторона: фоновых мест в движке
    /// пять и они наперечёт, а живых — весь остальной код. Забыть объявить фон
    /// — потерять бронь, а не показать пустоту.</para>
    /// </summary>
    public static class LvnRungScope
    {
        private static readonly AsyncLocal<LvnRung?> _current = new AsyncLocal<LvnRung?>();

        public static LvnRung Current => _current.Value ?? LvnRung.Live;

        /// <summary>Объявить ступень до конца <c>using</c>. Всё, что запущено
        /// внутри, наследует её — включая то, что доделается уже снаружи.</summary>
        public static Scope At(LvnRung rung) => new Scope(rung);

        public readonly struct Scope : IDisposable
        {
            private readonly LvnRung? _prev;
            internal Scope(LvnRung rung) { _prev = _current.Value; _current.Value = rung; }
            public void Dispose() => _current.Value = _prev;
        }
    }

    /// <summary>Полосы движка. Ширины разные, потому что дефициты разные: сеть
    /// меряется потоками соединения, распаковка — рабочими потоками и выгрузкой
    /// в видеопамять. Общее у них одно — бронь для живого.</summary>
    public static class LvnLanes
    {
        /// <summary>СЕТЬ. HTTP/2 мультиплексирует запросы в одном соединении, так
        /// что двенадцать — это не двенадцать сокетов, а двенадцать потоков; при
        /// шести (предел HTTP/1.1) пачка мелких файлов платила лишний круг на
        /// каждый. Двое мест берегутся живому.</summary>
        public static readonly LvnLane Wire = new LvnLane("сеть", 12, 2);

        /// <summary>РАСПАКОВКА. Три — чтобы готовые картинки (и выгрузка в
        /// видеопамять внутри) размазывались по кадрам, а не приезжали залпом.
        /// Одно место берегётся живому: иначе иконка, которую игрок уже видит
        /// пустой, ждёт распаковки трёх фоновых.</summary>
        public static readonly LvnLane Decoder = new LvnLane("распаковка", 3, 1);
    }
}
