using System;
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
    /// куда встать. Это БРОНЬ, а не вытеснение: уже начатую фоновую закачку
    /// полоса не прерывает — она лишь не пускает следующую. Вытеснение (обрыв
    /// на середине с сохранением куска) — отдельная работа, см.
    /// <c>docs/loader.md</c>, закон 1.</para>
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
            }
            else
            {
                await _all.WaitAsync(ct).ConfigureAwait(false);
            }
            return new Pass(this, live);
        }

        private void Leave(bool live)
        {
            _all.Release();
            if (!live) _background.Release();
        }

        /// <summary>Место в полосе. Освобождается выходом из <c>using</c> —
        /// парного <c>Release()</c> в <c>finally</c> писать больше не нужно, и
        /// забыть его больше нельзя.</summary>
        public readonly struct Pass : IDisposable
        {
            private readonly LvnLane _lane;
            private readonly bool _live;
            internal Pass(LvnLane lane, bool live) { _lane = lane; _live = live; }
            public void Dispose() => _lane?.Leave(_live);
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
