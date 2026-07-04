using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Lvn.Services
{
    /// <summary>
    /// Server-authoritative wallet: balances, inventory and purchases live on
    /// the backend; this client only asks and mirrors. Spend returns false on
    /// 409 (insufficient funds) — the game reacts, nothing desyncs. Offline
    /// (or no BaseUrl) every call is a graceful false/null: wire your own
    /// LvnStateStore-style queue on top if a game needs offline economy.
    /// </summary>
    public static class LvnWallet
    {
        /// <summary>Last known state (after any successful call).</summary>
        public static IReadOnlyDictionary<string, long> Balances => _balances;
        public static IReadOnlyDictionary<string, long> Inventory => _inventory;
        private static Dictionary<string, long> _balances = new Dictionary<string, long>();
        private static Dictionary<string, long> _inventory = new Dictionary<string, long>();

        /// <summary>Raised whenever the mirrored state changes.</summary>
        public static event Action Changed;

        public static async Task<bool> RefreshAsync()
        {
            var (code, body) = await LvnBackend.GetAsync("/v1/wallet");
            return code == 200 && Apply(body);
        }

        /// <summary>Server-side earn (reason lands in the audit history).</summary>
        public static async Task<bool> EarnAsync(string currency, long amount, string reason)
        {
            var (code, body) = await LvnBackend.PostAsync("/v1/wallet/earn",
                new JObject { ["currency"] = currency, ["amount"] = amount, ["reason"] = reason }.ToString());
            return code == 200 && Apply(body);
        }

        /// <summary>Spend; false when the server refuses (insufficient funds /
        /// offline). Optional sku is granted into the inventory atomically.</summary>
        public static async Task<bool> SpendAsync(string currency, long amount, string reason, string sku = null)
        {
            var payload = new JObject { ["currency"] = currency, ["amount"] = amount, ["reason"] = reason };
            if (!string.IsNullOrEmpty(sku)) payload["sku"] = sku;
            var (code, body) = await LvnBackend.PostAsync("/v1/wallet/spend", payload.ToString());
            return code == 200 && Apply(body);
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
                Changed?.Invoke();
                return true;
            }
            catch { return false; }
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
