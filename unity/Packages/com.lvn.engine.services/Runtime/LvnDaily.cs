using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Lvn.Services
{
    /// <summary>
    /// Daily bonus client: one claim per UTC day, streaks handled server-side,
    /// grants land in the wallet (refresh it after a claim). Offline → null.
    /// </summary>
    public static class LvnDaily
    {
        public sealed class Status
        {
            public int Streak;
            public bool ClaimedToday;
            public int NextStreak;
            public string NextCurrency;
            public long NextAmount;
        }

        public static async Task<Status> GetAsync()
        {
            var (code, body) = await LvnBackend.GetAsync("/v1/daily");
            if (code != 200 || string.IsNullOrEmpty(body)) return null;
            try
            {
                var d = JObject.Parse(body);
                return new Status
                {
                    Streak = (int?)d["streak"] ?? 0,
                    ClaimedToday = (bool?)d["claimed_today"] ?? false,
                    NextStreak = (int?)d["next_streak"] ?? 1,
                    NextCurrency = (string)d["next_reward"]?["currency"],
                    NextAmount = (long?)d["next_reward"]?["amount"] ?? 0,
                };
            }
            catch { return null; }
        }

        /// <summary>Claim today's bonus; false when already claimed or offline.
        /// On success the wallet mirror is refreshed automatically.</summary>
        public static async Task<bool> ClaimAsync()
        {
            var (code, _) = await LvnBackend.PostAsync("/v1/daily/claim", "{}");
            if (code != 200) return false;
            await LvnWallet.RefreshAsync();
            return true;
        }
    }
}
