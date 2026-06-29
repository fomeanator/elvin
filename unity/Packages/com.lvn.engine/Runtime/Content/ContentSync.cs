using System;
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

        /// <summary>Seconds between polls. Fast for dev (1–2s), slow for prod
        /// (15–30s). Clamped to a 0.25s floor.</summary>
        public float IntervalSeconds = 2f;

        public bool Running => _cts != null;
        public string LastVersion => _lastVersion;

        /// <summary>Raised (on the main thread) when the content version changes.
        /// Never fires for the first baseline poll.</summary>
        public event Action OnChanged;

        public ContentSync(ContentLoader loader, string versionPath = "/v1/content/version")
        {
            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
            _versionPath = versionPath;
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
            _cts = null;
            if (cts == null) return;
            try { cts.Cancel(); } catch { /* already disposed */ }
            cts.Dispose();
        }

        /// <summary>Poll once now. Returns true if the version changed since the
        /// previous poll (the first poll only establishes the baseline).</summary>
        public async Task<bool> PollOnceAsync(CancellationToken ct = default)
        {
            string v;
            try { v = ParseVersion(await _loader.DownloadScriptText(_versionPath, ct, singleAttempt: true)); }
            catch { return false; }
            if (v == null) return false;
            if (_lastVersion == null) { _lastVersion = v; return false; }
            if (v == _lastVersion) return false;
            _lastVersion = v;
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
                    catch (Exception ex) { UnityEngine.Debug.LogWarning($"[sync] handler failed: {ex.Message}"); }
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
