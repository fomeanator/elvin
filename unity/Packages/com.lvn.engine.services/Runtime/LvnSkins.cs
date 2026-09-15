using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Lvn.Services
{
    /// <summary>
    /// РЕЕСТР СКИНОВ — каталог с сервера (<c>/v1/skins</c>, TR-114): одна запись на
    /// всё, что можно надеть, показать или включить — наряды, фоны меню,
    /// аватарки, дальше рамки и темы. Источник каталога один — skins.json,
    /// правится из админки; клиент читает его здесь, а не собирает из четырёх
    /// мест манифеста. Пока каталог не пришёл (офлайн, старый сервер), экраны
    /// живут манифестом, как жили: реестр — сверху, а не вместо.
    /// </summary>
    public static class LvnSkins
    {
        public sealed class Skin
        {
            public string Sku, Kind, Name, Art, Preview, Rarity, Currency;
            public long Price;
            public bool Buy, Gacha;
            public double Weight;
            /// <summary>Только из крутки: в крутке есть, а не продаётся (цена — за копию).</summary>
            public bool GachaOnly => Gacha && !Buy;
        }

        private static readonly List<Skin> _all = new List<Skin>();
        private static readonly Dictionary<string, Skin> _bySku = new Dictionary<string, Skin>();
        private static Dictionary<string, string> _colors = new Dictionary<string, string>();
        private static Dictionary<string, double> _weights = new Dictionary<string, double>();

        public static IReadOnlyList<Skin> All => _all;
        public static IReadOnlyDictionary<string, string> RarityColors => _colors;
        public static IReadOnlyDictionary<string, double> RarityWeights => _weights;
        public static bool Loaded => _all.Count > 0;
        /// <summary>Каталог перечитан — витрины могут обновить подписи и цены.</summary>
        public static event Action Changed;

        public static Skin Find(string sku)
            => !string.IsNullOrEmpty(sku) && _bySku.TryGetValue(sku, out var s) ? s : null;

        /// <summary>Перечитать каталог с сервера. Неудача не трогает прежний.</summary>
        public static async Task<bool> RefreshAsync()
        {
            var (code, body) = await LvnBackend.GetAsync("/v1/skins");
            var d = LvnBackend.Json(code, body);
            if (d == null) return false;
            try
            {
                var skins = new List<Skin>();
                if (d["skins"] is JArray arr)
                    foreach (var raw in arr)
                        if (raw is JObject o && !string.IsNullOrEmpty((string)o["sku"]))
                            skins.Add(new Skin
                            {
                                Sku = (string)o["sku"], Kind = (string)o["kind"], Name = (string)o["name"],
                                Art = (string)o["art"], Preview = (string)o["preview"], Rarity = (string)o["rarity"],
                                Price = (long?)o["price"] ?? 0, Currency = (string)o["currency"],
                                Buy = (bool?)o["buy"] ?? false, Gacha = (bool?)o["gacha"] ?? false,
                                Weight = (double?)o["gacha_weight"] ?? 0,
                            });
                var colors = new Dictionary<string, string>();
                if (d["rarity_colors"] is JObject rc) foreach (var kv in rc) colors[kv.Key] = (string)kv.Value;
                var weights = new Dictionary<string, double>();
                if (d["rarity_weights"] is JObject rw) foreach (var kv in rw) weights[kv.Key] = (double?)kv.Value ?? 0;
                _all.Clear(); _bySku.Clear();
                foreach (var s in skins) { _all.Add(s); _bySku[s.Sku] = s; }
                _colors = colors; _weights = weights;
                Changed?.Invoke();
                return true;
            }
            catch (Exception ex) { LvnLog.Warn("[lvn-skins] каталог не разобран: " + ex.Message); return false; }
        }
    }
}
