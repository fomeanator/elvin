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
