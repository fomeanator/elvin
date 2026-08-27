using System;
using System.Collections.Generic;

namespace Lvn.UI
{
    /// <summary>
    /// ХРОНОМЕТРИСТ СЦЕНЫ — единственный, кто знает про порядок во времени:
    /// чья работа ещё актуальна, чья устарела и до какого момента ждём.
    ///
    /// <para>Сцена собирается асинхронно: команда пришла, арт поехал по сети,
    /// декод занял полсекунды, а за это время сменилась глава, игрок перелистал
    /// три эмоции и открыл гардероб. Ровно отсюда весь класс наших самых
    /// дорогих багов — «мелькнула», «вошла раньше, чем ушёл предыдущий»,
    /// «поздний наряд победил новый», «реплика напечаталась под непрозрачной
    /// загрузкой». Каждый ловился только на живом устройстве.</para>
    ///
    /// <para>Механизмов порядка было ПЯТЬ, и жили они порознь: эпоха сцены,
    /// поколение актёра, барьер ухода, барьер видимости, поколение ожидания.
    /// Пять счётчиков в трёх файлах — это не пять правил, это одно правило,
    /// написанное пять раз. Здесь оно одно, и его можно проверить тестом, не
    /// поднимая сцену.</para>
    ///
    /// <para>Три понятия, и все три — про «кто опоздал»:</para>
    /// <list type="bullet">
    ///   <item><b>Эпоха</b> — жизнь одной сцены. Смена главы, загрузка сейва,
    ///   уборка сцены поднимают её, и всякая работа, начатая в прошлой эпохе,
    ///   теряет право трогать экран.</item>
    ///   <item><b>Дорожка</b> (lane) — линия работ, где важен только САМЫЙ
    ///   НОВЫЙ: показ конкретного актёра, смена фона, карточка диалога.
    ///   Начавший работу берёт номер и потом спрашивает, не обогнали ли его.</item>
    ///   <item><b>Барьер</b> — «до этого момента не начинать»: уходящий должен
    ///   доиграть свой уход, кроссфейд облика — свой кроссфейд.</item>
    /// </list>
    ///
    /// <para>Время берётся снаружи (<see cref="Now"/>), поэтому тест гоняет
    /// часы вручную и не ждёт ни секунды реального времени.</para>
    /// </summary>
    public sealed class LvnStageClock
    {
        /// <summary>Откуда берётся «сейчас», в секундах. По умолчанию —
        /// нескалируемое реальное время: барьеры обязаны идти по тем же часам,
        /// что и фейды, даже когда игровое время остановлено или ускорено.</summary>
        public Func<float> Now = () => UnityEngine.Time.realtimeSinceStartup;

        // ── эпоха сцены ───────────────────────────────────────────────────────

        /// <summary>Текущая эпоха. Асинхронная работа запоминает её ДО первого
        /// ожидания и после каждого сверяется через <see cref="IsCurrent"/>.</summary>
        public int Epoch { get; private set; }

        /// <summary>Сцену убрали (смена главы, загрузка сейва): всё, начатое
        /// раньше, больше не имеет права рисовать. Барьеры при этом снимаются —
        /// ждать уходов прошлой сцены незачем, их уже нет.</summary>
        public int NewEpoch()
        {
            Epoch++;
            _until.Clear();
            return Epoch;
        }

        /// <summary>Та ли это ещё сцена.</summary>
        public bool IsCurrent(int epoch) => epoch == Epoch;

        // ── дорожки: «важен только самый новый» ───────────────────────────────

        private readonly Dictionary<string, int> _lanes = new Dictionary<string, int>();

        /// <summary>Взять номер на дорожке — начинающий работу объявляет себя
        /// новейшим. Прежний владелец узнает об этом по <see cref="IsNewest"/>
        /// и тихо уйдёт, не тронув экран.</summary>
        public int Claim(string lane)
        {
            if (string.IsNullOrEmpty(lane)) return 0;
            int next = (_lanes.TryGetValue(lane, out var cur) ? cur : 0) + 1;
            _lanes[lane] = next;
            return next;
        }

        /// <summary>Моя работа всё ещё самая новая на этой дорожке?</summary>
        public bool IsNewest(string lane, int ticket)
            => string.IsNullOrEmpty(lane)
               || !_lanes.TryGetValue(lane, out var cur) || cur == ticket;

        /// <summary>И эпоха та же, и на дорожке никто не обогнал — полное право
        /// трогать экран. Именно эту пару проверяют все асинхронные тракты.</summary>
        public bool MayTouch(int epoch, string lane, int ticket)
            => IsCurrent(epoch) && IsNewest(lane, ticket);

        /// <summary>Отменить всё, что идёт по дорожке, никого не начиная —
        /// «этот таймер больше не наш» (тап отменяет ожидание).</summary>
        public void Cancel(string lane) => Claim(lane);

        // ── барьеры: «до этого момента не начинать» ───────────────────────────

        private readonly Dictionary<string, float> _until = new Dictionary<string, float>();

        /// <summary>Занять барьер на <paramref name="seconds"/> вперёд. Барьер
        /// ПРОДЛЕВАЕТСЯ, а не переустанавливается: два уходящих одновременно —
        /// ждём того, кто дольше.</summary>
        public void Hold(string barrier, float seconds)
        {
            if (string.IsNullOrEmpty(barrier) || seconds <= 0f) return;
            float until = Now() + seconds;
            _until[barrier] = _until.TryGetValue(barrier, out var cur) && cur > until ? cur : until;
        }

        /// <summary>Сколько секунд барьер ещё держит; 0 — свободен.</summary>
        public float Remaining(string barrier)
        {
            if (string.IsNullOrEmpty(barrier) || !_until.TryGetValue(barrier, out var until)) return 0f;
            float left = until - Now();
            return left > 0f ? left : 0f;
        }

        /// <summary>Барьер свободен.</summary>
        public bool Passed(string barrier) => Remaining(barrier) <= 0.001f;

        /// <summary>Снять барьер досрочно (сцену убрали, ход отменили).</summary>
        public void Release(string barrier)
        {
            if (!string.IsNullOrEmpty(barrier)) _until.Remove(barrier);
        }

        /// <summary>Забыть всё: и барьеры, и дорожки. Полный сброс сцены.</summary>
        public void Reset()
        {
            _until.Clear();
            _lanes.Clear();
        }

        // ── имена дорожек и барьеров ──────────────────────────────────────────
        // Строками, а не полями: дорожек столько, сколько актёров на сцене.
        // Имена собираются ЗДЕСЬ, чтобы «actor:hill» не разошлось с «actor-hill»
        // в соседнем файле — молчаливая ошибка, которую не видно до живого бага.

        /// <summary>Показ конкретного актёра: важен только самый новый.</summary>
        public static string ActorLane(string id) => "actor:" + id;

        /// <summary>Смена фона.</summary>
        public const string BackgroundLane = "bg";

        /// <summary>Ожидание по команде <c>wait</c>.</summary>
        public const string WaitLane = "wait";

        /// <summary>Замена карточки диалога.</summary>
        public const string DialogueLane = "dialogue";

        /// <summary>Уходящие актёры доигрывают уход, прежде чем войдёт следующий.</summary>
        public const string ActorExitBarrier = "actor-exit";

        /// <summary>Вход или уход персонажа держит ввод: тап не должен сменить
        /// реплику, пока актёр этой реплики ещё летит.</summary>
        public const string ActorVisibilityBarrier = "actor-visible";

        /// <summary>Кроссфейд облика конкретного актёра — следующая команда
        /// стыкуется за ним, а не срезает его в один кадр.</summary>
        public static string SwapBarrier(string id) => "swap:" + id;
    }
}
