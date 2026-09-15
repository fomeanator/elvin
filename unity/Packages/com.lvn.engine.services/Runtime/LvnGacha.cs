using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Lvn.Services
{
    /// <summary>
    /// КРУТКИ (TR-47) — клиентская сторона гачи.
    ///
    /// <para>Что выпало, решает СЕРВЕР: здесь только спрашивают состояние и
    /// просят прокрутить. Экран показывает анимацию поверх уже известного
    /// ответа — иначе выигрыш стоил бы правки памяти телефона.</para>
    /// </summary>
    public static class LvnGacha
    {
        /// <summary>Сектор рулетки: валютный отдаёт число, супер — вещь.</summary>
        public sealed class Sector
        {
            public string Id;
            public string Kind;      // currency | super
            public string Currency;
            public long Amount;
            public string Label;
            public string Icon;
            public double Weight;    // вес в жеребьёвке — для показа шансов
            public bool Super => Kind == "super";
        }

        /// <summary>Награда супер-сектора: наряд, фон, аватарка.</summary>
        public sealed class Prize
        {
            public string Sku;
            public string Label;
            public string Art;
            public string Rarity;    // ключ редкости (сервер или манифест)
            public double Weight;    // вес внутри «Редкого» — для шанса
            public long Price;       // цена в гардеробе, 0 — не продаётся
            public string Currency;  // валюта цены
        }

        public sealed class Status
        {
            public List<Sector> Sectors = new List<Sector>();
            public List<Prize> PrizesLeft = new List<Prize>();
            public List<Prize> Prizes = new List<Prize>();   // весь набор, включая уже выбитые
            public bool FreeToday;
            public string SpinCurrency;
            public long SpinPrice;
            public int Spins;
        }

        /// <summary>Итог прокрута: какой сектор выпал и что за это дали.</summary>
        public sealed class Spin
        {
            public string SectorId;
            public bool Super;
            public string Currency;
            public long Amount;
            public Prize Prize;
            public bool FreeToday;
            public List<Prize> PrizesLeft = new List<Prize>();
            public bool WalletSynced;
            public string Error;      // пусто — прокрут состоялся
        }

        public static async Task<Status> GetAsync()
        {
            var (code, body) = await LvnBackend.GetAsync("/v1/gacha");
            var d = LvnBackend.Json(code, body);
            if (d == null) return null;
            try
            {
                var st = new Status
                {
                    FreeToday = (bool?)d["free_today"] ?? false,
                    SpinCurrency = (string)d["spin_currency"],
                    SpinPrice = (long?)d["spin_price"] ?? 0,
                    Spins = (int?)d["spins"] ?? 0,
                };
                ReadSectors(d["sectors"] as JArray, st.Sectors);
                ReadPrizes(d["prizes_left"] as JArray, st.PrizesLeft);
                ReadPrizes(d["prizes"] as JArray, st.Prizes);
                if (st.Prizes.Count == 0) st.Prizes.AddRange(st.PrizesLeft);   // старый сервер
                return st;
            }
            catch { return null; }
        }

        /// <summary>Прокрутить. Ответ несёт и приз, и новое состояние — второй
        /// запрос за состоянием не нужен, а значит и рассинхрона между ними.</summary>
        public static async Task<Spin> SpinAsync()
        {
            // Pending story earnings must reach the server before it charges the spin.
            await LvnWallet.FlushAsync();
            var (code, body) = await LvnBackend.PostAsync("/v1/gacha/spin", "{}");
            var spin = ReadSpin(code, body);
            if (!string.IsNullOrEmpty(spin.Error)) return spin;
            // This is a purchase/reward, never a throttled background nudge.
            // Refresh publishes BOTH balances and inventory before the reveal,
            // even if the player closes the screen while the request is in flight.
            // КОШЕЛЁК — СРАЗУ, А НЕ «ПРИ СЛУЧАЕ» (Ваня, fix/gacha-wallet). Сервер
            // только что списал цену прокрута и начислил выигрыш; зеркало на
            // телефоне об этом не знает. NudgeAsync молчит 15 секунд после
            // любого вопроса, а хаб спрашивал только что — кристаллы уходили
            // на сервере, а в шапке лежали прежние до перезапуска («крутка не
            // отнимает кристаллы», тестировщик 12.09). Крутка — действие
            // игрока: спрашивает сама крутка, а не экран.
            spin.WalletSynced = await LvnWallet.RefreshAsync();
            return spin;
        }

        internal static Spin ReadSpin(long code, string body)
        {
            if (code == 0) return new Spin { Error = "offline" };
            try
            {
                var d = JObject.Parse(body ?? "");
                var err = (string)d["error"];
                if (!string.IsNullOrEmpty(err)) return new Spin { Error = err };
                if (!LvnBackend.Ok(code) || string.IsNullOrEmpty((string)d["sector"]))
                    return new Spin { Error = "invalid_response" };
                var spin = new Spin
                {
                    SectorId = (string)d["sector"],
                    Super = (string)d["kind"] == "super",
                    Currency = (string)d["currency"],
                    Amount = (long?)d["amount"] ?? 0,
                    FreeToday = (bool?)d["free_today"] ?? false,
                };
                if (d["prize"] is JObject p)
                    spin.Prize = ReadPrize(p);
                ReadPrizes(d["prizes_left"] as JArray, spin.PrizesLeft);
                if (spin.Super ? string.IsNullOrEmpty(spin.Prize?.Sku) : string.IsNullOrEmpty(spin.Currency))
                    return new Spin { Error = "invalid_response" };
                return spin;
            }
            catch { return new Spin { Error = "invalid_response" }; }
        }

        private static void ReadSectors(JArray src, List<Sector> into)
        {
            if (src == null) return;
            foreach (var s in src)
                into.Add(new Sector
                {
                    Id = (string)s["id"],
                    Kind = (string)s["kind"],
                    Currency = (string)s["currency"],
                    Amount = (long?)s["amount"] ?? 0,
                    Label = (string)s["label"],
                    Icon = (string)s["icon"],
                    Weight = (double?)s["weight"] ?? 0,
                });
        }

        private static void ReadPrizes(JArray src, List<Prize> into)
        {
            if (src == null) return;
            foreach (var p in src)
                if (p is JObject o) into.Add(ReadPrize(o));
        }

        private static Prize ReadPrize(JObject p) => new Prize
        {
            Sku = (string)p["sku"], Label = (string)p["label"], Art = (string)p["art"],
            Rarity = (string)p["rarity"], Weight = (double?)p["weight"] ?? 0,
        };
    }
}
