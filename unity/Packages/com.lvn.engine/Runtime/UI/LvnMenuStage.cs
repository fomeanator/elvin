using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// ВИТРИНА МЕНЮ — сцена, которая стоит ЗА интерфейсом хаба: широкое полотно
    /// и кукла героини перед ним.
    ///
    /// <para>Числа этой сцены — рост куклы, где стоит камера полотна, сколько
    /// она проезжает за вкладку — подбирались на глаз и оседали прямо в коде
    /// оболочки: «width 1.26», «0.35 + 0.14 × вкладка». Каждая следующая
    /// правка означала поиск нужной строки среди тысячи и подстановку нового
    /// числа наугад. Здесь у них дом: движковые значения покрывают обычную
    /// портретную игру, новелла перекрывает нужное через <c>ui.browse</c>, а
    /// поменять их можно и на лету.</para>
    ///
    /// <para>Граница с соседями простая: <see cref="VnTheme"/> — постановка
    /// НОВЕЛЛЫ, <see cref="LvnMotion"/> — время и физика движения, здесь —
    /// сцена под меню.</para>
    /// </summary>
    public static class LvnMenuStage
    {
        // ── кукла ─────────────────────────────────────────────────────────────

        /// <summary>Рост куклы: доля высоты кадра. 0.91 — фигура почти во весь
        /// экран, ногами на нижней кромке, головой под строкой состояния.</summary>
        public static float DollHeight = 0.91f;

        /// <summary>Ширина, в которую вписывается фигура (доля кадра). Единица
        /// означает «шире экрана не станет, но и не ужмётся ради полей»: ширину
        /// мерит сама фигура, а не холст её файла (см. Placement.ContentW).</summary>
        public static float DollWidth = 1f;

        /// <summary>Где стоит кукла по ширине кадра: слово места
        /// («left | center | right», см. <c>Placement.SlotX</c>) или доля
        /// 0..1. По центру — фигура на полке; облик «сцена» ставит её левее,
        /// оставляя правую половину панелям.</summary>
        public static string DollPlace = "center";

        /// <summary>
        /// РОСТ, КАКИМ КУКЛА ВСТАНЕТ НА ЭТОМ ЭКРАНЕ — не выше самого экрана.
        ///
        /// <para>Рост меряется долей КАДРА сцены, а кадр — это меньшее из
        /// высоты экрана и опорных 1920 (см. <c>WorldStage.ApplyPlacement</c>).
        /// На длинном телефоне кадр 1920 при экране 2338, и рост 1.05 (макет
        /// партнёра) умещается с запасом; на 16:9 и на планшете кадр равен
        /// экрану, и та же единица с лишним срезала бы голову. Зажим считает
        /// от экрана: выше него кукла не станет, ниже авторского роста — тоже.</para>
        /// </summary>
        public static float DollHeightOnScreen
        {
            get
            {
                float sw = Screen.width, sh = Screen.height;
                if (sw <= 1f || sh <= 1f || sw > sh) return Mathf.Min(DollHeight, 1f);
                float trueH = 1080f * sh / sw;                 // канвас width-match к 1080
                float frame = Mathf.Min(trueH, 1920f);         // кадр, от которого меряют рост
                // Над головой — вырез телефона и шапка оболочки: голова не
                // должна упираться в логотип, как упиралась на 16:9 и планшете.
                float safeTop = Mathf.Max(0f, (sh - LvnEdges.SafeArea.yMax) * 1080f / sw);
                float room = Mathf.Max(0f, trueH - safeTop - DollHeadroom);
                return Mathf.Clamp(Mathf.Min(DollHeight, room / frame), 0.1f, 3f);
            }
        }

        /// <summary>Сколько единиц холста над головой куклы занимает шапка
        /// оболочки (под вырезом). Ставит облик, у которого шапка есть; ноль —
        /// кукла вправе стоять до самого верха.</summary>
        public static float DollHeadroom = 0f;

        // ── полотно ───────────────────────────────────────────────────────────

        /// <summary>Где стоит камера на полотне при открытии меню (0 — левый
        /// край картины, 1 — правый). Первая вкладка живёт левее центра: справа
        /// остаётся место, куда полотно поедет.</summary>
        public static float PanStart = 0.35f;

        /// <summary>Сколько полотна проезжает камера за одну вкладку. Шаг
        /// прямо виден игроку: маленький — фон кажется неподвижным, большой —
        /// вкладки читаются как разные комнаты.</summary>
        public static float PanStep = 0.14f;

        /// <summary>Точка полотна для вкладки — единственная формула переезда,
        /// вместо повторения «0.35 + 0.14 × N» в двух местах оболочки.</summary>
        public static float PanFor(int tab, int tabs = 4)
            => Mathf.Clamp01(PanStart + PanStep * Mathf.Clamp(tab, 0, Mathf.Max(0, tabs - 1)));

        // ── самолечение ───────────────────────────────────────────────────────
        // Сцену меню собирает асинхронный тракт (сеть, декод, поколения), и
        // любое его звено может не доехать. Сторож сверяется с ФАКТОМ на
        // экране, а не с флагом «мы вроде поставили» — три бага 26–27.08 были
        // ровно про это. Числа здесь — терпение сторожа, не украшение.

        /// <summary>Как часто сторож смотрит на сцену, секунды.</summary>
        public static float GuardPeriodSeconds = 1f;

        /// <summary>Сколько сторож ждёт, прежде чем счесть пустое полотно
        /// поломкой, а не ещё-не-доехавшей загрузкой, секунды.</summary>
        public static float GuardPatienceSeconds = 2f;

        /// <summary>Сколько бут-вуаль ждёт первого кадра сцены, прежде чем
        /// открыть меню как есть, миллисекунды. Без ожидания игрок видел чёрный
        /// экран под интерфейсом; с бесконечным — висел бы на вуали.</summary>
        public static int VeilWaitMs = 1200;

        /// <summary>За сколько секунд вуаль растворяется, открывая меню.</summary>
        public static float VeilFadeSeconds = 0.4f;

        /// <summary>Применить настройки новеллы (<c>ui.browse</c>). Пустые поля
        /// оставляют движковый дефолт: манифест не обязан знать про эту
        /// механику, чтобы игра выглядела правильно.</summary>
        public static void Apply(float? dollHeight, float? dollWidth,
                                 float? panStart, float? panStep, string dollPlace = null)
        {
            if (dollHeight.HasValue) DollHeight = Mathf.Clamp(dollHeight.Value, 0.1f, 3f);
            if (dollWidth.HasValue) DollWidth = Mathf.Clamp(dollWidth.Value, 0.1f, 3f);
            if (panStart.HasValue) PanStart = Mathf.Clamp01(panStart.Value);
            if (panStep.HasValue) PanStep = Mathf.Clamp(panStep.Value, 0f, 0.5f);
            if (!string.IsNullOrEmpty(dollPlace)) DollPlace = dollPlace;
        }
    }
}
