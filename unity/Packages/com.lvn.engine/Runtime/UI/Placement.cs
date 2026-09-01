using UnityEngine;

namespace Lvn.UI
{
    public enum TransitionType
    {
        None,
        Fade,
        SlideLeft,
        SlideRight,
        Pop,
        // Всплывает из глубины и утопает обратно — общий вид движка
        // (см. LvnAppear): телефон лежит на столе, персонаж выходит из-под
        // стекла, а не «включается».
        Rise,
        Drop,
        Unfold,
        // КИНЕМАТОГРАФИЧНЫЙ БОКОВОЙ УХОД: фед + малый снос к ближнему краю
        // (правый персонаж уходит вправо, левый — влево; направление берётся из
        // позиции, автору его писать не нужно). У составных героев поверх идёт
        // направленная волна проявления в шейдере.
        Drift,
        // ШЕЙДЕРНОЕ РАСТВОРЕНИЕ: спрайт съедается шумом со светящейся кромкой
        // (LvnSpriteFx, тот же _Dissolve, что у опа `sfx`). Отличается от Fade
        // по сути: там персонаж становится прозрачным целиком, здесь — исчезает
        // кусками, как сгорающая плёнка. Работает только на канвас-сцене: у
        // элемента UI Toolkit нет материала, а значит и шейдера.
        Dissolve,
    }

    /// <summary>
    /// Where to put a stage object, all in screen fractions so a script controls
    /// it without knowing the resolution: the object's <see cref="AnchorX"/>/
    /// <see cref="AnchorY"/> point (0..1 of the object) is placed at
    /// <see cref="X"/>/<see cref="Y"/> (0..1 of the screen), sized by
    /// <see cref="Width"/>/<see cref="Height"/>, ordered by <see cref="Z"/>, with
    /// optional <see cref="Flip"/>, <see cref="Rotation"/> and <see cref="Opacity"/>.
    /// Defaults give the classic standing character: bottom-centre anchored.
    /// </summary>
    public struct Placement
    {
        // Standard VN framing defaults (screen fractions): a large figure, bottom-
        // anchored (feet at the screen edge). ~1.5× the classic 0.46/0.62. A per-op
        // width=/height= overrides; ui.stage.actor_scale multiplies these.
        public const float DefaultWidth = 0.69f;
        public const float DefaultHeight = 0.93f;

        public bool Show;
        /// <summary>Lock the box to this width/height ratio (from the entity's
        /// <c>aspect</c>): the placed Width/Height become maximums and the box
        /// shrinks to match — required for layered/boned art registration.</summary>
        public float? BoxAspect;
        public float X, Y;          // screen position of the anchor point (0..1)
        public float? Width, Height; // size as a fraction of the screen (0..1)
        public float AnchorX, AnchorY;
        public int? Z;
        public bool Flip;
        public float Rotation;       // degrees
        public float Opacity;
        public float HoverOpacity;
        public TransitionType EnterTransition;
        public TransitionType ExitTransition;

        /// <summary>ПЕРЕХОД, ОТВЕЧАЮЩИЙ ЗА СМЕНУ ВИДИМОСТИ: вход, если фигуру
        /// показывают, иначе уход.
        ///
        /// <para>Выбор писали тернаркой на месте, и рядом — дважды, в ответ на
        /// РАЗНЫЕ вопросы: «есть ли зримый переход» и «какой именно играть».
        /// Вопросы разные, а выбор один, и жить ему у самой расстановки: она
        /// одна знает, показывают фигуру или уводят.</para></summary>
        public TransitionType VisibilityTransition => Show ? EnterTransition : ExitTransition;
        public float TransitionDuration;
        /// <summary>One-shot renderer hint: an already-visible actor received an
        /// explicit position/x/y command and should tween to it. Never persist it.</summary>
        public bool SmoothPosition;
        /// <summary>One-shot renderer hint: this visual rebuild came from live
        /// wardrobe preview and may use the dedicated outfit-flow shader.</summary>
        public bool WardrobeSwap;
        /// <summary>Hair flows from the head downward; clothing keeps the
        /// default feet-up reveal.</summary>
        public bool WardrobeFromTop;

        /// <summary>«Силуэт-проявление»: слои — это @mini-заготовка опоздавшего
        /// на медленной сети арта; рисуются затемнёнными, полный арт проявит их
        /// штатным кроссфейдом облика.</summary>
        public bool Silhouette;

        /// <summary>ГДЕ ВНУТРИ ХОЛСТА ЖИВЁТ САМА ФИГУРА — доли холста
        /// (x/y от левого-верхнего угла, w/h — размер), из <c>content</c>
        /// каталога. Холст персонажа почти никогда не равен персонажу: художник
        /// оставляет воздух по бокам, и у одного героя его 1%, а у другого 23%.
        /// Пока ширину мерил холст, один и тот же <c>w=</c> давал разный рост —
        /// «героиня маленькая» это не поза, это поля в png. По ширине размер и
        /// зажим у края считаются по фигуре, а якорь («ноги», «центр») ищется
        /// внутри неё, чтобы персонаж с полем под ногами не висел над полом.
        /// Высота намеренно остаётся долей КАДРА: воздух над головой — это рост,
        /// см. WorldPlacement. Ноль/пусто = данных нет, фигурой считается весь
        /// холст (прежнее поведение).</summary>
        public float ContentX, ContentY, ContentW, ContentH;

        /// <summary>РОСТ ФИГУРЫ В МЕТРАХ — общая шкала мира вместо доли экрана.
        /// Когда задан (командой <c>meters=</c> или ростом персонажа в
        /// каталоге), он и решает размер: высота фигуры на экране =
        /// метры ÷ высота сцены в метрах (<see cref="LvnScale"/>), а
        /// <c>Width</c>/<c>Height</c> уступают. Ноль — прежние доли экрана.
        /// Рост меряется по ФИГУРЕ, поэтому 1.7 и 1.9 встают рядом с настоящей
        /// разницей, сколько бы воздуха ни оставил художник вокруг каждого.</summary>
        public float Meters;

        /// <summary>Доля холста, занятая фигурой по ширине (1, когда данных нет).</summary>
        public float FigureW => ContentW > 0f && ContentW <= 1f ? ContentW : 1f;
        /// <summary>Доля холста, занятая фигурой по высоте (1, когда данных нет).</summary>
        public float FigureH => ContentH > 0f && ContentH <= 1f ? ContentH : 1f;
        /// <summary>Отступ фигуры от левого края холста в долях холста.</summary>
        public float FigureX => ContentW > 0f && ContentW <= 1f ? Mathf.Clamp01(ContentX) : 0f;
        /// <summary>Отступ фигуры от верха холста в долях холста.</summary>
        public float FigureY => ContentH > 0f && ContentH <= 1f ? Mathf.Clamp01(ContentY) : 0f;

        public static Placement Standing(float x) => new Placement
        {
            Show = true, X = x, Y = 1f, AnchorX = 0.5f, AnchorY = 1f, Opacity = 1f,
        };

        /// <summary>
        /// ИМЕНОВАННЫЕ МЕСТА ПО ГОРИЗОНТАЛИ — словарь ЯЗЫКА, а не таблица слоя
        /// отрисовки. Привычные VN-слоты от дальнего левого до дальнего правого
        /// плюс два ЗА кадром. Автор может ими не пользоваться и дать явную
        /// долю x.
        ///
        /// <para>Отвечали на это шестью списками и четырьмя разными словарями.
        /// Здесь знали `center_left` и `center_right`, но не знали
        /// `offscreen_left` — а его подсказывал редактор и принимал компилятор,
        /// и актёр, которого автор увёл ЗА кадр, вставал в ЦЕНТР экрана. Оба
        /// компилятора, наоборот, не знали про `center_left`: слово молча
        /// становилось ЭМОЦИЕЙ, и герой получал не место, а несуществующую
        /// эмоцию. Список стандартных мест для расталкивания толпы был выписан
        /// теми же числами ещё раз, руками.</para>
        /// </summary>
        public static readonly string[] SlotNames =
        {
            "offscreen_left", "far_left", "left", "center_left", "center",
            "center_right", "right", "far_right", "offscreen_right",
        };

        /// <summary>Места, где можно СТОЯТЬ, — по возрастанию x. Заэкранные сюда
        /// не входят: расталкивая толпу, никого нельзя вытолкнуть из кадра.</summary>
        public static readonly float[] StandingSlotXs =
        {
            0.12f, 0.25f, 0.38f, 0.50f, 0.62f, 0.75f, 0.88f,
        };

        public static float SlotX(string position)
        {
            switch (position)
            {
                // За кадром: доля НАМЕРЕННО вне [0,1] — фигура уходит целиком,
                // а не прижимается к краю.
                case "offscreen_left": return -0.25f;
                case "far_left": return 0.12f;
                case "left": return 0.25f;
                case "center_left": return 0.38f;
                case "center": return 0.50f;
                case "center_right": return 0.62f;
                case "right": return 0.75f;
                case "far_right": return 0.88f;
                case "offscreen_right": return 1.25f;
                default: return 0.50f;
            }
        }
    }
}
