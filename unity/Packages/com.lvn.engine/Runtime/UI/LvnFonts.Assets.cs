using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.TextCore.Text;

namespace Lvn.UI
{
    /// <summary>
    /// КАК ШРИФТ СТАНОВИТСЯ ПРИГОДНЫМ ДЛЯ ОТРИСОВКИ — обёртка, запасные,
    /// прогрев.
    ///
    /// <para>Файл шрифта сам по себе рисовать нечем: движку нужен SDF-ресурс,
    /// а он строится по файлу и стоит дорого — поэтому обёртки живут в пуле, по
    /// одной на файл. К ним прицепляются СИСТЕМНЫЕ ЗАПАСНЫЕ: гарнитура новеллы
    /// знает свои буквы, а эмодзи, иероглиф или редкий знак в имени игрока
    /// пришли бы пустым квадратом.</para>
    ///
    /// <para>ПРОГРЕВ — третье правило и самое незаметное. Обёртки
    /// ДИНАМИЧЕСКИЕ: глиф растеризуется при первом появлении, и печатающая
    /// машинка платит за это рывком ровно в тот момент, когда игрок читает.
    /// Поэтому текст главы прогревают заранее и по кусочкам, чтобы прогрев сам
    /// не стал рывком.</para>
    ///
    /// <para>Всё это — механизм. КАКУЮ гарнитуру взял игрок и что предложила
    /// новелла решает соседняя тема, поправку кегля считает третья.</para>
    /// </summary>
    public static partial class LvnFonts
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
            if (fa != null && fa.material != null)
                try { fa.material.SetFloat(FaceDilate, Mathf.Clamp01(LvnPrefs.TextWeight) * 0.12f); }
                catch { /* см. ApplyWeight: чужой шейдер молча остаётся как есть */ }
            _wrapped[font] = fa; // cache failures too — don't retry every label
            KickOsFallbacks(); // built in the background, attached when ready
            return fa;
        }
        private static void KickOsFallbacks()
        {
            if (_osKicked) return;
            _osKicked = true;
            LvnAsync.Fire(BuildOsFallbacksAsync(), "BuildOsFallbacks");
        }
        private static async System.Threading.Tasks.Task BuildOsFallbacksAsync()
        {
            var list = new List<FontAsset>();

            // СПРАШИВАЕМ У СИСТЕМЫ ТОЛЬКО ТО, ЧТО У НЕЁ ЕСТЬ. Раньше запасные
            // гарнитуры перебирались вслепую, а Unity на отсутствующую отвечает
            // не null, а объектом-пустышкой — и печатает ДВЕ ошибки в лог:
            // «Unable to find a font file [Roboto]» и «Unable to load font
            // face». На маке таких три из шести, то есть шесть красных строк на
            // каждом запуске. Ошибка, которая ничего не значит, дороже
            // молчания: она приучает не читать лог, и настоящая теряется среди
            // неё (живой лог Ильи, 28.08).
            var installed = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var f in Font.GetOSInstalledFontNames() ?? System.Array.Empty<string>())
                    if (!string.IsNullOrEmpty(f))
                    {
                        installed.Add(f);
                        installed.Add(f.Replace(" ", ""));   // «Helvetica Neue» ↔ «HelveticaNeue»
                    }
            }
            catch { /* платформа не отдаёт список — переберём вслепую, как раньше */ }

            foreach (var name in new[]
                     {
                         "Roboto", "Helvetica Neue", "Arial",          // Latin + Cyrillic
                         "PingFang SC", "Noto Sans CJK SC", "Yu Gothic" // CJK (when present)
                     })
            {
                if (installed.Count > 0
                    && !installed.Contains(name) && !installed.Contains(name.Replace(" ", "")))
                    continue;   // этой гарнитуры на системе нет — не тревожим TMP
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
