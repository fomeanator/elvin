using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using System.Text;

namespace Lvn.Content
{
    /// <summary>
    /// ТЕКСТ ГЛАВЫ — как скрипт попадает с сервера в игру и что показывать,
    /// когда сервера нет.
    ///
    /// <para>У скрипта своя судьба, не такая, как у картинок: он маленький, он
    /// нужен ЦЕЛИКОМ и до первого кадра, и его правят чаще всего остального.
    /// Отсюда две вещи, которых нет у ассетов: версия рядом с файлом (чтобы
    /// офлайн-реплей взял тот же текст, что играл) и обновление в фоне —
    /// глава продолжается на том, что уже прочитано, а свежая версия ложится
    /// на диск к следующему входу.</para>
    /// </summary>
    public sealed partial class ContentLoader
    {
        public async Task<string> DownloadScriptText(string scriptUrl, CancellationToken ct = default,
            bool singleAttempt = false)
        {
            var bytes = singleAttempt
                ? await FetchOnce(scriptUrl, ct)
                : await Fetch(scriptUrl, ct);
            return Encoding.UTF8.GetString(bytes);
        }

        // Version-pinned script load for chapter playback. Unlike
        // DownloadScriptText (always-fresh, no disk cache) this CACHES the script
        // on disk under a version-folded key, so a chapter opens OFFLINE if ever
        // played online, the version is pinned for the whole session, and an
        // edited script (new hash → new key) is re-downloaded on the next entry.
        // Returns null only if there's no cache AND we can't fetch.
        public async Task<string> DownloadScriptCached(string scriptUrl, CancellationToken ct = default)
        {
            var path = CachePath(_scriptCacheDir, scriptUrl, ".txt");
            if (File.Exists(path))
            {
                try { return await ReadAllTextAsync(path, ct); }
                catch { /* unreadable — fall through to refetch */ }
            }
            try
            {
                var bytes = await FetchOnce(scriptUrl, ct);
                try
                {
                    await WriteAllBytesAsync(path, bytes, ct);
                    await WriteScriptUrlSidecar(path, scriptUrl, ct);
                }
                catch { /* cache write best-effort */ }
                return Encoding.UTF8.GetString(bytes);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Offline and not cached for this version. Last resort: a previously
                // cached version OF THE SAME url (older but the right chapter).
                var stale = NewestCachedScript(scriptUrl);
                if (stale != null)
                {
                    try { return await ReadAllTextAsync(stale, ct); } catch { }   // старая копия не читается — вернём null, вызывающий сходит в сеть
                }
                return null;
            }
        }

        // Fire-and-forget: pull the latest version of a script to disk so the
        // NEXT chapter entry picks it up. `reloadIndex` re-reads the (no-store)
        // version index first to detect a hash published since boot.
        public void RefreshScriptInBackground(string scriptUrl, bool reloadIndex = true)
        {
            if (string.IsNullOrEmpty(scriptUrl)) return;
            LvnAsync.Fire(RefreshScriptAsync(scriptUrl, reloadIndex), "RefreshScript");
        }

        private async Task RefreshScriptAsync(string scriptUrl, bool reloadIndex)
        {
            try
            {
                if (reloadIndex)
                    await LoadAssetVersionsAsync(CancellationToken.None);
                var path = CachePath(_scriptCacheDir, scriptUrl, ".txt");
                if (File.Exists(path)) return; // newest version already cached
                var bytes = await FetchOnce(scriptUrl, CancellationToken.None);
                await WriteAllBytesAsync(path, bytes, CancellationToken.None);
                await WriteScriptUrlSidecar(path, scriptUrl, CancellationToken.None);
                LvnLog.Trace($"[content] script cache refreshed: {scriptUrl}");
            }
            catch { /* best-effort background refresh */ }
        }

        // Finds the most recently written cached version OF THE SAME script url —
        // the offline fallback. The version-folded filename (sha1(url@version))
        // can't be reversed, so each cached script is written with a `.url` sidecar
        // holding its plain url; we only accept a `.txt` whose sidecar matches the
        // requested url. Without this the fallback returned whatever chapter was
        // cached most recently — silently dropping the player into the wrong
        // chapter and saving the wrong ending. Returns null (→ Unavailable) rather
        // than ever serving a different script.
        private string NewestCachedScript(string scriptUrl)
        {
            if (string.IsNullOrEmpty(scriptUrl)) return null;
            try
            {
                var dir = new DirectoryInfo(_scriptCacheDir);
                if (!dir.Exists) return null;
                FileInfo newest = null;
                foreach (var f in dir.GetFiles("*.txt"))
                {
                    var sidecar = Path.ChangeExtension(f.FullName, ".url");
                    string cachedUrl = null;
                    try { if (File.Exists(sidecar)) cachedUrl = File.ReadAllText(sidecar).Trim(); }
                    catch { }   // сайдкар не прочёлся — считаем, что адреса рядом нет
                    if (cachedUrl != scriptUrl) continue; // different (or legacy, un-tagged) script
                    if (newest == null || f.LastWriteTimeUtc > newest.LastWriteTimeUtc) newest = f;
                }
                return newest?.FullName;
            }
            catch { return null; }
        }

        // Records the plain url of a just-cached script beside its version-folded
        // cache file, so the offline fallback can match cached versions to the
        // requested url (see NewestCachedScript).
        private static async Task WriteScriptUrlSidecar(string scriptPath, string scriptUrl, CancellationToken ct)
        {
            try
            {
                await WriteAllBytesAsync(Path.ChangeExtension(scriptPath, ".url"),
                    Encoding.UTF8.GetBytes(scriptUrl), ct);
            }
            catch { /* sidecar is best-effort; a missing one just disables offline fallback for this file */ }
        }
    }
}
