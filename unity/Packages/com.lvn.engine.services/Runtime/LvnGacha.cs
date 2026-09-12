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
            public bool Super => Kind == "super";
        }

        /// <summary>Награда супер-сектора: наряд, фон, аватарка.</summary>
        public sealed class Prize
        {
            public string Sku;
            public string Label;
            public string Art;
        }

        public sealed class Status
        {
            public List<Sector> Sectors = new List<Sector>();
            public List<Prize> PrizesLeft = new List<Prize>();
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
                return st;
            }
            catch { return null; }
        }

        /// <summary>Прокрутить. Ответ несёт и приз, и новое состояние — второй
        /// запрос за состоянием не нужен, а значит и рассинхрона между ними.</summary>
        public static async Task<Spin> SpinAsync()
        {
            var (code, body) = await LvnBackend.PostAsync("/v1/gacha/spin", "{}");
            var d = LvnBackend.Json(code, body);
            if (d == null) return new Spin { Error = "offline" };
            var err = (string)d["error"];
            if (!string.IsNullOrEmpty(err)) return new Spin { Error = err };
            var spin = new Spin
            {
                SectorId = (string)d["sector"],
                Super = (string)d["kind"] == "super",
                Currency = (string)d["currency"],
                Amount = (long?)d["amount"] ?? 0,
                FreeToday = (bool?)d["free_today"] ?? false,
            };
            if (d["prize"] is JObject p)
                spin.Prize = new Prize { Sku = (string)p["sku"], Label = (string)p["label"], Art = (string)p["art"] };
            ReadPrizes(d["prizes_left"] as JArray, spin.PrizesLeft);
            // КОШЕЛЁК — СРАЗУ, А НЕ «ПРИ СЛУЧАЕ». Сервер только что списал цену
            // прокрута и начислил выигрыш; зеркало на телефоне об этом не
            // знает. Оболочка просила освежить его NudgeAsync — а тот молчит
            // 15 секунд после любого вопроса, и хаб спрашивал только что:
            // кристаллы уходили на сервере, а в шапке лежали прежние до
            // перезапуска («крутка не отнимает кристаллы» — тестировщик
            // 12.09). Крутка — действие игрока, для него правило кошелька одно:
            // RefreshAsync. Спрашивает сама крутка, а не экран: экранов у неё
            // может стать два, а списание одно.
            await LvnWallet.RefreshAsync();
            return spin;
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
                });
        }

        private static void ReadPrizes(JArray src, List<Prize> into)
        {
            if (src == null) return;
            foreach (var p in src)
                into.Add(new Prize { Sku = (string)p["sku"], Label = (string)p["label"], Art = (string)p["art"] });
        }
    }
}
