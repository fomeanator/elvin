namespace Lvn.Services
{
    /// <summary>
    /// ИМЕНА СОБЫТИЙ — договор между игрой и отчётом.
    ///
    /// <para>Имя события это не строка, а СТЫК: клиент его пишет, сервер по нему
    /// сворачивает воронку, а человек читает вывод. Написаны они были голыми
    /// литералами в шестнадцати местах, и стык держался на том, что все
    /// шестнадцать совпадают с константами на другой стороне по памяти
    /// автора.</para>
    ///
    /// <para>Как такой договор расходится, видно по случившемуся: сервер годами
    /// носил пометки «not sent yet» у трёх событий, которые клиент давно шлёт, а
    /// отчёт воронки безусловно утверждал «клиент не шлёт chapter_abandon» и
    /// советовал читателю выводы по несуществующему миру. Ни одна сторона не
    /// солгала — просто никто не сверял.</para>
    ///
    /// <para>Ответственность: назвать события один раз. Опечатка становится
    /// ошибкой сборки, а не молча пустой метрикой; сверку сторон делает
    /// <c>TestAnalyticsNamesMatchTheServer</c> (Go, без Unity).</para>
    ///
    /// <para>Здесь НЕ живут свойства события и правила отправки — это
    /// <see cref="LvnAnalytics"/>. И здесь нет имён, которые придумывает АВТОР:
    /// метка конверсии из скрипта (<c>track "имя"</c>) приходит строкой из
    /// новеллы и договором движка не является.</para>
    /// </summary>
    public static class LvnEvents
    {
        // ── запуск и устройство ──────────────────────────────────────────────

        /// <summary>Игра поднялась.</summary>
        public const string Boot = "boot";

        /// <summary>Профиль устройства: экран, память, платформа.</summary>
        public const string Device = "device";

        /// <summary>Первый ИНТЕРАКТИВНЫЙ экран после загрузки — по нему меряют,
        /// сколько игроков не дождались.</summary>
        public const string FirstScreen = "first_screen";

        // ── глава ────────────────────────────────────────────────────────────

        /// <summary>Вошёл в главу.</summary>
        public const string ChapterStart = "chapter_start";

        /// <summary>Дочитал главу до конца.</summary>
        public const string ChapterFinish = "chapter_finish";

        /// <summary>Вышел из главы посреди неё. Без этого события уход и крах
        /// неразличимы: оба выглядят как «начал и не кончил».</summary>
        public const string ChapterAbandon = "chapter_abandon";

        /// <summary>Дошёл до авторской метки — слайд внутри главы.</summary>
        public const string LabelReach = "label_reach";

        // ── выбор ────────────────────────────────────────────────────────────

        /// <summary>Выбор показан игроку.</summary>
        public const string ChoiceShown = "choice_shown";

        /// <summary>Выбор сделан.</summary>
        public const string ChoicePick = "choice_pick";

        // ── сбои ─────────────────────────────────────────────────────────────

        /// <summary>Ассет не доехал: глава показывает серую болванку, а сессия
        /// без этого события выглядит счастливой.</summary>
        public const string AssetFail = "asset_fail";

        /// <summary>Сборка встретила op, которого не знает.</summary>
        public const string UnknownOp = "unknown_op";

        // ── деньги ───────────────────────────────────────────────────────────
        // Успешное списание в отчёт пишет СЕРВЕР (журнал кошелька, атомарно со
        // снятием денег). Клиент шлёт только то, чего в журнале быть не может.

        /// <summary>Не хватило, магазин предложен — игрок отказался.</summary>
        public const string SpendDeclined = "spend_declined";

        /// <summary>Не хватило даже после магазина.</summary>
        public const string SpendDenied = "spend_denied";

        /// <summary>Наряд куплен (по ИТОГУ обряда, а не по первой попытке).</summary>
        public const string WardrobeBuy = "wardrobe_buy";

        /// <summary>Наряд купить не вышло.</summary>
        public const string WardrobeBuyFail = "wardrobe_buy_fail";

        // ── реклама ──────────────────────────────────────────────────────────

        /// <summary>Ролик досмотрен, награда начислена.</summary>
        public const string AdReward = "ad_reward";

        /// <summary>Ролик не дал награды.</summary>
        public const string AdRewardFail = "ad_reward_fail";
    }
}
