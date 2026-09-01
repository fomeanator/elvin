using UnityEngine;

namespace Lvn
{
    /// <summary>
    /// ПАСПОРТ УСТРОЙСТВА — одна точка правды о железе и системе (решение
    /// Ильи 25.08: «просмотр устройства выдели отдельным модулем»). Всё, что
    /// раньше решалось inline-эвристиками по месту (ступень арта, кап кадров,
    /// язык по системе), читает отсюда; хост дополнительно отправляет
    /// снимок в серверный профиль игрока — как это делают все крупные
    /// аналитики (Firebase/Amplitude шлют device model/os/screen автоматом):
    /// саппорт и сегменты видят, НА ЧЁМ играет человек.
    ///
    /// <para>Живёт в ЯДРЕ, а не в интерфейсном слое: про железо спрашивают все —
    /// кэш картинок считает бюджет по памяти, загрузчик спрашивает про формат
    /// текстур, отчёты называют модель и систему. Пока паспорт лежал в UI, до
    /// него не дотягивался слой контента, и он читал железо напрямую; отчёты,
    /// у которых не хватало пары полей (видеочип, номер устройства), заодно
    /// брали напрямую и всё остальное.</para>
    /// </summary>
    public static class LvnDeviceProfile
    {
        /// <summary>Большая сторона экрана в физических пикселях.</summary>
        public static int ScreenPx => Mathf.Max(Screen.width, Screen.height);

        public static int RamMb => SystemInfo.systemMemorySize;

        public static float RefreshHz => (float)Screen.currentResolution.refreshRateRatio.value;

        public static string Model => SystemInfo.deviceModel;

        public static string Os => SystemInfo.operatingSystem;

        /// <summary>Видеочип — им объясняются «полосы на земле» и просевший
        /// кадр там, где на соседнем телефоне всё ровно.</summary>
        public static string Gpu => SystemInfo.graphicsDeviceName;

        /// <summary>Опознавательный номер устройства. Нужен отчётам, чтобы
        /// склеить между собой логи, жалобу и сессию одного человека.</summary>
        public static string DeviceId => SystemInfo.deviceUniqueIdentifier;

        /// <summary>Тянет ли устройство такой формат текстуры. Вопрос к железу,
        /// а не решение: ВКЛЮЧАТЬ ли формат — отдельное правило, и живёт оно у
        /// того, кто грузит картинки.</summary>
        public static bool SupportsFormat(UnityEngine.TextureFormat format)
            => SystemInfo.SupportsTextureFormat(format);

        /// <summary>Язык системы кодом ISO ("ru", "en", …); "" — не определён.</summary>
        public static string SystemLocale
        {
            get
            {
                switch (Application.systemLanguage)
                {
                    case SystemLanguage.Russian: return "ru";
                    case SystemLanguage.English: return "en";
                    case SystemLanguage.Ukrainian: return "uk";
                    case SystemLanguage.German: return "de";
                    case SystemLanguage.French: return "fr";
                    case SystemLanguage.Spanish: return "es";
                    case SystemLanguage.Portuguese: return "pt";
                    case SystemLanguage.Italian: return "it";
                    case SystemLanguage.Turkish: return "tr";
                    case SystemLanguage.Polish: return "pl";
                    case SystemLanguage.Japanese: return "ja";
                    case SystemLanguage.Korean: return "ko";
                    case SystemLanguage.Chinese:
                    case SystemLanguage.ChineseSimplified:
                    case SystemLanguage.ChineseTraditional: return "zh";
                    default: return "";
                }
            }
        }

        /// <summary>Рекомендуемая ступень арта (как App Thinning у сторов):
        /// большой экран с запасом памяти — 2K, средний — 1440p, иначе 1K.</summary>
        // КАКОМУ ТЕЛЕФОНУ КАКОЙ АРТ — продуктовое решение, а не деталь: от него
        // зависит, влезет ли игра в память дешёвого устройства и не будет ли
        // дорогое показывать мыло. Пороги стояли четырьмя безымянными числами
        // прямо в условии, и обсудить «а не поднять ли планку» было не с чем.
        //
        // Читаются они парами «экран И память»: крупный экран при малой памяти
        // не даёт права на крупный арт — именно такие устройства и падают на
        // распакованных текстурах.

        /// <summary>Планка крупного арта (@2k): флагманский экран и память,
        /// где полноразмерные текстуры живут спокойно.</summary>
        private const int HighScreenPx = 2000;
        private const int HighRamMb = 4096;

        /// <summary>Планка среднего арта (@1440): экран уже плотный, но памяти
        /// на @2k не хватит.</summary>
        private const int MidScreenPx = 1400;
        private const int MidRamMb = 3072;

        public static string RecommendedArtQuality()
        {
            if (ScreenPx >= HighScreenPx && RamMb >= HighRamMb) return "2k";
            if (ScreenPx >= MidScreenPx && RamMb >= MidRamMb) return "1440";
            return "1k";   // всё остальное, включая телефоны на 500–1000 МБ
        }

        /// <summary>Кап кадров по экрану: просить 60 у 30-герцовой панели
        /// бессмысленно.</summary>
        public static int FpsCap() => RefreshHz >= 59f ? 60 : 30;

        /// <summary>Снимок для серверного профиля/аналитики — плоские пары,
        /// готовые лечь в свойства события.</summary>
        public static (string key, object value)[] Snapshot() => new (string, object)[]
        {
            ("model", Model),
            ("os", Os),
            ("screen_px", ScreenPx),
            ("screen_w", Screen.width),
            ("screen_h", Screen.height),
            ("refresh_hz", Mathf.RoundToInt(RefreshHz)),
            ("ram_mb", RamMb),
            ("sys_locale", SystemLocale),
            ("rec_quality", RecommendedArtQuality()),
        };
    }
}
