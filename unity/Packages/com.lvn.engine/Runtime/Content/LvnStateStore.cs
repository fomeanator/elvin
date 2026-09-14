using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace Lvn.Content
{
    /// <summary>
    /// The player-state seam: how the app persists a title's script variables
    /// (relationships, route, memory flags — the "stats"). Like <c>ILvnAssets</c>,
    /// the engine ships a local-only default and lets a host plug in server sync.
    ///
    /// Contract: a title's stats are a flat JSON object (name → value). Load returns
    /// the stats to seed the next chapter with; Save records the ending stats.
    /// Implementations MUST be offline-safe (never throw on a dead network).
    /// </summary>
    public interface ILvnStateStore
    {
        Task<JObject> LoadVarsAsync(string titleId, CancellationToken ct);
        Task SaveVarsAsync(string titleId, JObject vars, CancellationToken ct);
    }

    /// <summary>
    /// Local-first, offline store: stats live in PlayerPrefs under
    /// <c>lvn_state_&lt;title&gt;</c> as <c>{"vars":{…},"updatedAt":"ISO"}</c>. No
    /// network — the app plays and keeps stats with no server at all. Also the disk
    /// layer <see cref="HttpStateStore"/> writes through, so it's the single source
    /// of the reconcile timestamp.
    /// </summary>
    public sealed class LocalStateStore : ILvnStateStore
    {
        internal static string Key(string titleId) => Lvn.LvnKeep.Scoped("lvn_state_", titleId);
        internal static event Action<string> Forgotten;

        /// <summary>Забыть переменные новеллы вместе с базой синхронизации.
        /// База уходит обязательно: оставшись, она объявила бы стёртые значения
        /// «нашей правкой» и вернула бы их с ближайшего слияния с сервером.</summary>
        public static void Forget(string titleId)
        {
            var key = Key(titleId);
            using (LvnKeep.Batch())
            {
                LvnKeep.Drop(key);
                LvnKeep.Drop(BaseKey(titleId));
            }
            Forgotten?.Invoke(key);
        }

        public Task<JObject> LoadVarsAsync(string titleId, CancellationToken ct)
        {
            var doc = ReadDoc(titleId);
            return Task.FromResult(Vars(doc));
        }

        public Task SaveVarsAsync(string titleId, JObject vars, CancellationToken ct)
        {
            WriteDoc(titleId, MakeDoc(vars));
            return Task.CompletedTask;
        }

        internal static JObject Vars(JObject doc) => doc?["vars"] as JObject ?? new JObject();

        internal static JObject MakeDoc(JObject vars) => new JObject
        {
            ["vars"] = vars ?? new JObject(),
            ["updatedAt"] = DateTime.UtcNow.ToString("o"),
        };

        /// <summary>ПРОЧИТАТЬ СОХРАНЁННЫЙ РАЗБОР. Нет записи или она битая —
        /// <c>null</c>, и это не беда: значит сохранять было нечего или сохранённое
        /// пережило смену формата. Оба чтения — общий разбор и базовый — держали
        /// эти пять строк своей копией.</summary>
        private static JObject ReadJson(string key)
        {
            try
            {
                var s = LvnKeep.Get(key, "");
                return string.IsNullOrEmpty(s) ? null : JObject.Parse(s);
            }
            catch { return null; }
        }

        internal static JObject ReadDoc(string titleId) => ReadJson(Key(titleId));

        internal static void WriteDoc(string titleId, JObject doc)
        {
            try
            {
                LvnKeep.Put(Key(titleId), doc.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch (Exception e) { Debug.LogWarning("[lvn-state] local write failed: " + e.Message); }
        }

        // ── sync base ────────────────────────────────────────────────────────
        // The vars as of the LAST successful server sync. The field-level merge
        // needs it: "which keys did THIS device change since we agreed with the
        // server" — those keys win over the server's copy in a conflict; keys we
        // didn't touch take the other device's values.

        internal static string BaseKey(string titleId) => Lvn.LvnKeep.Scoped("lvn_state_base_", titleId);

        internal static JObject ReadBase(string titleId) => ReadJson(BaseKey(titleId));

        internal static void WriteBase(string titleId, JObject vars)
        {
            try
            {
                LvnKeep.Put(BaseKey(titleId), (vars ?? new JObject()).ToString(Newtonsoft.Json.Formatting.None));
            }
            catch { /* base is an optimisation — merge degrades to overlay-all */ }
        }
    }

    /// <summary>
    /// Local reads and durable writes never wait for HTTP. A background worker
    /// reconciles the persisted pending scopes, including after restart/reconnect.
    /// Explicit cloud restore tools may await RefreshVarsAsync/FlushAsync;
    /// gameplay uses the immediate ILvnStateStore methods.
    /// </summary>
    public sealed partial class HttpStateStore : ILvnStateStore, IDisposable
    {
        private readonly string _base;
        private readonly string _user;
        // Было 8 — число без объяснения, отличавшееся от загрузчика на две
        // секунды без причины. Состояние тянут таким же коротким запросом, что и
        // манифест, значит и терпение у них одно.
        private const int TimeoutSeconds = Lvn.LvnNetPatience.RequestSeconds;

        // The per-blob secret (X-State-Key header). The user id travels in the
        // URL, which proxies and access logs record — the key is what actually
        // gates the blob (TOFU-claimed server-side on the first keyed PUT). An
        // account-style host passes a shared key; otherwise a per-device secret
        // is generated once.
        private readonly string _key;

        // Постоянная метка — у ПАСПОРТИСТА (Lvn.LvnMark): потеря секрета
        // означает, что сервер не отдаст блоб (ключ заявлен при первой
        // записи), поэтому у него, как и у метки игрока, второй дом.
        internal static string DeviceKey() => Lvn.LvnMark.Steady("lvn_state_key");

        /// <param name="stateKey">Shared secret for the user's blobs. REQUIRED to
        /// be the same across devices when <paramref name="userId"/> is an account
        /// id used on several devices; defaults to a per-device secret.</param>
        public HttpStateStore(string baseUrl, string userId, string stateKey = null)
        {
            _base = LvnUrl.Base(baseUrl);
            _user = string.IsNullOrEmpty(userId) ? "anon" : userId;
            _key = string.IsNullOrEmpty(stateKey) ? DeviceKey() : stateKey;
            InitializeSync();
        }

        public Task<JObject> LoadVarsAsync(string titleId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            SyncScope scope;
            try { scope = Track(titleId, refresh: true); }
            catch (ObjectDisposedException) { throw; }
            catch (Exception e)
            {
                // A damaged sync index must not hide an intact local save.
                Debug.LogWarning("[lvn-state] local read without sync: " + e.Message);
                scope = Scope(titleId);
            }
            return Task.FromResult((JObject)LocalStateStore.Vars(Read(scope.LocalKey)).DeepClone());
        }

        public Task SaveVarsAsync(string titleId, JObject vars, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            // Index first: a crash between index and document leaves harmless
            // extra work, never a durable save that the worker cannot discover.
            var scope = Track(titleId, refresh: false);
            var doc = LocalStateStore.MakeDoc((JObject)(vars ?? new JObject()).DeepClone());
            doc[PendingField] = LvnMark.Once();
            Write(scope.LocalKey, doc); // local write failures must not look like success
            Wake();
            return Task.CompletedTask;
        }

        /// <summary>Field-level conflict merge: start from the OTHER device's doc
        /// (the server's winner) and overlay only the keys THIS device changed
        /// since it last agreed with the server — so two devices touching
        /// different stats both keep their progress, instead of whole-blob
        /// newer-wins throwing one side away. With no baseline (fresh install),
        /// every local key overlays — the old behaviour.</summary>
        internal static JObject MergeVars(JObject serverVars, JObject localVars, JObject baseVars)
        {
            var merged = serverVars != null ? (JObject)serverVars.DeepClone() : new JObject();
            if (localVars == null) return merged;
            foreach (var p in localVars.Properties())
            {
                var baseVal = baseVars?[p.Name];
                if (baseVal != null && JToken.DeepEquals(baseVal, p.Value)) continue; // untouched here — theirs wins
                merged[p.Name] = p.Value.DeepClone();
            }
            if (baseVars != null)
                foreach (var p in baseVars.Properties())
                    if (localVars.Property(p.Name) == null) merged.Remove(p.Name);
            return merged;
        }

        /// <summary>
        /// СВЕРКА ПРИ ЗАГРУЗКЕ — тем же пополевым правилом, что и конфликт при
        /// записи.
        ///
        /// <para>Здесь стояло «новее побеждает ЦЕЛИКОМ», и это теряло игру:
        /// телефон играл в самолёте (статы записаны локально, PUT не ушёл),
        /// планшет тем временем писал на сервер — и первая же онлайн-загрузка
        /// выбрасывала офлайн-сессию. Пополевое слияние для конфликта ЗАПИСИ
        /// уже существовало рядом: одна и та же работа шла по двум правилам,
        /// и в дверь, которой пользуются чаще, поставили правило похуже.</para>
        ///
        /// <para>Порядок разбора: сначала ПРАВИЛО ОБЛАСТИ, если оно
        /// зарегистрировано (<see cref="RuleFor"/>) — свёрток прогресса не
        /// плоский набор статов, и пополевое слияние отдало бы «titles»
        /// целиком одной стороне; потом пополевое слияние по базе; и только
        /// без базы (свежая установка — сравнивать не с чем) остаётся прежнее
        /// «новее побеждает».</para>
        /// </summary>
        internal static JObject Reconcile(JObject server, JObject local, JObject baseVars,
                                          Func<JObject, JObject, JObject> rule)
        {
            if (server == null) return local ?? new JObject();
            if (local == null) return server;
            var mine = LocalStateStore.Vars(local);
            var theirs = LocalStateStore.Vars(server);
            if (rule != null) return LocalStateStore.MakeDoc(rule(mine, theirs));
            if (baseVars == null) return Newer(server, local);
            return LocalStateStore.MakeDoc(MergeVars(theirs, mine, baseVars));
        }

        // ПРАВИЛА ОБЛАСТЕЙ. Область (титул или служебный скоуп вроде
        // «__progress») вправе объявить своё слияние: у прогресса, статов и
        // настроек разная цена ошибки, и одно правило на всех — это выбор в
        // пользу того, чью потерю заметят позже.
        private static readonly System.Collections.Generic.Dictionary<string, Func<JObject, JObject, JObject>> _rules
            = new System.Collections.Generic.Dictionary<string, Func<JObject, JObject, JObject>>(StringComparer.Ordinal);

        /// <summary>Объявить правило слияния для области. <c>rule(мои, чужие)</c>
        /// возвращает сведённое.</summary>
        public static void RuleFor(string scope, Func<JObject, JObject, JObject> rule)
        {
            if (string.IsNullOrEmpty(scope)) return;
            if (rule == null) _rules.Remove(scope);
            else _rules[scope] = rule;
        }

        internal static Func<JObject, JObject, JObject> Rule(string scope)
            => !string.IsNullOrEmpty(scope) && _rules.TryGetValue(scope, out var r) ? r : null;

        // Pick the doc with the later updatedAt; a null side loses to a real one.
        private static JObject Newer(JObject a, JObject b)
        {
            if (a == null) return b ?? new JObject();
            if (b == null) return a;
            return IsNewer((string)a["updatedAt"], (string)b["updatedAt"]) ? a : b;
        }

        private static bool IsNewer(string x, string y)
        {
            if (string.IsNullOrEmpty(x)) return false;
            if (string.IsNullOrEmpty(y)) return true;
            var ox = DateTime.TryParse(x, null, DateTimeStyles.RoundtripKind, out var dx);
            var oy = DateTime.TryParse(y, null, DateTimeStyles.RoundtripKind, out var dy);
            if (!ox) return false;
            if (!oy) return true;
            return dx > dy;
        }

        private bool _keyRejectedLogged;

        private void WarnKeyRejected(string what)
        {
            if (_keyRejectedLogged) return;
            _keyRejectedLogged = true;
            Debug.LogWarning("[lvn-state] " + what + ": the server blob is claimed by a different state key. " +
                             "Multi-device accounts must share one key (HttpStateStore stateKey / NovelApp.StateKey). " +
                             "Playing on local state.");
        }

        private async Task Put(SyncScope scope, JObject local, JObject server, long version,
                               CancellationToken ct)
        {
            var baseline = Read(scope.BaseKey);
            var rule = Rule(scope.Title);
            // With no baseline Save remains a replacement (including explicit
            // reset to {}), preserving the existing state-store contract.
            var vars = rule != null && server != null
                ? rule(LocalStateStore.Vars(local), LocalStateStore.Vars(server))
                : baseline == null || LocalStateStore.Vars(local).Count == 0 ? LocalStateStore.Vars(local)
                : MergeVars(LocalStateStore.Vars(server), LocalStateStore.Vars(local), baseline);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (!Current(scope) || !JToken.DeepEquals(Read(scope.LocalKey), local)) return;
                // A lost successful response must not cause another write when
                // GET already proves the exact state is on the server.
                if (server != null && JToken.DeepEquals(vars, LocalStateStore.Vars(server)))
                {
                    Acknowledge(scope, local, vars);
                    return;
                }
                var send = LocalStateStore.MakeDoc((JObject)vars.DeepClone());
                send["_version"] = version;
                var response = await ExchangeAsync("PUT", scope, send, ct);
                if (!Current(scope)) return;
                if (response.Code == 409)
                {
                    server = response.Doc?["doc"] as JObject;
                    var nextVersion = (long?)response.Doc?["version"];
                    if (server == null || !nextVersion.HasValue) return;
                    version = nextVersion.Value;
                    vars = rule != null
                        ? rule(LocalStateStore.Vars(local), LocalStateStore.Vars(server))
                        : LocalStateStore.Vars(local).Count == 0 ? new JObject()
                        : MergeVars(LocalStateStore.Vars(server), LocalStateStore.Vars(local), baseline);
                    continue;
                }
                if (response.Code >= 200 && response.Code < 300)
                    Acknowledge(scope, local, vars);
                return;
            }
        }
    }
}
