using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using System.Text;
using System.Security.Cryptography;
using System.Linq;

namespace Lvn.Content
{
    /// <summary>
    /// ЧТО УЖЕ ЛЕЖИТ НА ДИСКЕ — и можно ли этому верить.
    ///
    /// <para>Диск отвечает на вопрос, от которого зависит вся офлайн-игра:
    /// «эту главу можно начать прямо сейчас?». Ответ не сводится к «файл
    /// существует»: файл может быть от прошлой версии, оборваться на середине
    /// или разойтись с контрольной суммой. Поэтому проверка ходит по адресам
    /// главы, а не по папке, и сверяет версию, а не наличие.</para>
    /// </summary>
    public sealed partial class ContentLoader
    {
        public bool HasCachedAssets()
        {
            try
            {
                return Directory.Exists(_assetCacheDir) &&
                       Directory.EnumerateFiles(_assetCacheDir, "*.bin").Any();
            }
            catch { return false; }
        }

        /// <summary>True when the content origin is a local bundle (StreamingAssets
        /// via file://). For the offline policy this means everything is "cached"
        /// and always reachable, so a bundled build lands on ReadyFromCache.</summary>
        public bool IsLocal => _local;

        /// <summary>
        /// ИСТОЧНИК ДОСТУПЕН. Офлайн-признак — про СЕТЬ, а файлы в сборке никуда
        /// не деваются: локальный источник доступен всегда.
        ///
        /// <para>Оговорку эту писали от руки и по-разному: где-то
        /// <c>IsLocal || !IsOffline</c>, где-то один <c>!IsOffline</c> — и
        /// локальная сборка считалась «офлайновой» ровно там, где про оговорку
        /// забыли. Вопрос один, и отвечает на него тот, кто держит источник.</para>
        /// </summary>
        public bool Reachable => _local || !LvnNetworkStatus.IsOffline;

        /// <summary>True if the version-pinned script for <paramref name="scriptUrl"/>
        /// is on disk. Pure disk check (no network) — used by the offline policy.
        /// A local bundle is authoritative and complete, so it always reports true.</summary>
        public bool IsScriptCached(string scriptUrl)
        {
            if (string.IsNullOrEmpty(scriptUrl)) return false;
            if (_local) return true;
            try { return File.Exists(CachePath(_scriptCacheDir, scriptUrl, ".txt")); }
            catch { return false; }
        }

        /// <summary>True if the asset bytes for <paramref name="url"/> are on disk
        /// under the current version key. Pure disk check (no network). A local
        /// bundle reports true (the asset ships inside the build).</summary>
        public bool IsAssetCached(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            if (_local) return true;
            try { return File.Exists(CachePath(_assetCacheDir, url, ".bin")); }
            catch { return false; }
        }

        /// <summary>Scans <paramref name="urls"/> against the local asset cache
        /// and returns the subset that are missing. Sets IsVerifying during the
        /// scan so the HUD can show a verifying state instead of filenames.</summary>
        public async Task<IReadOnlyList<string>> VerifyAsync(
            IReadOnlyList<string> urls, CancellationToken ct)
        {
            if (urls == null || urls.Count == 0) return Array.Empty<string>();
            IsVerifying = true;
            lock (_underway)
            {
                BatchTotal       = urls.Count;
                BatchDone        = 0;
                CurrentFileLabel = null;
                LastStartedUrl   = null;
            }
            var missing = new List<string>();
            foreach (var url in urls)
            {
                try { ct.ThrowIfCancellationRequested(); }
                catch (OperationCanceledException) { IsVerifying = false; throw; }
                if (!File.Exists(CachePath(_assetCacheDir, url, ".bin")))
                    missing.Add(url);
                lock (_underway) BatchDone++;
                try { await Task.Yield(); }
                catch (OperationCanceledException) { IsVerifying = false; throw; }
            }
            lock (_underway)
            {
                BatchTotal       = 0;
                BatchDone        = 0;
                CurrentFileLabel = null;
                LastStartedUrl   = null;
            }
            IsVerifying = false;
            return missing;
        }

        // Negative cache С TTL: url, на который сервер ответил 4xx, не
        // передёргивается на каждую перестройку экрана — но и не хоронится на
        // всю сессию. Сервер ГЕНЕРИТ варианты (@2k/@mini/ktx2) лениво: первый
        // запрос честно 404, файл готов через секунды — вечный кэш оставлял
        // витрину на полноразмерах до перезапуска (живой лог «ok via full»
        // при готовых mini). Две минуты — с запасом на самое долгое кодирование.

        private string CachePath(string dir, string url, string ext)
        {
            var ver = VersionFor(url);
            return Path.Combine(dir, HashKey(url, ver) + ext);
        }

        /// <summary>Content-integrity check: does the payload hash to the version
        /// index's sha256 hex? Exposed for tests.</summary>
        internal static bool Sha256Matches(byte[] data, string expectedHex)
        {
            if (data == null || string.IsNullOrEmpty(expectedHex)) return false;
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(data);
            if (expectedHex.Length != hash.Length * 2) return false;
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return string.Equals(sb.ToString(), expectedHex, StringComparison.OrdinalIgnoreCase);
        }

        // Pure cache-key hash, exposed for tests: sha1(url) or sha1(url@version).
        // ── умная уборка диска ───────────────────────────────────────────────
        // Кэш ключуется по url: общий арт двух глав — ОДИН файл, и он живёт,
        // пока его знает хоть одна глава манифеста («перс есть во второй главе
        // — с первой его не удаляют», правило Ильи). Мёртвые ключи (старые
        // версии после обновления арта, снятый контент) удаляются всегда; над
        // квотой уходят самые давние, защищённые (текущая/следующая глава,
        // вводная) — никогда.

        /// <summary>Ключи кэша, под которыми может лежать этот url: сам файл и
        /// все его варианты (@2k, @mini, .ktx2) — их и держит уборка живыми.</summary>
    }
}
