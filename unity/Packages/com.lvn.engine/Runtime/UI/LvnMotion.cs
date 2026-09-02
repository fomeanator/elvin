using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
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
        // ── ПРАЙС-ЛИСТ ВРЕМЕНИ ────────────────────────────────────────────────
        // Одно место, где решено, СКОЛЬКО ДЛИТСЯ движение в интерфейсе. Раньше
        // длительность подбиралась на месте — 130 здесь, 180 там, 240 в
        // соседнем экране, — и разница читалась не как замысел, а как
        // небрежность: соседние элементы одного экрана двигались вразнобой.
        // Значения именованы по НАЗНАЧЕНИЮ, а не по числу: правка одного имени
        // меняет ритм всей оболочки разом.

        /// <summary>Мелкая смена состояния на месте: подсветка, цвет чипа.
        /// Быстрее 90 мс глаз не успевает прочесть переход как движение.</summary>
        public const int Instant = 90;
        /// <summary>Мелкий элемент едет: ползунок, бейдж, значок.</summary>
        public const int Quick = 130;
        /// <summary>Обычная смена вида: вкладки, кнопки, панели. Базовый ритм.</summary>
        public const int Normal = 180;
        /// <summary>Содержимое проступает (плитки, карточки, строки).</summary>
        public const int Reveal = 160;
        /// <summary>Крупный переезд: лента довозит карточку, панель меняет высоту.</summary>
        public const int Calm = 240;
        /// <summary>ЭКРАН ЦЕЛИКОМ проявляется или уходит: попап, накладной
        /// раздел, галерея, вход, гардероб. Самое крупное движение оболочки —
        /// и единственное, которого в этом списке не было: пять экранов
        /// держали свои числа (0,18 / 0,25 / 0,3), а решение «в темп актёров»
        /// (Илья 25.08) знал только накладной экран. «Дорого» читается именно
        /// из согласованности: экран, гаснущий на своей скорости, выпадает из
        /// ритма, даже когда каждая скорость по отдельности хороша.</summary>
        public const int Screen = 200;
        /// <summary>ПОЯВЛЕНИЕ ОБОЛОЧКИ: полосы меню выезжают за кромки экрана —
        /// нижняя навигация снизу, верхний бар сверху. Длительность одна на
        /// обе: это не два движения, а одно раскрытие интерфейса, и разъехаться
        /// они не должны.</summary>
        public const int Curtain = 1200;
        /// <summary>«Сказал и вернул»: кнопка на секунду отвечает делом
        /// («Готово», «Скопировано») и возвращает свою надпись.</summary>
        public const int Notice = 1200;
        /// <summary>То же, но для ответа, который надо успеть прочесть и
        /// осмыслить («Точно удалить?»).</summary>
        public const int NoticeLong = 2500;

        /// <summary>ГЛОБАЛЬНЫЙ ТЕМП — единственная ручка «быстрее/медленнее» для
        /// всего движения сразу. 1 = как задумано, 0.5 = вдвое живее, 2 = вдвое
        /// вальяжнее. Меняется на лету (настройки, режим демонстрации,
        /// отладка): всё, что считает длительность через <see cref="Ms"/> и
        /// <see cref="Sec"/>, подхватывает новое значение со следующей
        /// анимации.</summary>
        public static float Tempo
        {
            get { EnsureComfort(); return _tempo; }
            set
            {
                float v = Mathf.Clamp(value, 0.05f, 4f);
                if (Mathf.Approximately(v, _tempo)) return;
                _tempo = v;
                TempoChanged?.Invoke();
            }
        }
        private static float _tempo = 1f;

        /// <summary>Во сколько раз живее движется интерфейс, когда игрок просит
        /// меньше движения. Не ноль: мгновенные подмены читаются как сбой
        /// отрисовки, а не как спокойствие — цель настройки убрать РАЗМАХ, а не
        /// связность.</summary>
        private const float ComfortTempo = 0.35f;

        private static bool _comfortWired;

        /// <summary>
        /// НАСТРОЙКА «МЕНЬШЕ ДВИЖЕНИЯ» ДОХОДИТ ДО ВСЕГО ДВИЖЕНИЯ.
        ///
        /// <para>Ручка темпа существовала с самого начала и описывала себя как
        /// «единственная ручка быстрее/медленнее для всего сразу» — но её никто
        /// не крутил. Настройку при этом уважали ДВА места: тряска экрана и
        /// полноэкранные эффекты. Всё остальное — выезд навбара на 1,2 секунды,
        /// подъезд контента, катсцена ухода в главу — шло полным ходом.</para>
        ///
        /// <para>То есть настройка обещала игроку то, чего не делала. Для
        /// человека с вестибулярной чувствительностью это не мелочь: он её
        /// включил и получил ровно то же самое.</para>
        ///
        /// <para>Подписка ленивая и одноразовая: темп спрашивают все, кто
        /// считает длительность, поэтому первый же расчёт её и заводит — ничей
        /// код инициализации трогать не пришлось.</para>
        /// </summary>
        private static void EnsureComfort()
        {
            if (_comfortWired) return;
            _comfortWired = true;
            LvnPrefs.Changed += ApplyComfort;
            ApplyComfort();
        }

        private static void ApplyComfort()
        {
            float want = LvnPrefs.ReduceMotion ? ComfortTempo : 1f;
            if (Mathf.Approximately(want, _tempo)) return;
            _tempo = want;
            TempoChanged?.Invoke();
        }

        /// <summary>НЕ ПОДКЛЮЧЁН: темп сменили — тем, кто закешировал
        /// длительности, пора пересчитать. Слушателей нет ни одного, и это
        /// осознанно.
        ///
        /// <para>USS-переход хранит длительность В СТИЛЕ ЭЛЕМЕНТА: она берётся
        /// один раз, когда переход назначают. Значит после смены темпа уже
        /// собранные элементы доигрывают по-старому, пока их не пересоберут, —
        /// и чтобы это починить, нужен список тронутых элементов, то есть ещё
        /// одна память, которую придётся вести руками. Цена выше беды: темп
        /// меняют в настройках, оттуда игрок возвращается на пересобранный
        /// экран.</para>
        ///
        /// <para>Единственное место, куда настройка НЕ доезжала совсем, —
        /// полоса в <c>LvnUiLive</c>: она писала свои 0.22 с мимо этого дома и
        /// один раз навсегда. Исправлено 01.09 не подпиской, а тем, что
        /// длительность там теперь спрашивается у дома на каждом обновлении.
        /// Это и есть правильный ответ на «A обязан толкнуть B»: сделать так,
        /// чтобы B спрашивал сам.</para></summary>
        public static event Action TempoChanged;

        /// <summary>
        /// Длительность с учётом темпа. ЧЕРЕЗ НЕЁ ОБЯЗАНО ИДТИ ВСЁ, что движется:
        /// мимо неё анимация не слышит ни ручку темпа, ни настройку игрока
        /// «меньше движения» — а та должна убирать размах ВЕЗДЕ, иначе половина
        /// экрана успокаивается, а половина продолжает дёргаться, и это
        /// раздражает сильнее исходного движения.
        /// </summary>
        public static int Ms(int ms)
        {
            EnsureComfort();
            return Mathf.Max(1, Mathf.RoundToInt(ms * _tempo));
        }

        /// <summary>Длительность в секундах с учётом темпа.</summary>
        public static float Sec(float seconds)
        {
            EnsureComfort();
            return Mathf.Max(0f, seconds * _tempo);
        }

        /// <summary>
        /// Насколько «живой» пружина. Затухание 1 — без проскока (для того, что
        /// уходит), 0,55–0,7 — заметный, но не клоунский проскок (для того, что
        /// появляется). Ниже 0,4 начинается желе.
        /// </summary>
        public const float DampingSoft = 0.62f;   // появление: проскок ~7%
        public const float DampingFirm = 1.0f;    // исчезновение: без отскока
        /// <summary>Отпускание кнопки: проскок ~15%. Заметно больше, чем у
        /// появления, и намеренно — палец уже ушёл, и этот отскок единственное,
        /// что подтверждает нажатие. Ниже 0,35 начинается желе.</summary>
        public const float DampingBouncy = 0.45f;

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
        /// ТОРМОЖЕНИЕ У ЦЕЛИ (OutCubic) — так в этой оболочке ПРИХОДЯТ: панель,
        /// лист, вкладка, секция кружка загрузок. Движение начинается быстро и
        /// мягко садится на место.
        ///
        /// <para>Кривая была написана ДЕВЯТЬ РАЗ одной и той же строкой в шести
        /// файлах — и в доме движения её при этом не было. Соседний
        /// <see cref="LvnAppear"/> даже отметил это словами: «своей Ease нет».
        /// Пока имени нет, «как выглядит движение» — не решение дома, а
        /// привычка автора файла: подправить его разом было негде.</para>
        ///
        /// <para>С <see cref="Enter"/> не путать: тот РАЗГОНЯЕТСЯ (в начале
        /// медленно, в конце сильнее) — и это отдельная просьба Ильи 28.08 про
        /// въезд из-за кромки, а не общее правило прихода.</para>
        /// </summary>
        public static float Settle(float p) => 1f - Mathf.Pow(1f - p, 3f);

        /// <summary>РАЗГОН ПРОЧЬ (InQuad) — так уходят: сорвалось с места и
        /// ушло. Зеркало <see cref="Settle"/>; врозь они и составляют правило
        /// «приход тормозит у цели, уход разгоняется».</summary>
        public static float Leave(float p) => p * p;

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
        /// ВЪЕЗД ИЗ-ЗА КРОМКИ: поверхность трогается еле заметно и разгоняется к
        /// месту.
        ///
        /// <para>Кубический разгон — просьба Ильи 28.08 («в начале медленно, в
        /// конце сильнее»): движение читается как ПОДЪЁМ, а не как выброс.
        /// Пружины здесь нет намеренно — полоса меню или страница, приехавшая с
        /// отскоком, читается как дёрганье интерфейса.</para>
        ///
        /// <para><paramref name="put"/> получает долю пути 0..1 и ставит
        /// поверхность в соответствующее место. Ставить её — работа звонящего:
        /// у полосы это проценты собственной высоты (пиксели в первом кадре ещё
        /// не посчитаны, и выезд шёл бы из ниоткуда), у страницы — ширина
        /// экрана влево.</para>
        ///
        /// <para>ЧЕМ БЫ ДВИЖЕНИЕ НИ КОНЧИЛОСЬ, ПОВЕРХНОСТЬ ВСТАЁТ НА МЕСТО:
        /// оборванная анимация (пересборка документа, смена темы посреди хода)
        /// однажды оставила верхнюю полосу за кромкой навсегда. Приём был
        /// записан трижды — у полосы, у нижнего меню и у страницы главной, — и
        /// страховка стояла не у всех.</para>
        /// </summary>
        public static void Enter(VisualElement el, int ms, Action<float> put)
        {
            if (el == null || put == null) return;
            put(0f);
            el.experimental.animation
              .Start(0f, 1f, Ms(ms), (e, p) => put(p * p * p))
              .OnCompleted(() => put(1f));
        }

        /// <summary>
        /// СЫГРАТЬ ДВИЖЕНИЕ И ДОЖДАТЬСЯ ЕГО КОНЦА.
        ///
        /// <para>Приём стоял дважды слово в слово — у перехода между вкладками и
        /// у пролёта промежуточного экрана: завести обещание, разрешить его на
        /// последнем кадре, ждать. И оба раза БЕЗ ПРЕДОХРАНИТЕЛЯ.</para>
        ///
        /// <para>Движение обрывается: поверхность вынули из дерева, документ
        /// пересобрали, сменили тему. Последнего кадра тогда не будет никогда —
        /// а ждущий останется ждать. У вкладок это хуже всего: там на время
        /// перехода поднят флаг «занято», и снимается он ПОСЛЕ ожидания.
        /// Оборванная анимация оставила бы флаг поднятым навсегда, и вкладки
        /// перестали бы переключаться до перезапуска.</para>
        ///
        /// <para>Поэтому ожидание всегда завершается: своим последним кадром,
        /// событием конца или предохранителем по часам. Довести поверхность до
        /// места — дело вызывающего, он и так делает это после ожидания.</para>
        /// </summary>
        public static Task PlayAsync(VisualElement el, int ms, Action<VisualElement, float> tick)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (el == null) { tcs.TrySetResult(true); return tcs.Task; }
            int span = Ms(ms);
            el.experimental.animation
              .Start(0f, 1f, span, (e, p) =>
              {
                  tick?.Invoke(e, p);
                  if (p >= 1f) tcs.TrySetResult(true);
              })
              .OnCompleted(() => tcs.TrySetResult(true));
            Lvn.LvnAsync.Fire(ReleaseLaterAsync(tcs, span), "MotionFailsafe");
            return tcs.Task;
        }

        /// <summary>Предохранитель ожидания: втрое дольше самого движения плюс
        /// запас. Считает РЕАЛЬНОЕ время — оборванная анимация обычно значит и
        /// вставшие кадры, а кадровый таймер встал бы вместе с ними.</summary>
        private static async Task ReleaseLaterAsync(TaskCompletionSource<bool> tcs, int span)
        {
            await Task.Delay(span * 3 + 250);
            tcs.TrySetResult(false);
        }

        /// <summary>ГАШЕНИЕ, КОТОРОГО МОЖНО ДОЖДАТЬСЯ.
        ///
        /// <para><see cref="FadeIn"/> объявляет переход и уходит — так делают
        /// плитки и надписи, которым никто не ждёт. Экрану же нужно ЗНАТЬ, что
        /// прозрачность доехала: бут-вуаль передаёт кадр следующему, всплывашка
        /// не снимается, пока не погасла. Отсюда второй вид — по кадру и с
        /// ожиданием.</para>
        ///
        /// <para>Жил он отдельным домом в один глагол (`ScreenFx`) и потому НЕ
        /// СПРАШИВАЛ ТЕМП: игрок включал «уменьшить движение», плитки начинали
        /// летать втрое быстрее, а девять экранных гашений оставались прежними.
        /// Настройка работала наполовину, и половина эта не называлась
        /// нигде.</para>
        ///
        /// <para>Отмена не бросает элемент на полпути: прозрачность ставится
        /// конечной. Незаконченное гашение оставляет экран полупрозрачным
        /// навсегда — это хуже, чем резкий, но целый кадр.</para></summary>
        public static async Task FadeAsync(VisualElement el, float from, float to,
                                           float seconds, CancellationToken ct)
        {
            if (el == null) return;
            seconds = Sec(seconds);   // темп — общий для всего движения
            if (seconds <= 0f) { el.style.opacity = to; return; }
            float t0 = LvnClock.Now();
            while (true)
            {
                if (ct.IsCancellationRequested) { el.style.opacity = to; return; }
                float t = Mathf.Clamp01(LvnClock.Since(t0) / seconds);
                t = t * t * (3f - 2f * t); // сглаженный ход: без рывка на концах
                el.style.opacity = Mathf.Lerp(from, to, t);
                if (t >= 1f) return;
                try { await Task.Yield(); }
                catch (System.OperationCanceledException) { el.style.opacity = to; return; }
            }
        }

        /// <summary>ТОЛЬКО ПРОЯВЛЕНИЕ, без сдвига и пружины (Илья 26.08:
        /// «убери эту убогую анимацию, делай фейд, прыжки убери везде»).
        /// Плитка, которая едет и пружинит, на списке читается как дёрганье —
        /// содержимое должно проступать, а не прыгать.</summary>
        public static void FadeIn(VisualElement el, int delayMs = 0, int ms = Reveal)
        {
            if (el == null) return;
            el.style.opacity = 0f;
            el.style.translate = new Translate(0, 0); // хвост прежних въездов
            el.schedule.Execute(() =>
            {
                el.style.transitionProperty = new List<StylePropertyName> { "opacity" };
                el.style.transitionDuration = new List<TimeValue> { new TimeValue(Ms(ms), TimeUnit.Millisecond) };
                el.style.transitionTimingFunction =
                    new List<EasingFunction> { new EasingFunction(EasingMode.EaseOutSine) };
                el.style.opacity = 1f;
            }).ExecuteLater(Mathf.Max(0, delayMs) + 16);
        }

        /// <summary>Проявить набор разом — без «волны» по элементам.</summary>
        public static void FadeInAll(IEnumerable<VisualElement> items, int ms = Reveal)
        {
            if (items == null) return;
            foreach (var el in items) FadeIn(el, 0, ms);
        }

        /// <summary>
        /// ПЛАВНАЯ СМЕНА СТИЛЯ. Подключает элементу декларативные переходы UITK:
        /// после этого любое присваивание перечисленных свойств (подсветка
        /// выбранного, перекраска пилюли, новая высота) едет кривой само, без
        /// единой строчки анимации на месте изменения.
        ///
        /// <para>Жило приватным методом внутри одного экрана, поэтому «плавно»
        /// умел ровно он: соседние экраны меняли цвет скачком. Дом у понятия
        /// один — здесь.</para>
        /// </summary>
        public static void Smooth(VisualElement el, int ms, params string[] props)
        {
            if (el == null || props == null || props.Length == 0) return;
            var list = new List<StylePropertyName>(props.Length);
            foreach (var p in props) list.Add(new StylePropertyName(p));
            el.style.transitionProperty = list;
            el.style.transitionDuration = new List<TimeValue>
                { new TimeValue(Ms(ms), TimeUnit.Millisecond) };
            el.style.transitionTimingFunction = new List<EasingFunction>
                { new EasingFunction(EasingMode.EaseOutCubic) };
        }

        /// <summary>Свойства, которыми карточка «переезжает» на новое место:
        /// прозрачность, сдвиг и цвет кромки. Набор общий, чтобы ленты в разных
        /// экранах вели себя одинаково.</summary>
        public static readonly string[] CardGlide =
        {
            "opacity", "translate", "border-top-color", "border-right-color",
            "border-bottom-color", "border-left-color",
        };

        /// <summary>
        /// «СКАЗАЛ И ВЕРНУЛ» — кнопка отвечает делом и через паузу возвращает
        /// свою надпись («Скопировать» → «Готово» → «Скопировать»).
        ///
        /// <para>Приём повторялся в шести экранах, каждый раз со своей паузой —
        /// от 1,2 до 4 секунд, — то есть один и тот же ответ интерфейса
        /// выглядел в разных местах по-разному. Пауза теперь из прайс-листа, а
        /// возврат берёт надпись, которая стояла на кнопке в момент вызова.</para>
        /// </summary>
        public static void FlashText(TextElement el, string message, int ms = Notice)
        {
            if (el == null) return;
            var was = el.text;
            el.text = message;
            el.schedule.Execute(() => { if (el.text == message) el.text = was; })
              .ExecuteLater(Ms(ms));
        }

        /// <summary>
        /// НЕ ПОДКЛЮЧЁН: ждёт авторской настройки. Естественный заказчик один —
        /// список выборов: он появляется мгновенно и лесенкой смотрелся бы
        /// живее. Но выбор — это ЭКРАН РЕШЕНИЯ: игрок уже готов нажать, и
        /// движение здесь скорее мешает, чем украшает. Такое включают полем
        /// манифеста (<c>ui.choices.appear</c>, как у диалога), а не молча.
        ///
        /// <para>Хореография: те же появления, но со сдвигом по времени. Первый
        /// элемент идёт сразу, каждый следующий — на <paramref name="stepMs"/>
        /// позже.</para>
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


        /// <summary>Класс-пометка «меня можно нажать». Кнопке не нужна — её
        /// узнаём по типу; ставится на всё остальное: вкладки, карточки,
        /// плашки, строки списка.</summary>
        public const string TapClass = "lvn-tap";

        /// <summary>Помечает элемент нажимаемым. Возвращает его же, чтобы
        /// вписываться в цепочку построения.</summary>
        public static T Tappable<T>(T el) where T : VisualElement
        {
            el?.AddToClassList(TapClass);
            return el;
        }

        // Что сейчас под пальцем. СТАТИЧЕСКОЕ поле, а не поле замыкания: отклик
        // можно включить сразу на нескольких корнях (оболочка + отдельный
        // экран), и без общего состояния один и тот же элемент отработал бы
        // дважды.
        private static VisualElement _pressed;
        private static Vector2 _pressAt;
        private static float _pressX = 1f, _pressY = 1f;

        /// <summary>
        /// ОТКЛИК НА НАЖАТИЕ для всего поддерева — одним вызовом.
        ///
        /// <para>Это самое дешёвое, что отличает живой интерфейс от мёртвого.
        /// Экран может быть безупречно свёрстан и правильно покрашен, но если
        /// палец опускается и НИЧЕГО не происходит, он читается как картинка:
        /// человек не понимает, нажалось ли, и жмёт второй раз. Подтверждение
        /// нужно в первые же миллисекунды — раньше, чем успеет отработать сам
        /// обработчик.</para>
        ///
        /// <para>Обработчик один и висит на корне, а не на каждой кнопке.
        /// Иначе про отклик пришлось бы помнить в каждом новом месте — и
        /// когда-нибудь забыть, что и произошло: во всей оболочке он стоял
        /// ровно на карточках хаба.</para>
        ///
        /// <para>Нажатие ставится МГНОВЕННО, а возврат идёт пружиной: так
        /// ведёт себя физический предмет, и так же — кнопки в системе.</para>
        /// </summary>
        public static void EnableTapFeedback(VisualElement root, float scale = 0.955f)
        {
            if (root == null) return;

            root.RegisterCallback<PointerDownEvent>(e =>
            {
                var el = FindTappable(e.target as VisualElement);
                if (el == null || ReferenceEquals(el, _pressed)) return;
                _pressed = el;
                _pressAt = e.position;
                // СКВОШ, а не равномерное уменьшение. Предмет, на который давят,
                // расплющивается: по вертикали сжимается сильнее, по горизонтали
                // почти не меняется. Равномерное уменьшение — это «объект стал
                // меньше», и читается оно именно как пластик.
                _pressX = 1f - (1f - scale) * 0.45f;
                _pressY = scale - 0.022f;
                el.style.scale = new Scale(new Vector2(_pressX, _pressY));
            }, TrickleDown.TrickleDown);

            // Уехал палец — отпускаем. Без этого элемент остаётся вдавленным на
            // всё время прокрутки, и лента едет с «залипшей» карточкой.
            root.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (_pressed == null) return;
                if ((((Vector2)e.position) - _pressAt).sqrMagnitude > 12f * 12f) Release();
            }, TrickleDown.TrickleDown);

            root.RegisterCallback<PointerUpEvent>(_ => Release(), TrickleDown.TrickleDown);
            root.RegisterCallback<PointerCancelEvent>(_ => Release(), TrickleDown.TrickleDown);
        }

        private static void Release()
        {
            var el = _pressed;
            _pressed = null;
            if (el == null) return;
            float fx = _pressX, fy = _pressY;
            Animate(el, 0, (t, x) =>
            {
                // Unclamped: пружина проскакивает единицу, и элемент на миг
                // становится ЧУТЬ БОЛЬШЕ исходного — это и есть отскок. Обрежь
                // здесь t по единице, и вся живость исчезнет.
                x.style.scale = new Scale(new Vector2(
                    Mathf.LerpUnclamped(fx, 1f, t),
                    Mathf.LerpUnclamped(fy, 1f, t)));
            }, DampingBouncy);
        }

        // Ищем ближайшего предка, который вообще нажимается. Глубина ограничена:
        // цель нажатия бывает вложена (иконка внутри кнопки внутри строки), но
        // не на двадцать уровней, а без ограничения одна промашка увела бы нас
        // до самого корня и вдавила бы весь экран.
        private static VisualElement FindTappable(VisualElement el)
        {
            for (int i = 0; el != null && i < 8; i++, el = el.parent)
                if (el is Button || el.ClassListContains(TapClass)) return el;
            return null;
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
