using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// Движение интерфейса по ПРУЖИНЕ, а не по кривой.
    ///
    /// <para>Разницу между дорогим интерфейсом и дешёвым делает тайминг, а не
    /// украшения. Элемент, который приезжает в точку и останавливается, читается
    /// как «сделано программистом» мгновенно и безошибочно. Элемент, который
    /// проскакивает цель на несколько процентов и оседает, читается как
    /// физический предмет.</para>
    ///
    /// <para>USS-переходы этого не умеют: там фиксированный набор кривых, и
    /// проскока среди них нет. Поэтому свойства двигаются отсюда, из кода.</para>
    ///
    /// <para>ВТОРОЕ ПО ВАЖНОСТИ — ХОРЕОГРАФИЯ. Пять карточек, выехавших разом,
    /// выглядят как перерисовка экрана. Те же пять со сдвигом в полсотни
    /// миллисекунд — как намерение. Отсюда <see cref="Stagger"/>.</para>
    /// </summary>
    public static class LvnMotion
    {
        /// <summary>
        /// Насколько «живой» пружина. Затухание 1 — без проскока (для того, что
        /// уходит), 0,55–0,7 — заметный, но не клоунский проскок (для того, что
        /// появляется). Ниже 0,4 начинается желе.
        /// </summary>
        public const float DampingSoft = 0.62f; // появление: проскок ~7%
        public const float DampingFirm = 1.0f;  // исчезновение: без отскока

        /// <summary>Жёсткость. Больше — быстрее приход. 120 ≈ 260 мс до покоя,
        /// что попадает в разумные для телефона 150–300 мс.</summary>
        public const float Stiffness = 120f;

        /// <summary>Сдвиг между соседними элементами в хореографии. Меньше 30 мс
        /// глаз не различает, больше 90 мс читается как задержка интерфейса.</summary>
        public const int StaggerMs = 55;

        // Шаг интегрирования. ФИКСИРОВАННЫЙ и мелкий намеренно: пружина,
        // проинтегрированная переменным кадром, на просадке до 20 fps не просто
        // замедляется, а РАСХОДИТСЯ — элемент улетает за экран. Кадр делится на
        // подшаги, поэтому анимация одинакова и на 30, и на 120 кадрах.
        private const float FixedStep = 1f / 120f;
        // Потолок подшагов. Он ограничивает НЕ размер шага, а сколько ВРЕМЕНИ мы
        // соглашаемся отыграть за один вызов: свернули приложение на десять
        // секунд — досчитывать эти десять секунд незачем, элемент всё равно
        // давно должен был приехать.
        private const int MaxSubSteps = 32; // до ~0,27 с за вызов, то есть вплоть до 4 fps

        /// <summary>
        /// Один шаг пружины. Вынесен отдельно и без зависимостей от Unity, чтобы
        /// поведение можно было проверить тестом, а не глазами.
        /// </summary>
        /// <param name="value">текущее значение (изменяется)</param>
        /// <param name="velocity">текущая скорость (изменяется)</param>
        /// <param name="target">куда стремимся</param>
        /// <param name="dt">прошедшее время, секунды</param>
        public static void Step(ref float value, ref float velocity, float target,
                                float dt, float stiffness = Stiffness, float damping = DampingSoft)
        {
            if (dt <= 0f) return;
            // Критическое затухание для этой жёсткости; damping — доля от него.
            float c = 2f * Mathf.Sqrt(stiffness) * damping;
            // Обрезаем ВРЕМЯ, а не число шагов. Первая версия делила любой dt на
            // не более чем MaxSubSteps шагов — и на dt=10 с получала шаг в
            // 0,6 с, на котором пружина расходится: тест поймал вылет в −3,8e27.
            float sim = Mathf.Min(dt, MaxSubSteps * FixedStep);
            int steps = Mathf.Max(1, Mathf.CeilToInt(sim / FixedStep));
            float h = sim / steps;
            for (int i = 0; i < steps; i++)
            {
                // Полунеявный Эйлер: скорость обновляется до положения. Явный
                // Эйлер на тех же шагах копит энергию и раскачивает пружину.
                velocity += (-stiffness * (value - target) - c * velocity) * h;
                value += velocity * h;
            }
        }

        /// <summary>Пришли ли в покой. Оба порога, а не один: значение может
        /// совпасть с целью на лету, в момент максимальной скорости.</summary>
        public static bool AtRest(float value, float velocity, float target,
                                  float epsValue = 0.001f, float epsVel = 0.01f)
            => Mathf.Abs(value - target) < epsValue && Mathf.Abs(velocity) < epsVel;

        /// <summary>
        /// Появление: элемент выезжает снизу и проявляется.
        /// </summary>
        /// <param name="el">что двигаем</param>
        /// <param name="fromY">откуда, в пикселях ниже конечного места</param>
        /// <param name="delayMs">задержка старта — для хореографии</param>
        public static void SlideIn(VisualElement el, float fromY = 22f, int delayMs = 0)
        {
            if (el == null) return;
            Animate(el, delayMs, (t, e) =>
            {
                // t: 0 → 1. Позиция и прозрачность из одного источника, поэтому
                // они не могут разъехаться при просадке кадров.
                e.style.translate = new Translate(0, fromY * (1f - t));
                e.style.opacity = Mathf.Clamp01(t);
            }, DampingSoft);
        }

        /// <summary>
        /// Появление с подскоком: элемент приходит из уменьшенного состояния.
        /// Для того, что должно ощущаться как «прилетело»: награда, метка.
        /// </summary>
        public static void PopIn(VisualElement el, float fromScale = 0.88f, int delayMs = 0)
        {
            if (el == null) return;
            Animate(el, delayMs, (t, e) =>
            {
                float s = Mathf.LerpUnclamped(fromScale, 1f, t); // Unclamped: проскок за 1 нужен
                e.style.scale = new Scale(new Vector2(s, s));
                e.style.opacity = Mathf.Clamp01(t);
            }, DampingSoft);
        }

        /// <summary>
        /// Хореография: те же появления, но со сдвигом по времени. Первый
        /// элемент идёт сразу, каждый следующий — на <paramref name="stepMs"/>
        /// позже.
        /// </summary>
        public static void Stagger(IEnumerable<VisualElement> items, float fromY = 22f,
                                   int stepMs = StaggerMs, int startDelayMs = 0)
        {
            if (items == null) return;
            int i = 0;
            foreach (var el in items)
            {
                SlideIn(el, fromY, startDelayMs + i * stepMs);
                i++;
                // Больше десятка элементов подряд копят задержку до полусекунды,
                // и хвост списка приезжает, когда игрок уже смотрит в другое
                // место. Дальше идут разом.
                if (i >= 10) stepMs = 0;
            }
        }

        /// <summary>
        /// Нажатие: короткое сжатие и возврат. Делает интерфейс осязаемым
        /// сильнее, чем любая подсветка.
        /// </summary>
        public static void Press(VisualElement el, float scale = 0.96f)
        {
            if (el == null) return;
            el.RegisterCallback<PointerDownEvent>(_ =>
                el.style.scale = new Scale(new Vector2(scale, scale)));
            // И отпускание, и уход пальца за пределы: без второго элемент
            // залипает сжатым, если игрок передумал и увёл палец.
            EventCallback<EventBase> back = _ => Animate(el, 0, (t, e) =>
            {
                float s = Mathf.LerpUnclamped(scale, 1f, t);
                e.style.scale = new Scale(new Vector2(s, s));
            }, DampingSoft);
            el.RegisterCallback<PointerUpEvent>(e => back(e));
            el.RegisterCallback<PointerLeaveEvent>(e => back(e));
        }

        /// <summary>
        /// Общий двигатель. Считает пружину 0 → 1 и отдаёт значение наружу.
        ///
        /// <para>Работает на планировщике самого элемента, а не на MonoBehaviour:
        /// планировщик привязан к панели и умирает вместе с ней. Экран, закрытый
        /// посреди анимации, не оставляет за собой ни живого объекта, ни ссылки
        /// на уничтоженный элемент.</para>
        /// </summary>
        private static void Animate(VisualElement el, int delayMs,
                                    Action<float, VisualElement> apply, float damping)
        {
            float v = 0f, vel = 0f;
            apply(0f, el);
            IVisualElementScheduledItem item = null;
            item = el.schedule.Execute((TimerState ts) =>
            {
                // Время берём у планировщика, а не у Time: он и так его считает,
                // а в редакторе вне игрового режима Time.realtimeSinceStartup
                // ведёт себя иначе, чем в сборке.
                float dt = Mathf.Min(ts.deltaTime / 1000f, 0.25f); // окно потери фокуса не догоняем
                Step(ref v, ref vel, 1f, dt, Stiffness, damping);
                apply(v, el);
                if (AtRest(v, vel, 1f))
                {
                    apply(1f, el);   // ровно в цель: остаточные 0,999 копятся в раскладке
                    item.Pause();    // остановка планировщиком, а не исключением
                }
            }).Every(16);
            if (delayMs > 0) item.ExecuteLater(delayMs);
        }
    }
}
