using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Lvn.Content
{
    /// <summary>
    /// Prioritized download planner that sits on top of <see cref="ContentLoader"/>.
    /// A chapter's release set arrives from the server as a map of content-path →
    /// <see cref="LvnAssetMeta"/> (sha/size/tier/critical/eta_ms). The scheduler
    /// splits it into:
    /// <list type="bullet">
    ///   <item><b>required</b> — assets first needed at/near chapter start
    ///   (critical:true). These gate the Play button: <see cref="RequiredReady"/>
    ///   flips true only once every one is on disk.</item>
    ///   <item><b>deferred</b> — assets first used later. They download in the
    ///   background, at lower priority, and KEEP downloading after the chapter
    ///   starts (the player can begin while art trickles in).</item>
    /// </list>
    /// Ordering within a phase is EDF-ish: critical first, then earliest eta, then
    /// smallest size. Concurrency is bounded per tier (mini wide, large narrow) so
    /// a big background file can't starve the critical queue. ContentLoader already
    /// does the heavy lifting (disk cache, dedup, the global download semaphore,
    /// retries), so this class only decides WHAT to fetch WHEN, and tracks progress.
    /// </summary>
    public sealed class AssetScheduler
    {
        // Ширины полос расписания — под общей полосой сети (LvnLanes.Wire).
        // Здесь делят НЕ по срочности, а по размеру: мелочь едет во всю ширину
        // одной пачкой, крупное — узко, иначе пара фонов занимает соединение и
        // пачка ждёт за ними. Броней у этих полос нет: живое по ним не ходит
        // вовсе — расписание главы целиком фоновое, и бронь ему достаётся
        // ниже, в полосе сети.
        private const int MiniParallel = 12;  // мельче MiniBytes — во всю ширину
        private const int NormalParallel = 6; // мельче LargeBytes
        private const int LargeParallel = 2;  // от LargeBytes и крупнее

        // ГДЕ ПРОХОДЯТ ГРАНИЦЫ РАЗМЕРА. Числа стояли прямо в SlotFor, а рядом
        // те же величины были записаны СЛОВАМИ в комментариях к ширине
        // очередей: два места про один порог, и разошлись бы они молча — код
        // качал бы по-новому, комментарий объяснял бы по-старому.
        /// <summary>Мельче этого файл считается мелким: значки, крошки-превью,
        /// силуэты. Их качают пачкой — задержка важнее полосы.</summary>
        private const long MiniBytes = 50 * 1024;
        /// <summary>От этого размера файл считается крупным: фон, спайн-атлас.
        /// Их держат в узкой очереди, иначе пара таких занимает всё соединение
        /// и мелочь ждёт за ними.</summary>
        private const long LargeBytes = 2 * 1024 * 1024;

        // Floor for a missing/zero size so a not-yet-uploaded asset still
        // contributes a little to the byte totals (keeps the bar honest).
        private const long MinAssetBytes = 1;

        private readonly ContentLoader _loader;

        private readonly LvnLane _miniSlots = new LvnLane("мелочь главы", MiniParallel, 0);
        private readonly LvnLane _normalSlots = new LvnLane("средние главы", NormalParallel, 0);
        private readonly LvnLane _largeSlots = new LvnLane("крупное главы", LargeParallel, 0);

        private readonly object _lock = new();
        private CancellationTokenSource _cts;

        // Progress, polled by the loading UI. Bytes drive the single progress bar
        // (required + deferred together); the required counters/flag drive when
        // the Play button lights up.
        public int RequiredTotal { get; private set; }
        public int RequiredDone { get; private set; }
        public bool RequiredReady { get; private set; }
        public long TotalBytes { get; private set; }
        public long DoneBytes { get; private set; }
        public bool AllDone { get; private set; }

        /// <summary>0..1 over the WHOLE set (required + deferred). Falls back to 0
        /// if the server reported no sizes (use the count-based gate instead).</summary>
        public float Progress
        {
            get
            {
                if (AllDone) return 1f;
                lock (_lock)
                {
                    if (TotalBytes > 0) return Mathf.Clamp01((float)DoneBytes / TotalBytes);
                    return 0f;
                }
            }
        }

        /// <summary>
        /// Готов обязательный набор. ШОВ ДЛЯ ВНЕШНЕГО ХОСТА: наш опрашивает
        /// <see cref="RequiredReady"/> предикатом, потому что ждёт ДВА условия
        /// сразу (скрипт главы и ассеты), и событие пришлось бы складывать с
        /// другим ожиданием вручную. Раньше здесь было написано, что по этому
        /// событию хост включает кнопку Play, — слово расходилось с делом.
        /// </summary>
        public event Action OnRequiredReady;
        /// <summary>Fired once when the entire set (required + deferred) is on disk.
        /// The host uses it to auto-start the chapter if the player waited.</summary>
        public event Action OnAllComplete;

        public AssetScheduler(ContentLoader loader)
        {
            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        }

        /// <summary>One asset to fetch, with the metadata used to order it.</summary>
        private readonly struct Item
        {
            public readonly string Url;
            public readonly long Size;
            public readonly string Kind;
            public readonly string Tier;
            public readonly bool Critical;
            public readonly long EtaMs;

            public Item(string url, LvnAssetMeta m)
            {
                Url = url;
                Size = m != null && m.size > 0 ? m.size : MinAssetBytes;
                Kind = m?.kind;
                Tier = m?.tier;
                Critical = m?.critical ?? false;
                EtaMs = m?.eta_ms ?? 0;
            }
        }

        /// <summary>(Re)plans and starts downloading the given release set. Any
        /// in-flight plan from a previous call is cancelled first (e.g. the player
        /// swiped to a different chapter). Returns immediately; progress is
        /// observed via the public properties/events. <paramref name="ct"/> is the
        /// host's lifetime token (app/scene shutdown).</summary>
        public void Start(IReadOnlyDictionary<string, LvnAssetMeta> assets, CancellationToken ct = default)
        {
            Stop();

            var (reqPlan, defPlan) = OrderForDownload(assets);
            var required = new List<Item>(reqPlan.Count);
            foreach (var kv in reqPlan) required.Add(new Item(kv.Key, kv.Value));
            var deferred = new List<Item>(defPlan.Count);
            foreach (var kv in defPlan) deferred.Add(new Item(kv.Key, kv.Value));

            long total = 0;
            foreach (var i in required) total += i.Size;
            foreach (var i in deferred) total += i.Size;

            lock (_lock)
            {
                RequiredTotal = required.Count;
                RequiredDone = 0;
                RequiredReady = required.Count == 0;
                TotalBytes = total;
                DoneBytes = 0;
                AllDone = required.Count == 0 && deferred.Count == 0;
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _cts.Token;

            LvnAsync.Fire(RunAsync(required, deferred, token), "Run");
            // Empty set → already "ready"/"complete"; notify synchronously.
            if (RequiredReady) OnRequiredReady?.Invoke();
            if (AllDone) OnAllComplete?.Invoke();
        }

        /// <summary>Cancels the current plan (if any). Safe to call repeatedly.</summary>
        public void Stop()
        {
            CancellationTokenSource cts;
            // Ссылку снимаем под замком, гасим — вне: Cancel зовёт чужие
            // обработчики, и держать на них блокировку незачем.
            lock (_lock) { cts = _cts; _cts = null; }
            Lvn.LvnCancel.Retire(cts);
        }

        private async Task RunAsync(List<Item> required, List<Item> deferred, CancellationToken ct)
        {
            try
            {
                // Phase 1 — required: download all, gate the Play button.
                await RunPhase(required, isRequired: true, ct);
                if (ct.IsCancellationRequested) return;

                lock (_lock) RequiredReady = true;
                OnRequiredReady?.Invoke();

                // Phase 2 — deferred: keep filling in the background. The player
                // may already have pressed Play; these continue during playback.
                await RunPhase(deferred, isRequired: false, ct);
                if (ct.IsCancellationRequested) return;

                lock (_lock) AllDone = true;
                OnAllComplete?.Invoke();
            }
            catch (OperationCanceledException) { /* replanned or shutting down */ }
            catch (Exception ex)
            {
                Debug.LogWarning($"[scheduler] run failed: {ex.Message}");
            }
        }

        private async Task RunPhase(List<Item> items, bool isRequired, CancellationToken ct)
        {
            if (items.Count == 0) return;
            var tasks = new List<Task>(items.Count);
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                tasks.Add(WarmOne(item, isRequired, ct));
            }
            await Task.WhenAll(tasks);
        }

        // Хвост главы качается ПОД ЖИВОЙ ИГРОЙ — шёпотом, а не в 12 глоток:
        // широкая параллельность (сеть+запись диска) на слабом устройстве
        // отбирала кадры у сцены («начинает лагать на 30%» — живой репорт).
        // Спешить некуда: у стрима фора чтения в десятки секунд.
        private readonly LvnLane _deferredSlots = new LvnLane("хвост главы", 2, 0);

        private async Task WarmOne(Item item, bool isRequired, CancellationToken ct)
        {
            var lane = isRequired ? SlotFor(item.Tier, item.Size) : _deferredSlots;
            using var pass = await lane.EnterAsync(LvnRung.CurrentChapter, ct);
            // СТУПЕНЬ ОБЪЯВЛЕНА ВСЛУХ. Расписание главы — фон по определению:
            // игрок на эти файлы ещё не смотрит. Без объявления загрузчик
            // считал бы их живыми (умолчание безопасно, но здесь неверно) и
            // сорок шесть картинок главы занимали бы бронь, оставленную актёру
            // в кадре.
            try
            {
                await Warm(item, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // ContentLoader already retried/gave up; log and move on so the
                // phase can complete (a dead asset can't block Play forever).
                Debug.LogWarning($"[scheduler] {item.Url} not fetched: {ex.Message}");
            }
            finally
            {
                MarkDone(item, isRequired);
            }
        }

        // Routes to the right ContentLoader primitive so the asset lands under the
        // cache key the in-game loader looks for. Uses the SERVER's classification
        // (kind) when present, falling back to the extension guess.
        //
        // ТОЛЬКО БАЙТЫ НА ДИСК — БЕЗ ДЕКОДА. Раньше здесь стоял
        // DownloadSpriteAsync, и «ничего не подтекает на камеру» означало
        // «распакуй все 46 картинок главы до Play»: пик ~546 МБ RGBA и
        // гарантированный OOM на слабых устройствах ещё на экране загрузки.
        // Правило сохраняется файлами: всё лежит на диске, декод происходит
        // окном сцены (WarmUpcomingArtAsync + сами bg/actor, скрытые вуалью).
        private async Task Warm(Item item, CancellationToken ct)
        {
            var kind = string.IsNullOrEmpty(item.Kind) ? DownloadPolicy.Kind(item.Url) : item.Kind;
            switch (kind)
            {
                case LvnParts.Audio:
                    await _loader.Prefetch(item.Url, LvnParts.Audio, ct);
                    break;
                default:
                    // Warm the SAME file the display path will fetch — the loader
                    // resolves ktx2/@2k/original with the display path's own rule.
                    await _loader.PrefetchSpriteBytes(item.Url, ct);
                    break;
            }
        }

        private void MarkDone(Item item, bool isRequired)
        {
            lock (_lock)
            {
                DoneBytes += item.Size;
                if (DoneBytes > TotalBytes) DoneBytes = TotalBytes;
                if (isRequired) RequiredDone++;
            }
        }

        private LvnLane SlotFor(string tier, long size)
        {
            var t = tier;
            if (string.IsNullOrEmpty(t))
                t = size < MiniBytes ? "mini" : size < LargeBytes ? "normal" : "large";
            return t switch
            {
                "mini" => _miniSlots,
                "large" => _largeSlots,
                _ => _normalSlots,
            };
        }

        /// <summary>Pure planner: partition a release set into (required, deferred)
        /// and order each by priority. Required = critical assets; deferred = the
        /// rest. The chapter script (.lvn) is excluded — the play flow fetches it
        /// directly. Required is smallest-first (the mini burst fills the bar fast
        /// with quick wins; <see cref="RequiredReady"/> still waits for ALL, so
        /// this only changes completion ORDER, never total time). Deferred is
        /// earliest-eta first (use order). Static and side-effect-free.</summary>
        internal static (List<KeyValuePair<string, LvnAssetMeta>> required,
                         List<KeyValuePair<string, LvnAssetMeta>> deferred)
            OrderForDownload(IReadOnlyDictionary<string, LvnAssetMeta> assets)
        {
            var required = new List<KeyValuePair<string, LvnAssetMeta>>();
            var deferred = new List<KeyValuePair<string, LvnAssetMeta>>();
            if (assets != null)
            {
                foreach (var kv in assets)
                {
                    if (string.IsNullOrEmpty(kv.Key) || DownloadPolicy.IsScript(kv.Key)) continue;
                    // ВАЖНОЕ ДЕРЖИТ ВХОД, ОСТАЛЬНОЕ ЕДЕТ НА ЛЕТУ. Заставка
                    // (бренд-фейд/лоадер) ждёт только critical — открывающую
                    // сцену; хвост главы стримится фоном во время игры, впереди
                    // читателя идёт PrefetchAhead. Прежнее «KR-правило» (ждать
                    // ВСЮ главу) стоило минуты ожидания на сотовой сети и
                    // вместе с декодом убивало слабые устройства.
                    if (kv.Value?.critical ?? false) required.Add(kv);
                    else deferred.Add(kv);
                }
            }
            required.Sort(CompareSizeFirst);
            deferred.Sort(ComparePriority); // earliest-use first — впереди читателя
            return (required, deferred);
        }

        // opening-look criticals first, then the small-first burst.
        private static int CriticalThenSizeFirst(KeyValuePair<string, LvnAssetMeta> a,
                                                 KeyValuePair<string, LvnAssetMeta> b)
        {
            bool ac = a.Value?.critical ?? false, bc = b.Value?.critical ?? false;
            if (ac != bc) return ac ? -1 : 1;
            return CompareSizeFirst(a, b);
        }

        // smallest file first (mini burst), then earliest eta, then path (stable).
        private static int CompareSizeFirst(KeyValuePair<string, LvnAssetMeta> a,
                                            KeyValuePair<string, LvnAssetMeta> b)
        {
            long asz = a.Value?.size ?? 0, bsz = b.Value?.size ?? 0;
            if (asz != bsz) return asz.CompareTo(bsz);
            long ae = a.Value?.eta_ms ?? 0, be = b.Value?.eta_ms ?? 0;
            if (ae != be) return ae.CompareTo(be);
            return string.CompareOrdinal(a.Key, b.Key);
        }

        // earliest eta, then smallest file (quick wins), then path (stable).
        private static int ComparePriority(KeyValuePair<string, LvnAssetMeta> a,
                                           KeyValuePair<string, LvnAssetMeta> b)
        {
            long ae = a.Value?.eta_ms ?? 0, be = b.Value?.eta_ms ?? 0;
            if (ae != be) return ae.CompareTo(be);
            long asz = a.Value?.size ?? 0, bsz = b.Value?.size ?? 0;
            if (asz != bsz) return asz.CompareTo(bsz);
            return string.CompareOrdinal(a.Key, b.Key);
        }

    }
}
