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
            var font = await ContentFontAsync(url, "шрифт",
                                              () => Theme != null && Theme.FontResourcePath == url);
            if (font == null) return;
            Theme.Font = font;
            LvnFonts.Prewarm(font, _prewarmCorpus); // глава может уже идти
            RebuildChrome();                        // реплики и выборы переодеваются
        }

        /// <summary>ДОСТАТЬ НАЧЕРТАНИЕ ПО АДРЕСУ — и объяснить словами каждую
        /// неудачу.
        ///
        /// <para>Двадцать строк из двадцати пяти совпадали у двух загрузок:
        /// шрифта темы и шрифта отдельной надписи. Механизм один — положить
        /// файл в кэш, превратить в начертание, на каждом обрыве сказать, ЧТО
        /// именно не получилось: «останется шрифт темы» верно, но автор просил
        /// другой и об этом не узнает.</para>
        ///
        /// <para>Отличались три вещи, и все три — про вызывающего: чьё имя
        /// стоит в жалобе, чем проверяется, что ждать ещё не поздно, и что
        /// делать с готовым начертанием. Первые две стали доводами, третья
        /// осталась у него.</para>
        ///
        /// <para><b>Проверка «ещё ждут» обязана стоять ПОСЛЕ ожидания.</b> Пока
        /// файл едет, тему могли сменить, а надпись — убрать с экрана;
        /// применить к ним начертание значило бы переодеть чужое. Это не отказ
        /// и жалобы не требует.</para></summary>
        private async Task<Font> ContentFontAsync(string url, string who,
                                                  System.Func<bool> stillWanted)
        {
            try
            {
                var ca = Assets as CachingAssets;
                if (ca == null)
                {
                    // Хост принёс свой доступ к ассетам, который файлы на диск не
                    // кладёт. Шрифт по адресу тут не взять — но АВТОР ЕГО ЗАДАЛ,
                    // и текст пойдёт другим начертанием: сказать надо.
                    Lvn.Content.ContentLoader.NoteAssetUnusable(url, who + ": хост не кэширует файлы на диск");
                    return null;
                }
                var path = await ca.EnsureCachedFileAsync(url, _cts != null ? _cts.Token : default);
                var font = LvnFonts.FromFile(path);
                if (!stillWanted()) return null;   // передумали, пока ехало — не отказ
                if (font == null)
                {
                    Lvn.Content.ContentLoader.NoteAssetUnusable(url, who + ": файл не стал начертанием");
                    return null;
                }
                return font;
            }
            catch (OperationCanceledException) { return null; } // сцена сменилась — не отказ
            catch (Exception ex)
            {
                Lvn.Content.ContentLoader.NoteAssetUnusable(url, who + ": " + ex.GetType().Name);
                return null;
            }
        }

        // A content-served font for ONE element (`text … font="/content/…"`):
        // fetched into the cache, applied when ready. A cached font lands the
        // same frame; a cold one swaps the face a moment after the label shows.
        private async Task ApplyContentFontAsync(VisualElement el, string url)
        {
            var font = await ContentFontAsync(url, "шрифт надписи",
                                              () => el != null && el.panel != null);
            if (font == null) return;
            LvnFonts.Apply(el, font);
            LvnFonts.Prewarm(font, _prewarmCorpus);
        }
    }
}
