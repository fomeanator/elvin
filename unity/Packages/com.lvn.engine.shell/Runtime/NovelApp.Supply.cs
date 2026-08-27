using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// СНАБЖЕНЕЦ — что скачать, когда прогреть и что выгрузить.
    ///
    /// <para>У главы есть критический груз (скрипт, первый фон, лица первых
    /// сцен) и всё остальное, что доедет по дороге. Пока их не различали, экран
    /// загрузки был декорацией: он гас, а настоящая работа — скачивание
    /// скрипта, чтение состояния, декод первого фона — начиналась ПОСЛЕ, и
    /// игрок входил в чёрную сцену. Теперь загрузка ждёт ровно то, без чего
    /// показывать нечего, а прочее подъезжает во время игры.</para>
    ///
    /// <para>Здесь же прогрев на старте (что попросить у сети, пока игрок
    /// смотрит на витрину), отчёт о промахах адресов — одна пропавшая картинка
    /// не должна тонуть в логе, но и повторяться в нём тысячу раз тоже, — и
    /// выгрузка арта прошлой главы: память телефона кончается быстрее терпения,
    /// и «что уже не нужно» — такой же вопрос снабжения, как «что пора
    /// привезти».</para>
    ///
    /// <para>Отдельным домом, потому что снабжение — сквозная тема со своими
    /// сроками и своей телеметрией, а не деталь той функции, из которой его
    /// впервые позвали.</para>
    /// </summary>
    public sealed partial class NovelApp
    {
        // ── chapter loading gate ────────────────────────────────────────────────
        // The shell's loading screen used to be decorative (ready = always true):
        // the script fetch, the state load and the first bg decode all happened
        // AFTER it faded — the player entered a black stage while the chapter
        // actually loaded. Now the screen kicks the real work and gates on it:
        // the script download plus the chapter's critical (required) assets via
        // the prioritized AssetScheduler; deferred assets keep streaming during
        // play. Offline every fetch fast-fails into the disk cache, so the gate
        // still completes — OfflinePolicy in PlayOneChapterAsync then decides
        // whether the chapter can actually play.
        private AssetScheduler _chapterSched;
        private Task _chapterScript = Task.CompletedTask;
        private LvnChapter _preparedChapter;

        private Func<bool> BeginChapterLoading(LvnChapter ch)
        {
            if (ch == null || _downloads == null) return () => true;
            _chapterScript = string.IsNullOrEmpty(ch.script_url)
                ? Task.CompletedTask
                : _assets.Loader.DownloadScriptCached(ch.script_url);
            _chapterSched = _downloads.BeginChapter(ch, destroyCancellationToken);
            _preparedChapter = ch;
            var script = _chapterScript;
            var sched = _chapterSched;
            LvnAsync.Fire(WatchChapterWarmAsync(ch, script, sched), "WatchChapterWarm");
            // A faulted script task still completes the gate — PlayOneChapterAsync
            // owns the error path (cache fallback / "unavailable offline").
            return () => script.IsCompleted && sched.RequiredReady;
        }

        // Timing telemetry for the loading gate: how long the script fetch and
        // the required-asset warm ACTUALLY took (per-asset costs are the
        // [lvn-perf] lines) — the number to shrink when "loading feels long".
        private static async Task WatchChapterWarmAsync(LvnChapter ch, Task script, AssetScheduler sched)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { await script; } catch { /* the gate's error path reports it */ }
            Debug.Log($"[lvn-boot] warm {ch.id}: script +{sw.ElapsedMilliseconds}ms");
            while (!sched.RequiredReady && sw.ElapsedMilliseconds < 120_000)
                await Task.Delay(100);
            Debug.Log($"[lvn-boot] warm {ch.id}: required assets {sched.RequiredDone}/{sched.RequiredTotal} +{sw.ElapsedMilliseconds}ms");
        }

        // Progress for the loading bar: bytes when the manifest reports asset
        // sizes, else the required-count fraction (an empty/finished plan is 1).
        private async Task SafeBootPrefetch(LvnManifest manifest, bool online)
        {
            // Online: verify + download the boot set. Offline: warm only what's
            // already on disk (no network), so a cached install still shows its art.
            try { await _downloads.BootPrefetchAsync(manifest, online, default); }
            catch { /* best-effort — missing boot art is non-fatal */ }
        }

        // Probe the server's /healthz with a hard 3s deadline. Token-based, because
        // UnityWebRequest.timeout doesn't reliably interrupt a DNS/TLS stall — the
        // difference between an instant offline fallback and a ~30s boot hang.
        private async Task<bool> ProbeOnlineAsync()
        {
            try
            {
                using var probe = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));
                return await _assets.Loader.HealthzAsync("/healthz", probe.Token);
            }
            catch { return false; }
        }

        private static void OnAssetFailed(string url, long code)
        {
            if (string.IsNullOrEmpty(url)) return;
            lock (_reportedAssetFails)
            {
                if (_reportedAssetFails.Count > 200) _reportedAssetFails.Clear(); // без роста без предела
                if (!_reportedAssetFails.Add(url)) return;
            }
            Lvn.Services.LvnAnalytics.Track("asset_fail", ("asset", url), ("code", code));
        }

        // Метки, о которых уже отчитались в этой главе. Цикл («спросить ещё
        // раз») проходит одну и ту же метку многократно, а для воронки важен
        // ФАКТ «дошёл», а не счётчик оборотов.
        private static readonly HashSet<string> _reachedLabels = new HashSet<string>();

        private async Task UnloadChapterArtSoonAsync(HashSet<string> pinned)
        {
            for (int i = 0; i < 3; i++) await Task.Yield();
            // /sprites/ здесь обязателен: послойный облик героини (~240 МБ
            // декода) не матчился и переживал главу целиком.
            _assets.Loader.UnloadWhere(u =>
                (u.Contains("/art/") || u.Contains("/bg/") || u.Contains("/sprites/"))
                && !pinned.Contains(Lvn.Content.DownloadPolicy.StripVariant(u)));
            // Диск убирается тем же тактом (в пуле потоков): мёртвые версии —
            // всегда, над квотой — давнее. Общий арт глав живёт одним файлом
            // и не удаляется, пока его знает хоть одна глава манифеста.
            await SweepDiskCacheAsync();
        }

        // Квота дискового кэша ассетов. Позже — настройка; 500 МБ покрывает
        // «скачать всё» Time Romance с запасом и не даёт кэшу расти вечно.
        private const long DiskCacheQuotaBytes = 500L << 20;







        // The cross-novel player-stat namespace. Stats under the `global` var
        // (scripts: `set/inc key="global.<stat>"`, read `global.<stat>`) persist to
        // a per-player state blob shared by EVERY novel, so they accumulate across
        // titles and one novel can read what another left behind. Ordinary vars stay
        // scoped to their title.
        private const string GlobalVar = "global";
        private const string GlobalScopeId = "__global";









        // Mobile: persist stats when the app is backgrounded / quit mid-chapter.
        // Fire-and-forget — the store writes its LOCAL cache synchronously before the
        // first await, so stats are safe even if the process is suspended immediately.
        // Desktop/editor: closing the window must save exactly like a mobile
        // background — otherwise the last lines and unsynced vars are lost.
    }
}
