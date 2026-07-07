using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// The engine's default design tokens — <b>"Полночь"</b> (a Radix-Colors mauve
    /// neutral with a rose accent and a warm gold for currency/premium). Every
    /// screen's built-in colour/radius FALLBACK derives from here, so the whole app
    /// looks like one coherent product out of the box. Authors still override any
    /// value in <c>manifest.ui.*</c> — these are only the defaults the parse
    /// fallbacks resolve to when a field is absent.
    ///
    /// Swapping the whole look = editing this one file (or shipping a second preset).
    /// </summary>
    public static class LvnTokens
    {
        // Neutrals (Radix "mauve", dark) — plum-tinted so nothing reads as flat grey.
        public static readonly Color Bg        = Hex("#171119"); // app background
        public static readonly Color Surface   = Hex("#241a24"); // cards / tiles / panels
        public static readonly Color SurfaceHi = Hex("#2c2130"); // raised / hover
        public static readonly Color Border    = Hex("#38293a"); // hairline separators
        public static readonly Color Text      = Hex("#f6ecf1"); // primary text
        public static readonly Color TextDim   = Hex("#b79caf"); // secondary text
        public static readonly Color Faint     = new Color(1f, 1f, 1f, 0.08f); // ghost button fill

        // Accent (rose) + the ink that sits on it.
        public static readonly Color Accent   = Hex("#ec5a92");
        public static readonly Color OnAccent = Hex("#1a0f16");

        // Warm gold — currency amounts, premium chips, the "buy" call to action.
        public static readonly Color Gold     = Hex("#f0d9a0");

        // Overlays.
        public static readonly Color Scrim   = new Color(0f, 0f, 0f, 0.72f);
        public static readonly Color PanelBg = new Color(0.086f, 0.063f, 0.094f, 0.97f); // dialogue/sheet fill

        public const float Radius   = 16f; // cards / sheets
        public const float RadiusSm = 12f; // buttons / chips

        private static Color Hex(string h)
        {
            ColorUtility.TryParseHtmlString(h, out var c);
            return c;
        }
    }
}
