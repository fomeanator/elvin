using System.Collections.Generic;
using System.Text;

namespace Lvn.UI
{
    /// <summary>
    /// ЛЕКАРЬ — тот, кто следит за здоровьем сцены и лечит её сам.
    ///
    /// <para>Сцену собирает асинхронный тракт: сеть, декод, поколения, чужие
    /// выгрузки. Любое звено может не доехать, и почти на каждый такой случай в
    /// коде завёлся свой сторож — со своим таймером, своим терпением и своим
    /// тегом в логе. Полотно опустело, слои куклы умерли, витрина осталась без
    /// фона, прокси перехода залип: пять мест, пять «раз в секунду посмотрим»,
    /// и ни одного ответа на простой вопрос — ЧТО В ЭТОЙ ИГРЕ ЛЕЧИЛОСЬ САМО.
    /// А это самый ценный вопрос из всех: каждое сработавшее лечение —
    /// след настоящего дефекта, который иначе увидел бы игрок.</para>
    ///
    /// <para>Здесь у них один дом. Недуг описывается четырьмя вещами: как
    /// понять, что он есть, как лечить, сколько терпеть перед вмешательством и
    /// — главное — <see cref="Busy">не делают ли эту работу прямо сейчас</see>.
    /// Загрузка имеет право доехать сама: лечить живое значит перебивать его, а
    /// терпение отвечает на этот вопрос лишь догадкой в секундах. Лекарь
    /// смотрит не чаще, чем просили, ждёт, пока везут, лечит не раньше терпения
    /// и ведёт счёт: <see cref="Journal"/> отвечает одной строкой.</para>
    ///
    /// <para>ЛЕЧЕНИЕ, КОТОРОЕ НЕ ПОМОГАЕТ, — ТОЖЕ ДИАГНОЗ. Если недуг не
    /// уходит, Лекарь разводит попытки всё шире (до <see cref="MaxPeriod"/>):
    /// молотить каждые полсекунды по неизлечимому — значит завалить лог и
    /// отобрать кадры у игры, вместо того чтобы честно сказать «не лечится».</para>
    ///
    /// <para>Время приходит снаружи (<see cref="Tick"/>), поэтому Лекаря можно
    /// проверить целиком, без сцены и без Unity.</para>
    /// </summary>
    public sealed class LvnHealer
    {
        /// <summary>Болен ли прямо сейчас.</summary>
        public delegate bool Check();

        /// <summary>Как лечить.</summary>
        public delegate void Cure();

        /// <summary>
        /// РАБОТУ УЖЕ ДЕЛАЮТ — лечить нечего, надо ждать.
        ///
        /// <para>Терпение отвечает на этот вопрос ЧИСЛОМ, то есть догадкой:
        /// «крупный канвас декодится 0.6с, потерпим 2с». Догадка врёт ровно
        /// там, где цена ошибки выше всего — на слабом телефоне и плохой сети
        /// загрузка идёт секунд десять, терпение кончается на второй, и лекарь
        /// перебивает ЖИВУЮ работу. У фона это не безобидно: он везётся с
        /// повторами и разрежением (до восьми попыток, ~2 минуты на всё), а
        /// лечение начинает эту лестницу с первой ступени — то есть лекарь
        /// ломает ровно тот механизм, который и должен был пережить обрыв.</para>
        ///
        /// <para>Поэтому у недуга есть третий вопрос, и ответ на него —
        /// ФАКТ, а не число. Пока работу делают, отсчёт терпения не идёт: он
        /// начинается с мгновения, когда делать перестали.</para>
        /// </summary>
        public delegate bool Busy();

        /// <summary>Дальше какого разрежения не разводить бесполезные попытки,
        /// секунды.</summary>
        public const float MaxPeriod = 8f;

        private sealed class Ailment
        {
            public string Name;
            public float Period;        // как часто смотреть
            public float Patience;      // сколько терпеть, прежде чем лечить
            public Check Sick;
            public Cure Heal;
            public Busy Working;        // null — спросить не у кого

            public float NextLook;      // когда смотреть в следующий раз
            // НОЛЬ — ЭТО ВРЕМЯ, А НЕ «НЕТ». Время сессии начинается с нуля, и
            // первая же проверка на первом кадре попадала ровно в него: «болен
            // с момента 0» читалось как «здоров», и недуг не лечился НИКОГДА.
            // Поэтому у болезни и лечения есть флаги, а не магические нули.
            public bool Ailing;         // замечен больным
            public float SickSince;     // с какого мгновения
            public bool Treated;        // хоть раз лечили
            public float LastHealAt;
            public float Spacing;       // текущее разрежение попыток
            public int Healed;          // сколько раз лечили
            public int InARow;          // лечений подряд без выздоровления
            public int Held;            // сколько раз не лечили, потому что везли
        }

        private readonly List<Ailment> _list = new List<Ailment>();

        /// <summary>Сколько лечений случилось за сессию — по всем недугам.</summary>
        public int Healings { get; private set; }

        /// <summary>
        /// ВЗЯТЬ НЕДУГ ПОД НАБЛЮДЕНИЕ. Повторное имя заменяет прежнюю запись:
        /// сцена пересобирается, и второй сторож того же имени означал бы
        /// двойное лечение одного и того же.
        /// </summary>
        public void Watch(string name, Check sick, Cure heal,
                          float period = 1f, float patience = 0f, Busy working = null)
        {
            if (string.IsNullOrEmpty(name) || sick == null || heal == null) return;
            Forget(name);
            _list.Add(new Ailment
            {
                Name = name,
                Period = period > 0.01f ? period : 1f,
                Patience = patience > 0f ? patience : 0f,
                Sick = sick,
                Heal = heal,
                Working = working,
                Spacing = period > 0.01f ? period : 1f,
            });
        }

        /// <summary>Снять наблюдение (сцена ушла, хост сменился).</summary>
        public void Forget(string name)
        {
            for (int i = _list.Count - 1; i >= 0; i--)
                if (_list[i].Name == name) _list.RemoveAt(i);
        }

        /// <summary>Забыть всё: сцена сменилась целиком.</summary>
        public void Clear() => _list.Clear();

        /// <summary>
        /// ОБХОД. Зовётся каждый кадр; сам решает, на кого сейчас смотреть.
        /// </summary>
        public void Tick(float now)
        {
            for (int i = 0; i < _list.Count; i++)
            {
                var a = _list[i];
                if (now < a.NextLook) continue;
                a.NextLook = now + a.Period;

                bool sick;
                try { sick = a.Sick(); }
                catch { continue; }   // сломанная проверка не должна валить обход

                if (!sick)
                {
                    // ВЫЗДОРОВЕЛ — отсчёт терпения и разрежение сбрасываются.
                    // Без сброса одно давнее недомогание заставляло бы лечить
                    // при первом же чихе через час.
                    a.Ailing = false;
                    a.InARow = 0;
                    a.Spacing = a.Period;
                    continue;
                }

                if (!a.Ailing) { a.Ailing = true; a.SickSince = now; continue; }

                // ВЕЗУТ — ЗНАЧИТ НЕ ЛЕЧИМ. И терпение считаем от этого
                // мгновения: иначе десятисекундная загрузка приезжает в мир,
                // где терпение кончилось восемь секунд назад, и первый же
                // кадр после неё был бы лечением.
                bool working;
                try { working = a.Working != null && a.Working(); }
                catch { working = false; }   // не смогли спросить — лечим как раньше
                if (working) { a.SickSince = now; a.Held++; continue; }

                if (now - a.SickSince < a.Patience) continue;
                if (a.Treated && now - a.LastHealAt < a.Spacing) continue;

                a.SickSince = now;      // терпение считается заново от лечения
                a.Treated = true;
                a.LastHealAt = now;
                a.Healed++;
                a.InARow++;
                Healings++;
                // Не помогает — разводим попытки шире (см. заголовок класса).
                if (a.InARow >= 3 && a.Spacing < MaxPeriod)
                    a.Spacing = a.Spacing * 2f < MaxPeriod ? a.Spacing * 2f : MaxPeriod;
                try { a.Heal(); }
                catch { /* лечение — попытка, а не обязательство */ }
            }
        }

        /// <summary>Сколько раз лечили этот недуг (0 — ни разу).</summary>
        public int HealedCount(string name)
        {
            foreach (var a in _list) if (a.Name == name) return a.Healed;
            return 0;
        }

        /// <summary>
        /// ЧТО ЛЕЧИЛОСЬ САМО — одной строкой в лог.
        ///
        /// <para>Ради этого Лекарь и заведён. Пустой журнал значит, что сцена
        /// собралась как задумано; непустой — список настоящих дефектов, каждый
        /// со счётчиком, и «лечили 12 раз» читается совсем иначе, чем «однажды
        /// мелькнуло».</para>
        /// </summary>
        public string Journal()
        {
            if (_list.Count == 0) return "[lvn-healer] под наблюдением никого";
            var sb = new StringBuilder("[lvn-healer] ");
            if (Healings == 0)
            {
                sb.Append($"наблюдений {_list.Count}, лечить не пришлось ни разу");
                int held = 0;
                foreach (var a in _list) held += a.Held;
                if (held > 0) sb.Append($" (ждали работу {held})");
                return sb.ToString();
            }
            sb.Append($"лечений {Healings}:");
            foreach (var a in _list)
            {
                if (a.Healed == 0) continue;
                sb.Append($" {a.Name}×{a.Healed}");
                if (a.InARow >= 3) sb.Append(" (НЕ ЛЕЧИТСЯ)");
                // «Ждали» — не шум, а мера того, насколько терпение разошлось с
                // жизнью: большое число значит, что число секунд подобрано не
                // под ту работу, которую на самом деле ждут.
                if (a.Held > 0) sb.Append($" (ждали {a.Held})");
            }
            return sb.ToString();
        }
    }
}
