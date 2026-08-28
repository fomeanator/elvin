using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;
using FontAsset = UnityEngine.TextCore.Text.FontAsset;

namespace Lvn.UI
{
    /// <summary>
    /// The engine's font pipeline: legacy <see cref="Font"/> references (theme
    /// fonts from Resources, downloaded .ttf files) are wrapped ONCE into
    /// TextCore SDF <see cref="FontAsset"/>s and applied via
    /// <c>unityFontDefinition</c> — the modern UITK text path (crisp under panel
    /// scaling, fallback-capable), replacing the legacy non-SDF
    /// <c>style.unityFont</c> route.
    ///
    /// Wrapped assets are DYNAMIC (glyphs rasterize on first use), so dialogue
    /// text must be pre-warmed at chapter load via <see cref="Prewarm"/> —
    /// otherwise the typewriter pays a rasterization hitch on every new glyph.
    /// </summary>
    public static class LvnFonts
    {
        private static readonly Dictionary<Font, FontAsset> _wrapped = new Dictionary<Font, FontAsset>();
        private static readonly Dictionary<string, Font> _fromFile = new Dictionary<string, Font>();

        /// <summary>The SDF FontAsset for a legacy Font (cached; null when the
        /// wrap fails — callers then fall back to the legacy path). Every wrapped
        /// asset gets the shared OS fallback chain, so a theme font that lacks
        /// Cyrillic/CJK still renders those runs instead of tofu.</summary>
        public static FontAsset From(Font font)
        {
            if (font == null) return null;
            if (_wrapped.TryGetValue(font, out var fa)) return fa;
            try { fa = FontAsset.CreateFontAsset(font); }
            catch { fa = null; }
            if (fa != null && _osFallbacks != null)
                try { fa.fallbackFontAssetTable = _osFallbacks; } catch { }   // шрифт не обернулся в SDF — ниже запасной путь
            _wrapped[font] = fa; // cache failures too — don't retry every label
            KickOsFallbacks(); // built in the background, attached when ready
            return fa;
        }

        // Script-coverage fallbacks built from fonts the OS ships (nothing added
        // to the build): a broad Latin+Cyrillic face per platform, then CJK.
        // Built ONE FONT PER FRAME in the background — creating six SDF assets
        // synchronously froze the first frame that touched any font. Colour
        // emoji are deliberately absent — bitmap emoji don't survive the SDF
        // pipeline; they come later via a sprite asset.
        private static List<FontAsset> _osFallbacks; // null until the builder finishes
        private static bool _osKicked;

        private static void KickOsFallbacks()
        {
            if (_osKicked) return;
            _osKicked = true;
            LvnAsync.Fire(BuildOsFallbacksAsync(), "BuildOsFallbacks");
        }

        private static async System.Threading.Tasks.Task BuildOsFallbacksAsync()
        {
            var list = new List<FontAsset>();
            foreach (var name in new[]
                     {
                         "Roboto", "Helvetica Neue", "Arial",          // Latin + Cyrillic
                         "PingFang SC", "Noto Sans CJK SC", "Yu Gothic" // CJK (when present)
                     })
            {
                await System.Threading.Tasks.Task.Yield(); // one asset per frame — no spike
                try
                {
                    var os = Font.CreateDynamicFontFromOSFont(name, 90);
                    if (os == null) continue;
                    var fa = FontAsset.CreateFontAsset(os);
                    if (fa != null) list.Add(fa);
                }
                catch { /* missing on this OS — next candidate */ }
            }
            _osFallbacks = list;
            // Late-attach to every font wrapped before the chain was ready.
            foreach (var kv in _wrapped)
                if (kv.Value != null)
                    try { kv.Value.fallbackFontAssetTable = list; } catch { }   // файл шрифта не читается — останется гарнитура панели
        }

        /// <summary>A Font loaded from a file on disk (downloaded/StreamingAssets
        /// locale packs) — never Resources. Cached per path.</summary>
        public static Font FromFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (_fromFile.TryGetValue(path, out var f)) return f;
            try { f = new Font(path); }
            catch { f = null; }
            _fromFile[path] = f;
            return f;
        }

        // ── ШРИФТ ТЕМЫ ──────────────────────────────────────────────────
        // Один вызов на корень — и всё дерево получает шрифт: unityFontDefinition
        // наследуется вниз. Иначе шрифт пришлось бы ставить в каждом экране, и
        // ровно один из них всегда оказывался бы забытым.
        private static Font _default;
        private static string _defaultPath;

        /// <summary>Шрифт действующей темы (загружается один раз на путь).</summary>
        public static Font Default
        {
            get
            {
                var path = LvnFonts.PathFor(LvnTheme.Current != null ? LvnTheme.Current.FontPath : null);
                if (string.IsNullOrEmpty(path)) return null;
                if (_default != null && _defaultPath == path) return _default;
                _defaultPath = path;
                _default = Resources.Load<Font>(path);
                return _default;
            }
        }

        /// <summary>Шрифт заголовков — тот же гарнитуры, но тяжёлый.</summary>
        /// <summary>
        /// ШРИФТ ДВИЖКА ИЗ КОРОБКИ — Onest (OFL, лежит в пакете): у него родная
        /// кириллица, и он не безликий. Системный Arial выдаёт «сделано на
        /// коленке» раньше, чем прочитан текст.
        ///
        /// <para>Путь стоял умолчанием в ДВУХ темах — сцены и оболочки, — и
        /// совпадал он по договорённости, а не по устройству. Разойдись они на
        /// одну правку, и диалог с меню поехали бы разными начертаниями:
        /// заметно сразу, а искать пришлось бы в двух файлах, где написано одно
        /// и то же.</para>
        /// </summary>
        public const string EngineFontPath = "Fonts/Onest-Regular";

        /// <summary>Заголовочное начертание того же семейства.</summary>
        public const string EngineDisplayPath = "Fonts/Onest-ExtraBold";

        // ── гарнитуры на выбор игрока ────────────────────────────────────────

        /// <summary>Гарнитура из каталога: чем набирать и как её назвать
        /// игроку.</summary>
        public readonly struct Family
        {
            public readonly string Id;       // ключ в настройках
            public readonly string Title;    // подпись в списке
            public readonly string Path;     // текст
            public readonly string Display;  // заголовки
            public Family(string id, string title, string path, string display)
            { Id = id; Title = title; Path = path; Display = display; }
        }

        /// <summary>
        /// ПЯТЬ ГАРНИТУР, И КАЖДАЯ ЗАЧЕМ-ТО.
        ///
        /// <para>Набор не «побольше вариантов», а разные ответы на «чем читать
        /// длинный текст с телефона»: нейтральный гротеск, интерфейсный,
        /// русский по происхождению, книжный засечный и геометричный. Все — с
        /// родной кириллицей и по свободной лицензии (OFL, тексты в
        /// Third-Party-Notices.md): шрифт без кириллицы в русской новелле
        /// показывает не текст, а квадраты.</para>
        ///
        /// <para>Заголовочное начертание есть только у Onest — у остальных
        /// заголовки набираются тем же файлом: переменные шрифты Google несут
        /// все веса в одном файле, и отдельный «жирный» им не нужен.</para>
        /// </summary>
        public static readonly Family[] Families =
        {
            new Family("onest",   "Onest",      "Fonts/Onest-Regular", "Fonts/Onest-ExtraBold"),
            new Family("inter",   "Inter",      "Fonts/Inter",         "Fonts/Inter"),
            new Family("golos",   "Golos Text", "Fonts/GolosText",     "Fonts/GolosText"),
            new Family("literata","Literata",   "Fonts/Literata",      "Fonts/Literata"),
            new Family("manrope", "Manrope",    "Fonts/Manrope",       "Fonts/Manrope"),
        };

        /// <summary>Гарнитура по ключу настройки; неизвестный ключ и пустой —
        /// первая (шрифт движка из коробки).</summary>
        public static Family FamilyOf(string id)
        {
            if (!string.IsNullOrEmpty(id))
                foreach (var f in Families)
                    if (f.Id == id) return f;
            return Families[0];
        }

        /// <summary>Что выбрал игрок. Пусто — гарнитура НОВЕЛЛЫ (тема), иначе
        /// выбор перекрывает её: подогнать шрифт под свои глаза важнее
        /// авторского вкуса, и это ровно та настройка, ради которой её
        /// просили.</summary>
        public static Family Chosen => FamilyOf(LvnPrefs.FontFamily);

        /// <summary>Выбрал ли игрок гарнитуру сам.</summary>
        public static bool PlayerPicked => !string.IsNullOrEmpty(LvnPrefs.FontFamily);

        /// <summary>Путь текстового шрифта с учётом выбора игрока: тема
        /// спрашивает ЗДЕСЬ, а не читает своё поле напрямую.</summary>
        public static string PathFor(string themePath)
            => PlayerPicked ? Chosen.Path : (string.IsNullOrEmpty(themePath) ? EngineFontPath : themePath);

        /// <summary>То же для заголовков.</summary>
        public static string DisplayPathFor(string themePath)
            => PlayerPicked ? Chosen.Display : (string.IsNullOrEmpty(themePath) ? EngineDisplayPath : themePath);

        /// <summary>Прежние имена умолчаний — их читают темы при сборке.</summary>
        public const string DefaultPath = EngineFontPath;
        public const string DefaultDisplayPath = EngineDisplayPath;

        private static Font _display;
        private static string _displayPath;
        public static Font Display
        {
            get
            {
                var path = LvnFonts.DisplayPathFor(LvnTheme.Current != null ? LvnTheme.Current.FontDisplayPath : null);
                if (string.IsNullOrEmpty(path)) return null;
                if (_display != null && _displayPath == path) return _display;
                _displayPath = path;
                _display = Resources.Load<Font>(path);
                return _display;
            }
        }

        /// <summary>Поставить шрифт темы на корень слоя.</summary>
        public static void ApplyDefault(VisualElement root) => Apply(root, Default);

        /// <summary>Apply a font to an element the modern way (SDF FontAsset via
        /// unityFontDefinition), falling back to the legacy assignment only when
        /// the wrap failed. Null font = no-op (theme/panel default applies).</summary>
        public static void Apply(VisualElement el, Font font)
        {
            if (el == null || font == null) return;
            var fa = From(font);
            if (fa != null) el.style.unityFontDefinition = new StyleFontDefinition(FontDefinition.FromSDFFont(fa));
            else el.style.unityFont = new StyleFont(font);
        }

        /// <summary>Rasterize every distinct character of <paramref name="text"/>
        /// into the font's atlas — SPREAD over frames (a whole chapter's corpus
        /// in one call froze the entry for hundreds of ms). Fire-and-forget: the
        /// first line may still rasterize a few glyphs on-reveal, but never the
        /// whole alphabet at once. Missing glyphs cascade into the same fallback
        /// assets the renderer will pick at draw time.</summary>
        public static void Prewarm(Font font, string text) => LvnAsync.Fire(PrewarmSpreadAsync(font, text), "PrewarmSpread");

        private static async System.Threading.Tasks.Task PrewarmSpreadAsync(Font font, string text, int charsPerFrame = 48)
        {
            if (font == null || string.IsNullOrEmpty(text)) return;
            var fa = From(font);
            if (fa == null) return;
            var distinct = new HashSet<char>();
            var sb = new StringBuilder(256);
            foreach (var c in text)
                if (!char.IsControl(c) && distinct.Add(c)) sb.Append(c);
            for (int i = 0; i < sb.Length; i += charsPerFrame)
            {
                var chunk = sb.ToString(i, System.Math.Min(charsPerFrame, sb.Length - i));
                string missing;
                try { fa.TryAddCharacters(chunk, out missing); }
                catch { return; /* atlas full / dynamic-OS font — render-time fallback covers it */ }
                if (!string.IsNullOrEmpty(missing) && _osFallbacks != null)
                    foreach (var fb in _osFallbacks)
                    {
                        try { fb.TryAddCharacters(missing, out missing); }
                        catch { break; }
                        if (string.IsNullOrEmpty(missing)) break;
                    }
                await System.Threading.Tasks.Task.Yield(); // one chunk per frame
            }
        }
    }
}
