using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// ПАСПОРТ УСТРОЙСТВА — одна точка правды о железе и системе (решение
    /// Ильи 25.08: «просмотр устройства выдели отдельным модулем»). Всё, что
    /// раньше решалось inline-эвристиками по месту (ступень арта, кап кадров,
    /// язык по системе), читает отсюда; хост дополнительно отправляет
    /// снимок в серверный профиль игрока — как это делают все крупные
    /// аналитики (Firebase/Amplitude шлют device model/os/screen автоматом):
    /// саппорт и сегменты видят, НА ЧЁМ играет человек.
    /// </summary>
    public static class LvnDeviceProfile
    {
        /// <summary>Большая сторона экрана в физических пикселях.</summary>
        public static int ScreenPx => Mathf.Max(Screen.width, Screen.height);

        public static int RamMb => SystemInfo.systemMemorySize;

        public static float RefreshHz => (float)Screen.currentResolution.refreshRateRatio.value;

        public static string Model => SystemInfo.deviceModel;

        public static string Os => SystemInfo.operatingSystem;

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
        public static string RecommendedArtQuality()
        {
            if (ScreenPx >= 2000 && RamMb >= 4096) return "2k";
            if (ScreenPx >= 1400 && RamMb >= 3072) return "1440";
            return "1k";
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
