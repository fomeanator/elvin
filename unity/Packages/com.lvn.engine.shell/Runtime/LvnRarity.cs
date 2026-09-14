using System.Collections.Generic;
using UnityEngine;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// РЕДКОСТЬ ПРЕДМЕТА — шесть ступеней «как в Доте» (TR-109, Илья 15.09):
    /// от обычного до бессмертного, у каждой свой цвет. Ключ хранится в
    /// манифесте у предмета гардероба и у фона; цвета можно переопределить в
    /// <c>ui.wardrobe.rarity_colors</c>, без них — эти. Слова — из словаря
    /// (<c>rarity.*</c>). Один дом на гардероб, крутки и церемонию.
    /// </summary>
    public static class LvnRarity
    {
        public static readonly string[] Order = { "common", "uncommon", "rare", "mythical", "legendary", "immortal" };

        private static readonly Dictionary<string, string> Hex = new Dictionary<string, string>
        {
            ["common"] = "#9d9d9d", ["uncommon"] = "#5e98d9", ["rare"] = "#4b69ff",
            ["mythical"] = "#8847ff", ["legendary"] = "#d32ce6", ["immortal"] = "#e4ae39",
        };

        private static readonly Dictionary<string, string> English = new Dictionary<string, string>
        {
            ["common"] = "Common", ["uncommon"] = "Uncommon", ["rare"] = "Rare",
            ["mythical"] = "Mythical", ["legendary"] = "Legendary", ["immortal"] = "Immortal",
        };

        /// <summary>Порядок ступени: 0 — обычный … 5 — бессмертный; −1 — нет редкости.</summary>
        public static int Rank(string key)
        {
            if (string.IsNullOrEmpty(key)) return -1;
            for (int i = 0; i < Order.Length; i++) if (Order[i] == key) return i;
            return -1;
        }

        /// <summary>Цвет ступени: палитра манифеста, иначе цвет по умолчанию, иначе приглушённый.</summary>
        public static Color ColorOf(string key, IReadOnlyDictionary<string, string> palette = null)
        {
            if (string.IsNullOrEmpty(key)) return LvnTokens.TextDim;
            if (palette != null && palette.TryGetValue(key, out var hex) && !string.IsNullOrEmpty(hex))
                return UiColor.Named(hex, LvnTokens.TextDim);
            return Hex.TryGetValue(key, out var def) ? UiColor.Named(def, LvnTokens.TextDim) : LvnTokens.TextDim;
        }

        /// <summary>Название ступени словами словаря.</summary>
        public static string Word(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            return Lvn.Content.LvnWords.Of("rarity." + key, English.TryGetValue(key, out var en) ? en : key);
        }
    }
}
