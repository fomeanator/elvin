using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Lvn.Content
{
    /// <summary>
    /// Polls the server's cheap content-version endpoint and raises
    /// <see cref="OnChanged"/> whenever the version differs from the last poll —
    /// the trigger for a live content reload. The poll is a single tiny request
    /// (the server answers a one-line hash, or a zero-body 304 via ETag), so a
    /// short interval is cheap: the host refetches the manifest + version index
    /// and re-applies only what changed. Editing a <c>.lvn</c> or the manifest on
    /// the server shows up in the app within one interval.
    /// </summary>
    public sealed class ContentSync
    {
        private readonly ContentLoader _loader;
        private readonly string _versionPath;
        private string _lastVersion;
        private CancellationTokenSource _cts;

        /// <summary>КАК ЧАСТО СПРАШИВАТЬ. Быстро для разработки (1–2 с),
        /// медленно для прода (15–30 с); пол — четверть секунды.</summary>
        public float IntervalSeconds = DefaultIntervalSeconds;

        /// <summary>Умолчание опроса — ОДНО ЧИСЛО НА ДВОИХ.
        ///
        /// <para>Столько же стояло в <c>NovelApp.SyncInterval</c>, которое это
        /// поле и заполняет. Две копии одного решения: поправишь одну — вторая
        /// молча останется прежней и оживёт там, где синхронизацию заводят
        /// мимо приложения. Теперь число здесь, а приложение его спрашивает.</para></summary>
        public const float DefaultIntervalSeconds = 2f;

        public bool Running => _cts != null;
        public string LastVersion => _lastVersion;

        /// <summary>Версия, которая была ДО последней смены. От неё считается
        /// разница: к моменту события опрос уже переписал текущую, и спрашивать
        /// «что изменилось с текущей» значило бы всегда получать пустоту.</summary>
        public string PreviousVersion { get; private set; }

        /// <summary>Raised (on the main thread) when the content version changes.
        /// Never fires for the first baseline poll.</summary>
        public event Action OnChanged;

        public ContentSync(ContentLoader loader, string versionPath = "/v1/content/version")
        {
            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
            _versionPath = versionPath;
        }

        /// <summary>
        /// ВЕРСИЯ, С КОТОРОЙ ЗАПУСК ЗАБРАЛ КОНТЕНТ — точка отсчёта.
        ///
        /// <para>Без неё первый опрос отвечать не мог и потому объявлял смену
        /// ВСЕГДА (NotifyOnFirstPoll): иначе правка, сделанная между забором
        /// главы и стартом опроса, стала бы точкой отсчёта и не доехала бы
        /// никогда. Дыра настоящая, но цена — перезагрузка главы на КАЖДОМ
        /// запуске: на живом первом входе (04.09) сцена, которая только
        /// началась, тут же переигрывалась заново (ReplayVisuals, след 5
        /// шагов).</para>
        ///
        /// <para>Точка отсчёта снимается в начале запуска — до того, как
        /// забран контент. Тогда первый опрос отвечает СРАВНЕНИЕМ: сервер тот
        /// же — молчим, сервер сменился по дороге — перезагружаем. Оба намерения
        /// целы, лишней работы нет.</para>
        /// </summary>
        public string Baseline
        {
            get => _lastVersion;
            set { if (!string.IsNullOrEmpty(value)) _lastVersion = value; }
        }

        /// <summary>Снять версию контента одним запросом — для точки отсчёта.
        /// Молчит при любой беде: не ответил сервер — значит точки нет, и опрос
        /// поведёт себя как раньше.</summary>
        public static async Task<string> PeekVersionAsync(ContentLoader loader,
            string versionPath = "/v1/content/version", CancellationToken ct = default)
        {
            try { return ParseVersion(await loader.DownloadScriptText(versionPath, ct, singleAttempt: true)); }
            catch { return null; }
        }

        public void Start(CancellationToken ct = default)
        {
            Stop();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = Loop(_cts.Token);
        }

        public void Stop()
        {
            var cts = _cts;
            _cts = null;              // ссылку снимаем первой — см. LvnCancel
            Lvn.LvnCancel.Retire(cts);
        }

        /// <summary>Что именно изменилось с названной версии.</summary>
        public sealed class Delta
        {
            /// <summary>Разницу посчитать не от чего — забирать всё.</summary>
            public bool Full;
            public string Version;
            public Dictionary<string, string> Changed = new Dictionary<string, string>();
            public List<string> Removed = new List<string>();

            /// <summary>Поменялся ли САМ КАТАЛОГ. Только ради него стоит идти за
            /// манифестом: 435 КБ, и правка одной реплики его не трогает.</summary>
            public bool ManifestChanged => Full || Changed.ContainsKey(ManifestKey);
        }

        /// <summary>Имя манифеста в карте версий — по нему узнают, что каталог
        /// действительно менялся.</summary>
        public const string ManifestKey = "manifest.json";

        /// <summary>
        /// СПРОСИТЬ РАЗНИЦУ, А НЕ ЗАБИРАТЬ ВСЁ.
        ///
        /// <para>Замер на живом проекте: карта версий 282 КБ, манифест 435 КБ.
        /// Правка одной реплики меняла хеш её скрипта, значит и общую версию, —
        /// и клиент забирал 717 КБ, чтобы применить изменение в сотню байт.
        /// Живое обновление упиралось не в частоту опроса, а в цену ответа.</para>
        ///
        /// <para>Отказ сервера — не беда: <c>null</c> значит «не смогли
        /// спросить», и вызывающий идёт прежним, дорогим, но рабочим путём.
        /// Новый тракт обязан быть ускорением, а не единственной дорогой.</para>
        /// </summary>
        public async Task<Delta> FetchDeltaAsync(string since, CancellationToken ct = default)
        {
            string path = _versionPath.Replace("/version", "/changes");
            if (!string.IsNullOrEmpty(since)) path += "?since=" + Uri.EscapeDataString(since);
            string json;
            try { json = await _loader.DownloadScriptText(path, ct, singleAttempt: true); }
            catch { return null; }
            try
            {
                var o = Newtonsoft.Json.Linq.JObject.Parse(json);
                var d = new Delta
                {
                    Full = (bool?)o["full"] ?? false,
                    Version = (string)o["version"],
                };
                if (o["changed"] is Newtonsoft.Json.Linq.JObject ch)
                    foreach (var kv in ch) d.Changed[kv.Key] = (string)kv.Value;
                if (o["removed"] is Newtonsoft.Json.Linq.JArray rm)
                    foreach (var x in rm) d.Removed.Add((string)x);
                return d;
            }
            catch { return null; }
        }

        /// <summary>Poll once now. Returns true if the version changed since the
        /// previous poll (the first poll only establishes the baseline).</summary>
        public async Task<bool> PollOnceAsync(CancellationToken ct = default)
        {
            string v;
            try { v = ParseVersion(await _loader.DownloadScriptText(_versionPath, ct, singleAttempt: true)); }
            catch { return false; }
            var prev = _lastVersion;
            bool changed = AdvanceVersion(ref _lastVersion, v, notifyOnFirst: false);
            if (changed) PreviousVersion = prev;
            // Диагностический след: без него «а тот ли контент играет?» каждый
            // раз выясняется руками через curl к /v1/content/version.
            if (prev == null && _lastVersion != null)
                LvnLog.Info($"[lvn-sync] контент: базовая версия {Short(_lastVersion)}");
            else if (changed)
                LvnLog.Info($"[lvn-sync] контент: {Short(prev)} → {Short(_lastVersion)}");
            return changed;
        }

        private static string Short(string v)
            => string.IsNullOrEmpty(v) ? "-" : v.Substring(0, Math.Min(8, v.Length));

        /// <summary>Pure version-state transition, exposed internally for tests.</summary>
        internal static bool AdvanceVersion(ref string lastVersion, string version, bool notifyOnFirst)
        {
            if (version == null) return false;
            if (lastVersion == null)
            {
                lastVersion = version;
                return notifyOnFirst;
            }
            if (version == lastVersion) return false;
            lastVersion = version;
            return true;
        }

        private async Task Loop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                bool changed = await PollOnceAsync(ct);
                if (changed)
                {
                    try { OnChanged?.Invoke(); }
                    catch (Exception ex) { UnityEngine.Debug.LogWarning($"[lvn-sync] handler failed: {ex.Message}"); }
                }
                try { await Task.Delay(Math.Max(250, (int)(IntervalSeconds * 1000f)), ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        /// <summary>Pull the <c>version</c> field out of the endpoint's JSON.
        /// Pure — exposed for tests.</summary>
        internal static string ParseVersion(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return (string)Newtonsoft.Json.Linq.JObject.Parse(json)["version"]; }
            catch { return null; }
        }
    }
}
