using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;

namespace Lvn.UI.Screens
{
    internal static class ArtBoxPurgePolicy
    {
        internal static bool IsCurrent(string purgeSuffix, string preferredSuffix)
            => !string.IsNullOrEmpty(purgeSuffix) && purgeSuffix == preferredSuffix;

        // Текущий выбор передаётся явно: решение должно проверяться без сцены
        // и без изменения глобальной настройки игрока. Адрес уже несёт ступень
        // показа; null вместо адреса перекачки означает, что работы нет.
        internal static (IReadOnlyList<string> DeleteUrls, string ReloadUrl) Decide(
            string variantUrl, string purgeSuffix, string preferredSuffix,
            IReadOnlyList<string> qualityVariants)
        {
            if (!IsCurrent(purgeSuffix, preferredSuffix)
                || string.IsNullOrEmpty(variantUrl) || !variantUrl.Contains(purgeSuffix))
                return (Array.Empty<string>(), null);

            var deleteUrls = new List<string>();
            foreach (var suffix in qualityVariants)
                if (suffix != purgeSuffix)
                    deleteUrls.Add(variantUrl.Replace(purgeSuffix, suffix));
            // Наличие старых файлов и отсутствие нового проверяет загрузчик:
            // здесь выбирается лишь адрес возможной перекачки, без чтения диска.
            return (deleteUrls, deleteUrls.Count > 0 ? variantUrl : null);
        }
    }

    /// <summary>
    /// ХРАНИЛИЩЕ И ЗАГРУЗКИ — часть <see cref="NovelApp"/>: что игра уже
    /// скачала, чего ей не хватает, как забрать всё целиком и как вычистить
    /// лишнее.
    ///
    /// <para>Тема самостоятельная и объёмная (полный обход манифеста, ступени
    /// качества арта, уборка чужих боксов, очередь глав), а жила посреди
    /// файла, где рядом лежат бут, меню, кошелёк и жизненный цикл приложения.
    /// Разговор про мегабайты и разговор про сцену — разные разговоры.</para>
    /// </summary>
    public sealed partial class NovelApp
    {
        // Подпись главы в очереди загрузок: тире и слово «глава» были склеены
        // кодом четырежды — в четырёх местах одинаково по-русски.
        private static string ChapterEntryLabel(LvnTitle t, LvnChapter ch)
            => LvnWords.Of("dl.chapter_entry", "{title} — chapter {n}")
                .Replace("{title}", LvnWords.Name("title", t.id, t.name))
                .Replace("{n}", ch.number.ToString());

        // ── «Скачать всю игру» (ELVIN-85) ────────────────────────────────────
        // Полный список контента по манифесту, с ЭФФЕКТИВНЫМИ url (крупный
        // арт живёт @2k-вариантом — качаем то, что возьмёт показ).
        private List<(string url, string kind, long size)> CollectContentItems()
        {
            var seen = new HashSet<string>();
            var items = new List<(string, string, long)>();
            void Add(string url, string kind, long size)
            {
                if (string.IsNullOrEmpty(url)) return;
                var eff = DownloadPolicy.Effective(kind, url);
                if (seen.Add(eff)) items.Add((eff, kind, size));
            }
            // ЧТО перечислять — у Описи (LvnParts), здесь только глагол.
            foreach (var part in LvnParts.OfAll(_manifest)) Add(part.Url, part.Kind, part.Size);
            return items;
        }

        private Task<(long missingBytes, int missingCount, long usedBytes)> StorageInfoAsync()
            => Task.Run(async () =>
            {
                var items = CollectContentItems();
                var loader = _assets.Loader;
                long missing = 0; int count = 0;
                foreach (var (url, _, size) in items)
                    if (!loader.IsAssetCached(url))
                    {
                        missing += size > 0 ? size : DownloadPolicy.UnknownSizeBytes;
                        count++;
                    }
                long used = await loader.AssetCacheDiskUsageAsync();
                return (missing, count, used);
            });

        private Lvn.UI.Screens.DownloadCenter _dlCenter;
        private int _lastMissingCount = -1;

        // «Скачать всё» — очередью ПО ГЛАВАМ (решение Ильи 25.08): видно, что
        // качается и что ждёт, любую главу можно снять крестиком. Общие файлы
        // (обложки, меню, звуки) едут первой записью — они нужны любому экрану.
        private Task DownloadEverythingAsync()
        {
            var loader = _assets.Loader;
            _dlCenter ??= new Lvn.UI.Screens.DownloadCenter(loader);
            var m = _manifest;
            if (m?.titles == null) return Task.CompletedTask;

            var chapterUrls = new HashSet<string>();
            var perChapter = new List<(string label, long bytes, List<Lvn.Content.PreloadItem> items)>();
            foreach (var t in m.titles)
            {
                if (t == null) continue;
                foreach (var ch in t.ChaptersOf())
                {
                    if (ch == null) continue;
                    var items = new List<Lvn.Content.PreloadItem>();
                    long bytes = 0;
                    void Add(string url, string kind, long size)
                    {
                        if (string.IsNullOrEmpty(url)) return;
                        var eff = DownloadPolicy.Effective(kind, url);
                        if (!chapterUrls.Add(eff) || loader.IsAssetCached(eff)) return;
                        items.Add(new Lvn.Content.PreloadItem { Url = eff, Kind = kind, Size = size });
                        bytes += size > 0 ? size : DownloadPolicy.UnknownSizeBytes;
                    }
                    foreach (var part in LvnParts.OfChapter(ch)) Add(part.Url, part.Kind, part.Size);
                    if (items.Count > 0)
                        perChapter.Add((ChapterEntryLabel(t, ch), bytes, items));
                }
            }
            // Всё, что не привязано к главам (обложки, меню, интерфейсные звуки).
            var shared = new List<Lvn.Content.PreloadItem>();
            long sharedBytes = 0;
            foreach (var (url, kind, size) in CollectContentItems())
            {
                if (chapterUrls.Contains(url) || loader.IsAssetCached(url)) continue;
                shared.Add(new Lvn.Content.PreloadItem { Url = url, Kind = kind, Size = size });
                sharedBytes += size > 0 ? size : DownloadPolicy.UnknownSizeBytes;
            }
            if (shared.Count > 0) _dlCenter.Enqueue(LvnWords.Of("dl.shared", "Covers and menu"), sharedBytes, shared);
            foreach (var (label, bytes, items) in perChapter)
                _dlCenter.Enqueue(label, bytes, items);
            LvnLog.Trace($"[lvn-content] «Скачать всё»: {perChapter.Count} глав + {shared.Count} общих файлов в очередь");
            return _dlCenter.WhenDrainedAsync();
        }

        /// <summary>Ступень качества с АВТОДЕФОЛТОМ по устройству (как App
        /// Thinning у сторов): пока игрок не выбрал сам, ступень подбирается
        /// по экрану и памяти — маленький/старый телефон стартует легче и
        /// этого не замечает.</summary>
        internal static string EffectiveArtQuality()
        {
            var chosen = Lvn.UI.LvnPrefs.ArtQuality;
            return !string.IsNullOrEmpty(chosen)
                ? chosen
                : Lvn.LvnDeviceProfile.RecommendedArtQuality();
        }

        // Смена качества = ПЕРЕКАЧКА (мысль Ильи: «для этого дозагрузчик и
        // пригодится»): старый бокс вычищается с диска, и ровно то, чем игрок
        // пользовался (что было скачано), встаёт в очередь центра загрузок
        // главами — в новом качестве. Не вся игра: только скачанное.
        private async Task PurgeOtherArtBoxAsync(string keepSuffix)
        {
            var loader = _assets?.Loader;
            var m = _manifest;
            if (loader == null || m?.titles == null) return;
            string cur = keepSuffix;
            if (!ArtBoxPurgePolicy.IsCurrent(cur, DownloadPolicy.PreferredSuffix)) return;
            var redo = new List<(string label, long bytes, List<Lvn.Content.PreloadItem> items)>();
            int removed = 0;
            bool completed = await Task.Run(() =>
            {
                var seen = new HashSet<string>();
                foreach (var t in m.titles)
                {
                    if (!ArtBoxPurgePolicy.IsCurrent(cur, DownloadPolicy.PreferredSuffix)) return false;
                    if (t == null) continue;
                    foreach (var ch in t.ChaptersOf())
                    {
                        if (!ArtBoxPurgePolicy.IsCurrent(cur, DownloadPolicy.PreferredSuffix)) return false;
                        if (ch?.assets == null) continue;
                        List<Lvn.Content.PreloadItem> items = null;
                        long bytes = 0;
                        foreach (var kv in ch.assets)
                        {
                            if (!ArtBoxPurgePolicy.IsCurrent(cur, DownloadPolicy.PreferredSuffix)) return false;
                            if ((kv.Value?.kind ?? "sprite") != "sprite") continue;
                            var eff = DownloadPolicy.DownscaleVariant(kv.Key);
                            var decision = ArtBoxPurgePolicy.Decide(eff, cur,
                                DownloadPolicy.PreferredSuffix, DownloadPolicy.QualityVariants);
                            if (decision.ReloadUrl == null || !seen.Add(decision.ReloadUrl)) continue;
                            bool had = false;
                            foreach (var url in decision.DeleteUrls)
                            {
                                // Между файлами игрок может выбрать другой бокс:
                                // старое решение больше не разрешает его удалять.
                                if (!ArtBoxPurgePolicy.IsCurrent(cur, DownloadPolicy.PreferredSuffix)) return false;
                                if (loader.DeleteCachedAsset(url)) { had = true; removed++; }
                            }
                            if (!ArtBoxPurgePolicy.IsCurrent(cur, DownloadPolicy.PreferredSuffix)) return false;
                            if (!had) continue;
                            if (loader.IsAssetCached(decision.ReloadUrl)) continue;
                            items ??= new List<Lvn.Content.PreloadItem>();
                            items.Add(new Lvn.Content.PreloadItem { Url = decision.ReloadUrl, Kind = "sprite", Size = kv.Value?.size ?? 0 });
                            bytes += kv.Value?.size ?? DownloadPolicy.UnknownSizeBytes;
                        }
                        if (items != null)
                            redo.Add((ChapterEntryLabel(t, ch), bytes, items));
                    }
                }
                return true;
            });
            // Даже готовый список устаревает за время ожидания рабочего потока.
            // Прерванный обход не возобновляем, если игрок успел выбрать cur снова.
            if (!completed || !ArtBoxPurgePolicy.IsCurrent(cur, DownloadPolicy.PreferredSuffix)) return;
            LvnLog.Trace($"[lvn-content] качество арта: чужие боксы вычищены ({removed} файлов), "
                + $"перекачка {redo.Count} глав в {cur}");
            if (redo.Count == 0) return;
            _dlCenter ??= new Lvn.UI.Screens.DownloadCenter(loader);
            foreach (var (label, bytes, items) in redo)
            {
                if (!ArtBoxPurgePolicy.IsCurrent(cur, DownloadPolicy.PreferredSuffix)) return;
                _dlCenter.Enqueue(label, bytes, items);
            }
        }

        // Докачка одной главы очередью центра (кнопка «Скачать главу N»).
        private void EnqueueChapterDownload(LvnTitle t, LvnChapter ch)
        {
            var loader = _assets.Loader;
            _dlCenter ??= new Lvn.UI.Screens.DownloadCenter(loader);
            var items = new List<Lvn.Content.PreloadItem>();
            long bytes = 0;
            void Add(string url, string kind, long size)
            {
                if (string.IsNullOrEmpty(url)) return;
                var eff = DownloadPolicy.Effective(kind, url);
                if (loader.IsAssetCached(eff)) return;
                items.Add(new Lvn.Content.PreloadItem { Url = eff, Kind = kind, Size = size });
                bytes += size > 0 ? size : DownloadPolicy.UnknownSizeBytes;
            }
            foreach (var part in LvnParts.OfChapter(ch)) Add(part.Url, part.Kind, part.Size);
            _dlCenter.Enqueue(ChapterEntryLabel(t, ch), bytes, items, LvnWords.Name("title", t?.id, t?.name));
        }

        // Офлайн-доступность глав для листа кружка: глава «с галочкой»,
        // когда ВСЕ её файлы уже на диске. Зовётся при развороте листа.
        private List<(string label, bool cached)> ChapterAvailability()
        {
            var res = new List<(string, bool)>();
            foreach (var (t, ch, ok) in CachedChapters()) res.Add((ChapterEntryLabel(t, ch), ok));
            return res;
        }

        // То же по новеллам: сколько глав на устройстве из скольких. Лист
        // показывает новеллу одной строкой с полосой, а не главу за главой.
        private List<(string title, int cached, int total)> TitleAvailability()
        {
            var res = new List<(string, int, int)>();
            LvnTitle last = null; int cached = 0, total = 0;
            void Flush()
            {
                if (last != null) res.Add((LvnWords.Name("title", last.id, last.name), cached, total));
            }
            foreach (var (t, _, ok) in CachedChapters())
            {
                if (!ReferenceEquals(t, last)) { Flush(); last = t; cached = 0; total = 0; }
                total++;
                if (ok) cached++;
            }
            Flush();
            return res;
        }

        // Главы манифеста с ответом «все файлы на диске?» — один обход для
        // списка глав и для сводки по новеллам.
        private IEnumerable<(LvnTitle t, LvnChapter ch, bool ok)> CachedChapters()
        {
            var loader = _assets?.Loader;
            var m = _manifest;
            if (loader == null || m?.titles == null) yield break;
            foreach (var t in m.titles)
            {
                if (t == null) continue;
                foreach (var ch in t.ChaptersOf())
                {
                    if (ch == null) continue;
                    bool ok = true;
                    foreach (var part in LvnParts.OfChapter(ch))
                    {
                        if (string.IsNullOrEmpty(part.Url)) continue;
                        if (loader.IsAssetCached(DownloadPolicy.Effective(part.Kind, part.Url))) continue;
                        ok = false; break;
                    }
                    yield return (t, ch, ok);
                }
            }
        }

        private async Task SweepDiskCacheAsync()
        {
            var m = _manifest;
            if (m?.titles == null || _assets?.Loader == null) return;
            var loader = _assets.Loader;
            var live = new HashSet<string>();
            var prot = new HashSet<string>();
            void Add(HashSet<string> set, string u) => loader.AddLiveKeysFor(u, set);
            foreach (var t in m.titles)
            {
                if (t == null) continue;
                bool intro = Lvn.UI.Screens.LvnIntro.Is(t);
                var current = LvnProgress.Current(t);
                foreach (var part in LvnParts.OfTitleArt(t)) Add(live, part.Url);
                foreach (var ch in t.ChaptersOf())
                {
                    if (ch == null) continue;
                    // Вводная и глава, на которой стоит прогресс, — неприкосновенны:
                    // им играть следующими.
                    bool keep = intro || (current != null && ch.id == current.id);
                    foreach (var part in LvnParts.OfChapter(ch))
                    {
                        Add(live, part.Url);
                        if (keep) Add(prot, part.Url);
                    }
                }
            }
            foreach (var u in MenuArtUrls()) { Add(live, u); Add(prot, u); }
            foreach (var part in LvnParts.OfShellSound(m)) Add(live, part.Url);
            var (removed, freed) = await loader.SweepAssetCacheAsync(live, prot, DiskCacheQuotaBytes);
            if (removed > 0)
                LvnLog.Trace($"[lvn-content] уборка диска: {removed} файлов, {freed >> 20} МБ (мёртвые версии + давнее над квотой)");
        }

        // Every image url the MENU surfaces reference (covers, chapter loading
        // backdrops, collection art) — the chapter-end unload must never destroy
        // these while the carousel/hub still draw them. Rebuilt lazily per
        // manifest (content live-reload swaps the manifest object).
        private HashSet<string> _menuArt;
        private LvnManifest _menuArtFor;

        private HashSet<string> MenuArtUrls()
        {
            if (_menuArt != null && ReferenceEquals(_menuArtFor, _manifest)) return _menuArt;
            var set = new HashSet<string>();
            foreach (var part in LvnParts.OfMenuArt(_manifest)) set.Add(part.Url);
            _menuArt = set;
            _menuArtFor = _manifest;
            return set;
        }
    }
}
