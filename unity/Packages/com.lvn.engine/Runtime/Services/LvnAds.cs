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
        }

        /// <summary>The server's rewarded placements (GET /v1/ads/catalog).
        /// Null offline.</summary>
        public static async Task<List<Placement>> GetCatalogAsync()
        {
            var (code, body) = await LvnBackend.GetAsync("/v1/ads/catalog");
            if (code != 200 || string.IsNullOrEmpty(body)) return null;
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
                    });
                }
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

            var (code, _) = await LvnBackend.PostAsync("/v1/ads/reward",
                new JObject { ["placement"] = placement }.ToString());
            LvnAnalytics.Track(code == 200 ? "ad_reward" : "ad_reward_fail", ("placement", placement));
            if (code != 200) return false;
            await LvnWallet.RefreshAsync(); // the grant lands in the pills immediately
            return true;
        }
    }
}
