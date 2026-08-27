using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// ЧЕМ НАПИСАН ТЕКСТ — шрифт новеллы и его доставка.
    ///
    /// <para>Шрифт приходит вместе с контентом, как картинка: новелла вправе
    /// принести свой. Отсюда две заботы, которых нет у остального текста —
    /// не запрашивать один файл дважды и не оставить игрока без букв, пока он
    /// едет: до прихода своего шрифта текст живёт на системном, и подмена
    /// проходит незаметно.</para>
    /// </summary>
    public sealed partial class VnStage
    {
        private string _fontUrlLoading; // content url already being fetched (dedup)

        private void ResolveFont()
        {
            if (Theme == null || Theme.Font != null || string.IsNullOrEmpty(Theme.FontResourcePath)) return;
            var src = Theme.FontResourcePath;
            if (src.StartsWith("/"))
            {
                if (_fontUrlLoading == src) return; // fetch already in flight / done
                _fontUrlLoading = src;
                LvnAsync.Fire(LoadContentFontAsync(src), "LoadContentFont");
                return;
            }
            Theme.Font = Resources.Load<Font>(src);
        }

        private async Task LoadContentFontAsync(string url)
        {
            try
            {
                var ca = Assets as CachingAssets;
                if (ca == null) return;
                var path = await ca.EnsureCachedFileAsync(url, _cts != null ? _cts.Token : default);
                var font = LvnFonts.FromFile(path);
                // The theme may have been swapped while the font downloaded —
                // only apply if it still asks for this exact url.
                if (font == null || Theme == null || Theme.FontResourcePath != url) return;
                Theme.Font = font;
                LvnFonts.Prewarm(font, _prewarmCorpus); // chapter may already be playing
                RebuildChrome(); // dialogue/choices re-skin with the new face
            }
            catch { /* best-effort: the panel default font keeps rendering */ }
            // Release the dedup guard: per-chapter theme rebuilds null out
            // Theme.Font, and the NEXT ResolveFont must be able to re-apply —
            // by then it's a cache hit (file on disk + LvnFonts path cache).
            finally { _fontUrlLoading = null; }
        }

        // A content-served font for ONE element (`text … font="/content/…"`):
        // fetched into the cache, applied when ready. A cached font lands the
        // same frame; a cold one swaps the face a moment after the label shows.
        private async Task ApplyContentFontAsync(VisualElement el, string url)
        {
            try
            {
                var ca = Assets as CachingAssets;
                if (ca == null) return;
                var path = await ca.EnsureCachedFileAsync(url, _cts != null ? _cts.Token : default);
                var font = LvnFonts.FromFile(path);
                if (font == null || el == null || el.panel == null) return;
                LvnFonts.Apply(el, font);
                LvnFonts.Prewarm(font, _prewarmCorpus);
            }
            catch { /* label keeps the theme face */ }
        }
    }
}
