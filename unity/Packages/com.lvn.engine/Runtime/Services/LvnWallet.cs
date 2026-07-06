using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Lvn.Services
{
    /// <summary>
    /// Offline-first wallet over a server-authoritative ledger (the Liminal
    /// model). ONLINE: every call goes to the backend and mirrors its answer.
    /// OFFLINE: earns and spends apply to a PERSISTED local mirror and join a
    /// replay queue; the next moment the network exists (any Refresh — chapter
    /// entry, opening the shop/wardrobe) the queue replays FIFO onto the
    /// server, then the server's truth overwrites the mirror. A spend the
    /// server ultimately refuses (the device overspent while offline) is
    /// dropped on replay and the refresh corrects the balance — the server
    /// stays the single source of truth, the player just never loses an
    /// offline session's honest earnings. IAP/ad rewards stay online-only by
    /// nature (they need the store / the ad network anyway).
    /// </summary>
    public static class LvnWallet
    {
        /// <summary>Last known state (server truth when online; the persisted
        /// local mirror while offline).</summary>
        public static IReadOnlyDictionary<string, long> Balances { get { EnsureLoaded(); return _balances; } }
        public static IReadOnlyDictionary<string, long> Inventory { get { EnsureLoaded(); return _inventory; } }
        private static Dictionary<string, long> _balances = new Dictionary<string, long>();
        private static Dictionary<string, long> _inventory = new Dictionary<string, long>();

        /// <summary>Raised whenever the mirrored state changes.</summary>
        public static event Action Changed;

        /// <summary>Queued offline operations waiting for the network.</summary>
        public static int PendingCount { get { EnsureLoaded(); return _queue.Count; } }

        private const string PMirror = "lvn.wallet.mirror";
        private const string PQueue = "lvn.wallet.queue";
        private static readonly List<JObject> _queue = new List<JObject>();
        private static bool _loaded;
        private static bool _flushing;

        public static async Task<bool> RefreshAsync()
        {
            EnsureLoaded();
            await FlushAsync(); // offline earnings land BEFORE we read the truth
            var (code, body) = await LvnBackend.GetAsync("/v1/wallet");
            return code == 200 && Apply(body);
        }

        /// <summary>Server-side earn; offline it lands in the local mirror and
        /// the replay queue (still true — the earning is honest and durable).</summary>
        public static async Task<bool> EarnAsync(string currency, long amount, string reason)
        {
            EnsureLoaded();
            var payload = new JObject { ["op"] = "earn", ["currency"] = currency, ["amount"] = amount, ["reason"] = reason };
            var (code, body) = await LvnBackend.PostAsync("/v1/wallet/earn", payload.ToString());
            if (code == 200) return Apply(body);
            if (code != 0) return false; // the server SAW it and refused — not an offline case
            ApplyLocal(payload);
            Enqueue(payload);
            return true;
        }

        /// <summary>Spend; false when refused (insufficient funds). Offline the
        /// PERSISTED local balance gates the spend, the op queues for replay —
        /// the wardrobe keeps working on a plane. Optional sku is granted into
        /// the inventory atomically.</summary>
        public static async Task<bool> SpendAsync(string currency, long amount, string reason, string sku = null)
        {
            EnsureLoaded();
            var payload = new JObject { ["op"] = "spend", ["currency"] = currency, ["amount"] = amount, ["reason"] = reason };
            if (!string.IsNullOrEmpty(sku)) payload["sku"] = sku;
            var (code, body) = await LvnBackend.PostAsync("/v1/wallet/spend", payload.ToString());
            if (code == 200) return Apply(body);
            if (code != 0) return false; // 409 insufficient etc. — the server's word stands
            if (!CanApplyLocal(payload)) return false; // offline overdraft — honest no
            ApplyLocal(payload);
            Enqueue(payload);
            return true;
        }

        /// <summary>Replay the offline queue FIFO onto the server. Called from
        /// every Refresh; safe to call any time. Stops at the first transport
        /// failure (still offline) and keeps the rest queued. A server 4xx
        /// (e.g. the overdraft finally caught) DROPS the op — truth wins.</summary>
        public static async Task FlushAsync()
        {
            EnsureLoaded();
            if (_flushing || _queue.Count == 0) return;
            _flushing = true;
            try
            {
                while (_queue.Count > 0)
                {
                    var op = _queue[0];
                    var path = (string)op["op"] == "earn" ? "/v1/wallet/earn" : "/v1/wallet/spend";
                    var body = new JObject(op);
                    body.Remove("op");
                    var (code, resp) = await LvnBackend.PostAsync(path, body.ToString());
                    if (code == 0) return; // still offline — keep the queue
                    _queue.RemoveAt(0);
                    PersistQueue();
                    if (code == 200) Apply(resp);
                    else UnityEngine.Debug.LogWarning(
                        $"[lvn-wallet] queued {op["op"]} {op["currency"]} {op["amount"]} rejected on sync ({code}) — server truth wins");
                }
            }
            finally { _flushing = false; }
        }

        /// <summary>One purchasable pack from the server's IAP catalog — the
        /// store screen's card. Amount is the full grant (bonus included);
        /// Price is a display string, billing happens in the platform store.</summary>
        public sealed class IapPack
        {
            public string Sku;
            public string Currency;
            public long Amount;
            public string Title;   // optional; "" → the screen composes one
            public string Price;   // optional display price ("$4.99")
            public string Icon;    // optional content url
            public long Bonus;     // optional "+N bonus" share of Amount
        }

        /// <summary>The purchasable packs (GET /v1/iap/catalog, server-sorted).
        /// Null offline / when no server is configured.</summary>
        public static async Task<List<IapPack>> GetCatalogAsync()
        {
            var (code, body) = await LvnBackend.GetAsync("/v1/iap/catalog");
            return code == 200 ? ParseCatalog(body) : null;
        }

        /// <summary>Parse a /v1/iap/catalog response; null on garbage.</summary>
        public static List<IapPack> ParseCatalog(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var packs = new List<IapPack>();
                foreach (var t in JObject.Parse(json)["packs"] as JArray ?? new JArray())
                {
                    if (!(t is JObject o) || string.IsNullOrEmpty((string)o["sku"])) continue;
                    packs.Add(new IapPack
                    {
                        Sku = (string)o["sku"],
                        Currency = (string)o["currency"] ?? "",
                        Amount = (long?)o["amount"] ?? 0,
                        Title = (string)o["title"] ?? "",
                        Price = (string)o["price"] ?? "",
                        Icon = (string)o["icon"] ?? "",
                        Bonus = (long?)o["bonus"] ?? 0,
                    });
                }
                return packs;
            }
            catch { return null; }
        }

        /// <summary>Redeem a store purchase. The server validates the receipt
        /// (dev mode: -iap-dev) and grants from its catalog — the client never
        /// decides amounts.</summary>
        public static async Task<bool> VerifyPurchaseAsync(string platform, string sku, string receipt)
        {
            var (code, body) = await LvnBackend.PostAsync("/v1/iap/verify",
                new JObject { ["platform"] = platform, ["sku"] = sku, ["receipt"] = receipt }.ToString());
            return code == 200 && Apply(body);
        }

        private static bool Apply(string json)
        {
            if (string.IsNullOrEmpty(json)) return false;
            try
            {
                var doc = JObject.Parse(json);
                _balances = ToMap(doc["balances"] as JObject);
                _inventory = ToMap(doc["inventory"] as JObject);
                PersistMirror();
                Changed?.Invoke();
                return true;
            }
            catch { return false; }
        }

        // ── the offline half: persisted mirror + replay queue ───────────────

        /// <summary>Would this op fit the local mirror? (spend needs funds).
        /// Pure — tests drive it directly.</summary>
        internal static bool CanApplyLocal(JObject op)
        {
            if ((string)op["op"] != "spend") return true;
            var cur = (string)op["currency"] ?? "";
            _balances.TryGetValue(cur, out var have);
            return have >= ((long?)op["amount"] ?? 0);
        }

        /// <summary>Apply an op to the local mirror and persist it. Pure state
        /// change (no network) — the offline path and tests share it.</summary>
        internal static void ApplyLocal(JObject op)
        {
            var cur = (string)op["currency"] ?? "";
            long amount = (long?)op["amount"] ?? 0;
            _balances.TryGetValue(cur, out var have);
            _balances[cur] = (string)op["op"] == "earn" ? have + amount : have - amount;
            var sku = (string)op["sku"];
            if ((string)op["op"] == "spend" && !string.IsNullOrEmpty(sku))
            {
                _inventory.TryGetValue(sku, out var n);
                _inventory[sku] = n + 1;
            }
            PersistMirror();
            Changed?.Invoke();
        }

        private static void Enqueue(JObject op)
        {
            _queue.Add(op);
            PersistQueue();
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var mirror = UnityEngine.PlayerPrefs.GetString(PMirror, "");
                if (!string.IsNullOrEmpty(mirror))
                {
                    var doc = JObject.Parse(mirror);
                    _balances = ToMap(doc["balances"] as JObject);
                    _inventory = ToMap(doc["inventory"] as JObject);
                }
                var q = UnityEngine.PlayerPrefs.GetString(PQueue, "");
                if (!string.IsNullOrEmpty(q))
                    foreach (var t in JArray.Parse(q))
                        if (t is JObject o) _queue.Add(o);
            }
            catch { /* corrupt prefs → clean start; the next Refresh restores truth */ }
        }

        private static void PersistMirror()
        {
            var doc = new JObject { ["balances"] = ToJObject(_balances), ["inventory"] = ToJObject(_inventory) };
            UnityEngine.PlayerPrefs.SetString(PMirror, doc.ToString(Newtonsoft.Json.Formatting.None));
            UnityEngine.PlayerPrefs.Save();
        }

        private static void PersistQueue()
        {
            UnityEngine.PlayerPrefs.SetString(PQueue, new JArray(_queue).ToString(Newtonsoft.Json.Formatting.None));
            UnityEngine.PlayerPrefs.Save();
        }

        /// <summary>Wipe the mirror and queue (tests / account switch).</summary>
        internal static void ResetLocal()
        {
            _balances = new Dictionary<string, long>();
            _inventory = new Dictionary<string, long>();
            _queue.Clear();
            _loaded = true;
            UnityEngine.PlayerPrefs.DeleteKey(PMirror);
            UnityEngine.PlayerPrefs.DeleteKey(PQueue);
        }

        private static JObject ToJObject(Dictionary<string, long> map)
        {
            var o = new JObject();
            foreach (var kv in map) o[kv.Key] = kv.Value;
            return o;
        }

        private static Dictionary<string, long> ToMap(JObject o)
        {
            var map = new Dictionary<string, long>();
            if (o != null)
                foreach (var kv in o)
                    map[kv.Key] = (long?)kv.Value ?? 0;
            return map;
        }
    }
}
