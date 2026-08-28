using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Lvn.Services
{
    /// <summary>
    /// Rewarded ads — currency for a completed video. The engine ships no ad
    /// SDK: the host installs its mediator (CAS.AI etc.) and plugs
    /// <see cref="ShowRewarded"/>; the SERVER owns the reward amounts and the
    /// per-user daily caps (content/ads.json → /v1/ads/reward), so a hacked
    /// client can at most watch its own quota. No hook — no ad surfaces
    /// anywhere, the store screen simply doesn't render the free cards.
    /// </summary>
    public static class LvnAds
    {
        /// <summary>Host hook: show a rewarded ad for a placement, resolve
        /// true when the user EARNED the reward (watched to completion).
        /// CAS.AI example: wrap MediationManager.ShowAd + OnAdCompleted.</summary>
        public static Func<string, Task<bool>> ShowRewarded;

        public static bool Available => ShowRewarded != null;

        /// <summary>One rewarded placement as the server advertises it.</summary>
        public sealed class Placement
        {
            public string Id;
            public string Currency;
            public long Amount;
            public int DailyCap;

            /// <summary>Сколько показов осталось в текущем цикле; -1 — цикла
            /// нет. Считает СЕРВЕР: клиент, ведущий свой счётчик, разошёлся бы
            /// с ним на первом же перезапуске игры.</summary>
            public int Left = -1;

            /// <summary>Сколько показов в полном цикле — «2 из ТРЁХ». Без него
            /// подпись знала бы только половину.</summary>
            public int Charges;

            /// <summary>Unix-время, когда заряды вернутся; 0 — они есть.</summary>
            public long ReadyAtUnix;

            /// <summary>Сколько ждать сейчас, в секундах. Ноль — можно
            /// смотреть.</summary>
            public long WaitSeconds
            {
                get
                {
                    if (ReadyAtUnix <= 0) return 0;
                    long left = ReadyAtUnix - System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    return left > 0 ? left : 0;
                }
            }

            /// <summary>Можно ли смотреть прямо сейчас.</summary>
            public bool Ready => Left != 0 && WaitSeconds <= 0;
        }

        /// <summary>The server's rewarded placements (GET /v1/ads/catalog).
        /// Null offline.</summary>
        public static async Task<List<Placement>> GetCatalogAsync()
        {
            var (code, body) = await LvnBackend.GetAsync("/v1/ads/catalog");
            if (!LvnBackend.Ok(code) || string.IsNullOrEmpty(body)) return null;
            try
            {
                var list = new List<Placement>();
                foreach (var t in JObject.Parse(body)["placements"] as JArray ?? new JArray())
                {
                    if (!(t is JObject o)) continue;
                    list.Add(new Placement
                    {
                        Id = (string)o["placement"] ?? "",
                        Currency = (string)o["currency"] ?? "",
                        Amount = (long?)o["amount"] ?? 0,
                        DailyCap = (int?)o["daily_cap"] ?? 0,
                        Charges = (int?)o["charges"] ?? 0,
                        Left = (int?)o["left"] ?? -1,
                        ReadyAtUnix = (long?)o["ready_at"] ?? 0,
                    });
                }
                NoteCatalog(list);
                return list;
            }
            catch { return null; }
        }

        /// <summary>Show the ad, then claim the SERVER-side reward and refresh
        /// the wallet mirror. False on cancel/cap/offline.</summary>
        public static async Task<bool> WatchAndRewardAsync(string placement)
        {
            if (ShowRewarded == null || string.IsNullOrEmpty(placement)) return false;
            bool completed;
            try { completed = await ShowRewarded(placement); }
            catch { return false; }
            if (!completed) return false;

            var (code, reply) = await LvnBackend.PostAsync("/v1/ads/reward",
                new JObject { ["placement"] = placement }.ToString());
            LvnAnalytics.Track(LvnBackend.Ok(code) ? LvnEvents.AdReward : LvnEvents.AdRewardFail, ("placement", placement));
            // Ответ несёт новое состояние зарядов — и при отказе тоже: «ещё
            // 1:12» игроку сказать можно только отсюда.
            NoteState(placement, reply);
            if (!LvnBackend.Ok(code)) return false;
            await LvnWallet.RefreshAsync(); // the grant lands in the pills immediately
            return true;
        }

        /// <summary>Состояние зарядов, каким его назвал сервер в последнем
        /// ответе. Кнопка спрашивает ЗДЕСЬ: свой счётчик у неё разошёлся бы с
        /// сервером на первом перезапуске.</summary>
        public static Placement StateOf(string placement)
            => !string.IsNullOrEmpty(placement) && _state.TryGetValue(placement, out var p) ? p : null;

        /// <summary>Состояние обновилось — кнопке пора перерисоваться.</summary>
        public static event Action Changed;

        private static readonly Dictionary<string, Placement> _state = new Dictionary<string, Placement>();

        internal static void NoteState(string placement, string replyJson)
        {
            if (string.IsNullOrEmpty(placement)) return;
            try
            {
                var o = string.IsNullOrEmpty(replyJson) ? null : JObject.Parse(replyJson);
                if (o == null) return;
                if (!_state.TryGetValue(placement, out var p))
                    _state[placement] = p = new Placement { Id = placement };
                if (o["left"] != null) p.Left = (int)o["left"];
                if (o["charges"] != null) p.Charges = (int)o["charges"];
                if (o["ready_at"] != null) p.ReadyAtUnix = (long)o["ready_at"];
                if (o["currency"] != null) p.Currency = (string)o["currency"];
                if (o["amount"] != null) p.Amount = (long)o["amount"];
                Changed?.Invoke();
            }
            catch { /* ответ не разобрался — кнопка останется с прежним состоянием */ }
        }

        /// <summary>Запомнить каталог: у кнопки должно быть состояние ДО первого
        /// показа, иначе она рисует себя доступной и обманывает.</summary>
        public static void NoteCatalog(List<Placement> catalog)
        {
            if (catalog == null) return;
            foreach (var p in catalog)
                if (!string.IsNullOrEmpty(p?.Id)) _state[p.Id] = p;
            Changed?.Invoke();
        }
    }
}
