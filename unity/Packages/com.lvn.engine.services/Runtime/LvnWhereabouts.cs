namespace Lvn.Services
{
    /// <summary>
    /// ГДЕ СЕЙЧАС ИГРОК — один ответ для всех, кто пишет о происходящем.
    ///
    /// <para>Контекст «новелла / глава / метка / номер команды» нужен каждому
    /// журналу: аналитике — чтобы событие можно было отнести к месту, жалобе
    /// игрока — чтобы понять, на что он жалуется, диагностике — чтобы читать
    /// лог. Держали его ДВОЕ: <see cref="LvnAnalytics"/> и
    /// <see cref="LvnFeedback"/>, у каждого свои поля с теми же именами.</para>
    ///
    /// <para>И это уже стоило данных: хост заполнял только поля аналитики, а
    /// поля обратной связи не заполнял НИКТО. Жалоба уходила на сервер без
    /// новеллы и главы — то есть без ответа на вопрос «о чём она». Ровно тот
    /// случай, когда состояние синхронизируют вручную и однажды забывают.</para>
    ///
    /// <para>Ответственность: знать место. Кто и что решает записать — дело
    /// самих журналов; здесь только «мы сейчас вот здесь».</para>
    /// </summary>
    public static class LvnWhereabouts
    {
        /// <summary>Новелла, в которой игрок сейчас. Пусто — он в меню.</summary>
        public static string Title { get; private set; }

        /// <summary>Глава. Пусто — вне главы.</summary>
        public static string Chapter { get; private set; }

        /// <summary>Последняя пройденная метка сюжета — куда именно он дошёл
        /// внутри главы.</summary>
        public static string Label { get; private set; }

        /// <summary>Номер команды на этой метке: две жалобы с одной метки
        /// различаются шагом.</summary>
        public static int At { get; private set; }

        /// <summary>Игрок вошёл в главу.</summary>
        public static void Enter(string title, string chapter)
        {
            Title = title;
            Chapter = chapter;
            Label = null;
            At = 0;
        }

        /// <summary>Игрок вышел из главы — место снова «меню». Метка гасится
        /// вместе с главой: метка без главы бессмысленна и в прошлый раз
        /// пережила бы выход, приклеившись к следующему событию.</summary>
        public static void Leave()
        {
            Title = null;
            Chapter = null;
            Label = null;
            At = 0;
        }

        /// <summary>Дошли до метки сюжета.</summary>
        public static void Mark(string label, int at)
        {
            Label = label;
            At = at;
        }
    }
}
