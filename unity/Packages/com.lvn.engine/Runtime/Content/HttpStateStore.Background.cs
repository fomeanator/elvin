using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace Lvn.Content
{
    public sealed partial class HttpStateStore
    {
        internal const string PendingField = "_localPending";
        private string _indexId;
        private readonly Dictionary<string, SyncScope> _refresh = new Dictionary<string, SyncScope>();
        private readonly CancellationTokenSource _life = new CancellationTokenSource();
        private Task _flushTask;
        private bool _disposed;
        private float _nextAttempt;
        private int _retrySeconds = 1;
        private static int _forgetGeneration;
        private static readonly Dictionary<string, int> ForgottenTitles = new Dictionary<string, int>();

        /// <summary>A cloud result was stored locally. The host may refresh an
        /// idle menu, but must not replace a running player's variables.</summary>
        public event Action<string, JObject> Synchronized;

        internal readonly struct StateReply
        {
            public readonly long Code;
            public readonly JObject Doc;
            public StateReply(long code, JObject doc = null) { Code = code; Doc = doc; }
        }

        // Deterministic tests control delayed/lost replies through the same
        // worker. Production and integration tests use real UnityWebRequest.
        internal Func<string, string, JObject, CancellationToken, Task<StateReply>> Transport;

        private sealed class SyncScope
        {
            public string Title, LocalKey, BaseKey, Owner, RemoteTitle;
            public int Generation, TitleGeneration;
        }

        static HttpStateStore()
        {
            LvnKeep.Wiped += () => Interlocked.Increment(ref _forgetGeneration);
            LocalStateStore.Forgotten += key => ForgottenTitles[key] = TitleGeneration(key) + 1;
        }

        private static int TitleGeneration(string key) => ForgottenTitles.TryGetValue(key, out var n) ? n : 0;

        private void InitializeSync()
        {
            using var hash = SHA256.Create();
            _indexId = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(_base + "\n" + _user)))
                .Replace("-", "").ToLowerInvariant();
            SyncRunner.Register(this);
        }

        internal string IndexKey => LvnKeep.Scoped("lvn_state_sync_", _indexId);

        public int PendingCount
        {
            get
            {
                int count = 0;
                foreach (var entry in ReadIndex().Properties())
                {
                    var scope = Scope(entry.Name);
                    var doc = Read(scope.LocalKey);
                    if (doc != null && (doc[PendingField] != null
                        || !JToken.DeepEquals(LocalStateStore.Vars(doc), Read(scope.BaseKey)))) count++;
                }
                return count;
            }
        }

        private SyncScope Scope(string title)
        {
            title ??= "";
            var localKey = LocalStateStore.Key(title);
            var storedId = localKey.Substring("lvn_state_".Length);
            return new SyncScope
            {
                Title = title, LocalKey = localKey, BaseKey = LocalStateStore.BaseKey(title),
                Owner = LvnKeep.Owner, Generation = _forgetGeneration,
                TitleGeneration = TitleGeneration(localKey),
                // Keep the first owner's established cloud address. Another
                // account must never read/upload the first owner's cloud blob.
                // When the caller supplies the signed-in account id, the
                // remote identity is already account-scoped. Its address must
                // not depend on whether this was the device's FIRST account.
                RemoteTitle = _user == LvnKeep.Owner || storedId == (title.Length == 0 ? "default" : title)
                    ? title : storedId,
            };
        }

        private bool Current(SyncScope scope) => !_disposed && scope.Generation == _forgetGeneration
            && scope.Owner == LvnKeep.Owner && scope.TitleGeneration == TitleGeneration(scope.LocalKey);

        private static JObject Read(string key)
        {
            var raw = LvnKeep.Get(key, "");
            if (string.IsNullOrEmpty(raw)) return null;
            try { return JObject.Parse(raw); }
            catch (JsonException) { return null; }
        }

        private static void Write(string key, JObject doc)
            => LvnKeep.Put(key, doc.ToString(Formatting.None));

        private JObject ReadIndex()
        {
            var key = IndexKey;
            var index = Read(key) ?? Read(key + ".bak");
            if (index != null) return index;
            if (LvnKeep.Has(key) || LvnKeep.Has(key + ".bak"))
                throw new InvalidOperationException("state sync index unreadable; preserving pending data");
            return new JObject();
        }

        private SyncScope Track(string title, bool refresh)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HttpStateStore));
            var scope = Scope(title);
            var index = ReadIndex();
            if (index.Property(scope.Title) == null)
            {
                index[scope.Title] = true;
                Write(IndexKey, index);
                Write(IndexKey + ".bak", index);
            }
            if (refresh) _refresh[scope.LocalKey] = scope;
            Wake();
            return scope;
        }

        private void Wake() { _nextAttempt = 0; SyncRunner.Ensure(); }

        /// <summary>Explicit cloud refresh for account restore/tools. Normal
        /// chapter reads do not call this and never wait for a remote server.</summary>
        public async Task<JObject> RefreshVarsAsync(string title, CancellationToken ct = default)
        {
            var scope = Track(title, refresh: true);
            await FlushAsync(ct);
            return Current(scope) ? (JObject)LocalStateStore.Vars(Read(scope.LocalKey)).DeepClone() : new JObject();
        }

        /// <summary>One bounded delivery pass, serialized per store. A failed
        /// transfer leaves the persisted pending revision for the next pass.</summary>
        public Task FlushAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (_disposed || LvnNetworkStatus.ForceOffline) return Task.CompletedTask;
            if (_flushTask != null && !_flushTask.IsCompleted) return _flushTask;
            _flushTask = FlushCoreAsync(ct);
            return _flushTask;
        }

        private async Task FlushCoreAsync(CancellationToken ct)
        {
            // Yield before starting HTTP: even an immediately completing
            // transport cannot re-enter callers before _flushTask is assigned.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
            await Task.Yield();
            try
            {
                var index = ReadIndex();
                var scopes = new List<SyncScope>();
                foreach (var entry in index.Properties()) scopes.Add(Scope(entry.Name));
                foreach (var scope in scopes)
                {
                    if (!Current(scope) || linked.IsCancellationRequested) break;
                    var local = Read(scope.LocalKey);
                    var refresh = _refresh.TryGetValue(scope.LocalKey, out var requested) && Current(requested);
                    bool dirty = local != null && (local[PendingField] != null
                        || !JToken.DeepEquals(LocalStateStore.Vars(local), Read(scope.BaseKey)));
                    if (!dirty && !refresh) continue;
                    var response = await ExchangeAsync("GET", scope, null, linked.Token);
                    if (!Current(scope)) break;
                    if (response.Code != 404 && (response.Code < 200 || response.Code >= 300)) continue;
                    if (response.Code != 404 && !(response.Doc?["vars"] is JObject)) continue;
                    var current = Read(scope.LocalKey);
                    // A title was explicitly forgotten while GET was in flight.
                    if (local != null && current == null) { _refresh.Remove(scope.LocalKey); continue; }
                    var server = response.Code == 404 ? null : response.Doc;
                    long version = server == null ? 0 : (long?)server["_version"] ?? 0;
                    // Re-read AFTER GET; the player may have saved a newer state.
                    dirty = current != null && (current[PendingField] != null
                        || !JToken.DeepEquals(LocalStateStore.Vars(current), Read(scope.BaseKey)));
                    if (dirty) await Put(scope, current, server, version, linked.Token);
                    else if (server != null)
                        Acknowledge(scope, current, LocalStateStore.Vars(Reconcile(
                            server, current, Read(scope.BaseKey), Rule(scope.Title))));
                    if (Current(scope)) _refresh.Remove(scope.LocalKey);
                }
            }
            catch (OperationCanceledException) { /* disposal/caller cancellation keeps the persisted revision */ }
            catch (Exception e) { Debug.LogWarning("[lvn-state] background sync deferred: " + e.Message); }
            finally
            {
                _nextAttempt = LvnClock.Wall() + _retrySeconds;
                _retrySeconds = Math.Min(30, _retrySeconds * 2);
            }
        }

        private void Acknowledge(SyncScope scope, JObject expected, JObject accepted)
        {
            if (!Current(scope)) return;
            var current = Read(scope.LocalKey);
            // No response may resurrect forgotten data or replace a newer save.
            if (!JToken.DeepEquals(current, expected)) return;
            bool changed = !JToken.DeepEquals(LocalStateStore.Vars(current), accepted);
            Write(scope.BaseKey, (JObject)accepted.DeepClone());
            Write(scope.LocalKey, LocalStateStore.MakeDoc((JObject)accepted.DeepClone()));
            _retrySeconds = 1;
            try { if (changed) Synchronized?.Invoke(scope.Title, (JObject)accepted.DeepClone()); }
            catch (Exception e) { Debug.LogWarning("[lvn-state] sync observer: " + e.Message); }
        }

        private async Task<StateReply> ExchangeAsync(string method, SyncScope scope, JObject doc, CancellationToken ct)
        {
            if (LvnNetworkStatus.ForceOffline || !Current(scope)) return new StateReply(0);
            var url = _base + "/v1/state?user=" + UnityWebRequest.EscapeURL(_user + "__" + scope.RemoteTitle);
            if (Transport != null) return await Transport(method, url, doc, ct);
            try
            {
                using var req = new UnityWebRequest(url, method);
                req.downloadHandler = new DownloadHandlerBuffer();
                if (doc != null)
                {
                    req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(doc.ToString(Formatting.None)));
                    req.SetRequestHeader("Content-Type", "application/json");
                }
                req.SetRequestHeader("X-State-Key", _key);
                req.timeout = TimeoutSeconds;
                if (!await LvnNetWait.AwaitAsync(req, req.SendWebRequest(), ct)) return new StateReply(0);
                if (LvnNetWait.Failed(req))
                {
                    LvnNetworkStatus.MarkOffline("state sync network error");
                    return new StateReply(0);
                }
                LvnNetworkStatus.MarkOnline("state sync reached server");
                if (req.responseCode == 401) WarnKeyRejected(method);
                JObject body = null;
                try { body = JObject.Parse(req.downloadHandler.text); }
                catch (JsonException) { /* non-JSON error responses leave the revision pending */ }
                return new StateReply(req.responseCode, body);
            }
            catch (OperationCanceledException) { return new StateReply(0); }
            catch (Exception) { return new StateReply(0); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _refresh.Clear();
            LvnCancel.Retire(_life); // persisted revisions intentionally survive disposal
        }

        // One main-thread worker for all stores. Connectivity can be announced
        // on a network thread: only a flag crosses that boundary, never prefs/UI.
        private sealed class SyncRunner : MonoBehaviour
        {
            private static SyncRunner _instance;
            private static readonly List<WeakReference<HttpStateStore>> Stores = new List<WeakReference<HttpStateStore>>();
            private static int _wake;

            private static void OnConnectivity(bool online) { if (online) Interlocked.Exchange(ref _wake, 1); }
            private void OnEnable() => LvnNetworkStatus.Changed += OnConnectivity;
            private void OnDisable() => LvnNetworkStatus.Changed -= OnConnectivity;

            internal static void Register(HttpStateStore store)
            {
                Stores.Add(new WeakReference<HttpStateStore>(store));
                Ensure();
            }

            internal static void Ensure()
            {
                if (_instance != null || !Application.isPlaying) return;
                var go = new GameObject("LvnStateSync") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<SyncRunner>();
            }

            private void Update()
            {
                bool wake = Interlocked.Exchange(ref _wake, 0) != 0;
                for (int i = Stores.Count - 1; i >= 0; i--)
                {
                    if (!Stores[i].TryGetTarget(out var store) || store._disposed) { Stores.RemoveAt(i); continue; }
                    if (wake) { store._nextAttempt = 0; store._retrySeconds = 1; }
                    if (!LvnNetworkStatus.ForceOffline && LvnClock.Wall() >= store._nextAttempt
                        && (store._flushTask == null || store._flushTask.IsCompleted))
                        LvnAsync.Fire(store.FlushAsync(), "StateSync");
                }
            }

            private void OnApplicationPause(bool paused) { if (!paused) Interlocked.Exchange(ref _wake, 1); }
        }
    }
}
