using System.Collections.Generic;

namespace Lvn.UI
{
    /// <summary>
    /// НА КАКОМ ЯЗЫКЕ ИДЁТ ИГРА — выбор, его варианты и их имена.
    ///
    /// <para>Работа была растащена на четверых. Настройки оболочки строили ряд
    /// из «пустой строки и каталогов». Меню внутри главы предлагало ТОТ ЖЕ
    /// выбор кнопкой-циклом, со своим правилом перебора. Хост при загрузке
    /// манифеста сам смотрел на язык устройства и ЗАПИСЫВАЛ его в выбор игрока.
    /// Имена языков лежали в хранилище настроек — там, где хранят числа
    /// громкости.</para>
    ///
    /// <para>Из-за записи «за игрока» пропадала разница между «не выбирал» и
    /// «выбрал сам»: после первого же запуска на английском телефоне выбор
    /// выглядел сделанным, и вернуться к «как в системе» было нечем — такого
    /// варианта в ряду просто не было. Автоподстановка работала ровно один раз
    /// в жизни установки.</para>
    ///
    /// <para>Здесь <b>«Авто»</b> — полноправный вариант, а не отсутствие
    /// выбора: он хранится как выбор и означает «спроси устройство сейчас».
    /// Игрок может уйти в английский и вернуться в авто, а сменив язык
    /// телефона — увидеть игру на нём.</para>
    ///
    /// <para>Три разных ответа не путать:</para>
    /// <list type="bullet">
    /// <item><b>Выбор</b> (<see cref="Chosen"/>) — что стоит в настройках:
    /// «авто», «оригинал» или код языка.</item>
    /// <item><b>Действующий</b> (<see cref="Effective"/>) — на каком языке
    /// показывать СЕЙЧАС: «авто» уже разрешён в код или в оригинал.</item>
    /// <item><b>Оригинал</b> — пустая строка: текст, как его написал автор.</item>
    /// </list>
    /// </summary>
    public static class LvnLocale
    {
        /// <summary>«Как в системе». Хранится как обычный выбор — потому и
        /// обратим, в отличие от прежней разовой автоподстановки.</summary>
        public const string Auto = "auto";

        /// <summary>Язык оригинала — как написал автор.</summary>
        public const string Original = "";

        /// <summary>Что выбрано в настройках. Умолчание — «авто»: на новом
        /// устройстве игра открывается на языке телефона, если он у новеллы
        /// есть.</summary>
        public static string Chosen
        {
            get => LvnPrefs.LocaleChosen ? LvnPrefs.Locale : Auto;
            set => LvnPrefs.Locale = value ?? Auto;
        }

        /// <summary>На каком языке показывать сейчас: «авто» разрешено в код
        /// системы, если у новеллы есть такой каталог, иначе в оригинал.
        /// Незнакомый код (каталог убрали из манифеста) — тоже оригинал: лучше
        /// авторский текст, чем ключи вместо реплик.</summary>
        public static string Effective
        {
            get
            {
                var chosen = Chosen;
                if (chosen != Auto)
                    return chosen == Original || Has(chosen) ? chosen : Original;
                var sys = LvnDeviceProfile.SystemLocale;
                return !string.IsNullOrEmpty(sys) && Has(sys) ? sys : Original;
            }
        }

        /// <summary>Варианты для ряда настроек — в том порядке, в каком их
        /// читают: сначала «авто», потом оригинал, потом переводы. «Авто»
        /// показывается, только если новелле есть что предложить системному
        /// языку: иначе это вариант, который ничего не меняет, а объяснить
        /// игроку, почему кнопка бездействует, нечем.</summary>
        public static IReadOnlyList<string> Options()
        {
            var list = new List<string>();
            var sys = LvnDeviceProfile.SystemLocale;
            if (!string.IsNullOrEmpty(sys) && Has(sys)) list.Add(Auto);
            list.Add(Original);
            var have = LvnPrefs.AvailableLocales;
            if (have != null)
                foreach (var code in have)
                    if (!string.IsNullOrEmpty(code) && !list.Contains(code)) list.Add(code);
            return list;
        }

        /// <summary>Имя варианта, каким его видит игрок. «Авто» называет и
        /// язык, который получится: «Авто (English)» — иначе выбор выглядит
        /// как отказ от выбора.</summary>
        public static string Title(string code)
        {
            if (code == Auto)
            {
                var sys = LvnDeviceProfile.SystemLocale;
                var auto = Lvn.Content.LvnWords.Of("settings.language_auto", "Auto");
                return string.IsNullOrEmpty(sys) || !Has(sys)
                    ? auto : $"{auto} ({Name(sys)})";
            }
            return Name(code);
        }

        /// <summary>Следующий вариант по кругу — для кнопки-цикла во
        /// внутриигровом меню. Круг один и тот же, что и список в настройках:
        /// раньше меню перебирало свой, без «авто».</summary>
        public static string Next(string current)
        {
            var opts = Options();
            if (opts.Count == 0) return Original;
            for (int i = 0; i < opts.Count; i++)
                if (opts[i] == current) return opts[(i + 1) % opts.Count];
            return opts[0];
        }

        // Есть ли у новеллы каталог на этом языке.
        private static bool Has(string code)
        {
            var have = LvnPrefs.AvailableLocales;
            if (have == null || string.IsNullOrEmpty(code)) return false;
            for (int i = 0; i < have.Count; i++)
                if (have[i] == code) return true;
            return false;
        }

        // Человеческое имя языка. Оригинал зовётся своим языком («Русский»), а
        // не словом «Оригинал»: игрок выбирает язык, а не служебный термин.
        private static string Name(string code) => LvnPrefs.LocaleTitle(code);
    }
}
