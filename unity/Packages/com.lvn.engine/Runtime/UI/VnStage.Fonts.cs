using System;
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
                if (ca == null)
                {
                    // Хост принёс свой доступ к ассетам, который файлы на диск не
                    // кладёт. Шрифт по адресу тут не взять — но АВТОР ЕГО ЗАДАЛ,
                    // и текст пойдёт другим начертанием: сказать надо.
                    Lvn.Content.ContentLoader.NoteAssetUnusable(url, "шрифт: хост не кэширует файлы на диск");
                    return;
                }
                var path = await ca.EnsureCachedFileAsync(url, _cts != null ? _cts.Token : default);
                var font = LvnFonts.FromFile(path);
                // The theme may have been swapped while the font downloaded —
                // only apply if it still asks for this exact url.
                if (Theme == null || Theme.FontResourcePath != url) return;   // тему сменили — не отказ
                if (font == null)
                {
                    Lvn.Content.ContentLoader.NoteAssetUnusable(url, "шрифт: файл не стал начертанием");
                    return;
                }
                Theme.Font = font;
                LvnFonts.Prewarm(font, _prewarmCorpus); // chapter may already be playing
                RebuildChrome(); // dialogue/choices re-skin with the new face
            }
            catch (OperationCanceledException) { /* глава сменилась — не отказ */ }
            catch (Exception ex)
            {
                // Раньше здесь стояло молчание с пояснением «best-effort: панель
                // продолжит рисовать своим шрифтом». Рисовать-то продолжит, но
                // автор ЗАДАЛ начертание и увидит чужое — ни в логе, ни в
                // отчёте следа не оставалось.
                Lvn.Content.ContentLoader.NoteAssetUnusable(url, "шрифт: " + ex.GetType().Name);
            }
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
                if (ca == null)
                {
                    Lvn.Content.ContentLoader.NoteAssetUnusable(url, "шрифт надписи: хост не кэширует файлы на диск");
                    return;
                }
                var path = await ca.EnsureCachedFileAsync(url, _cts != null ? _cts.Token : default);
                var font = LvnFonts.FromFile(path);
                if (el == null || el.panel == null) return;   // надпись убрали — не отказ
                if (font == null)
                {
                    Lvn.Content.ContentLoader.NoteAssetUnusable(url, "шрифт надписи: файл не стал начертанием");
                    return;
                }
                LvnFonts.Apply(el, font);
                LvnFonts.Prewarm(font, _prewarmCorpus);
            }
            catch (OperationCanceledException) { /* сцена сменилась — не отказ */ }
            catch (Exception ex)
            {
                // «Надпись останется с шрифтом темы» — верно, но автор просил
                // ДРУГОЙ и об этом не узнает.
                Lvn.Content.ContentLoader.NoteAssetUnusable(url, "шрифт надписи: " + ex.GetType().Name);
            }
        }
    }
}
