using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Lvn.Services
{
    /// <summary>
    /// Named leaderboards: best score wins, ties go to the earlier claim.
    /// Top-N comes with the caller's own rank — the number players care about.
    /// </summary>
    public static class LvnLeaderboard
    {
        public sealed class Entry
        {
            public string Name;
            public long Score;
        }

        public sealed class Top
        {
            public List<Entry> Entries = new List<Entry>();
            public int Total;
            public int MyRank;   // 0 = not on the board / anonymous
            public long MyScore;
        }

        /// <summary>Submit a score (server keeps the best); returns the fresh
        /// rank, or 0 offline.</summary>
        public static async Task<int> SubmitAsync(string board, long score, string displayName = null)
        {
            var payload = new JObject { ["score"] = score };
            if (!string.IsNullOrEmpty(displayName)) payload["name"] = displayName;
            var (code, body) = await LvnBackend.PostAsync("/v1/leaderboard/" + board, payload.ToString());
            if (code != 200 || string.IsNullOrEmpty(body)) return 0;
            try { return (int?)JObject.Parse(body)["rank"] ?? 0; } catch { return 0; }
        }

        public static async Task<Top> GetTopAsync(string board, int n = 10)
        {
            var (code, body) = await LvnBackend.GetAsync($"/v1/leaderboard/{board}?n={n}");
            if (code != 200 || string.IsNullOrEmpty(body)) return null;
            try
            {
                var d = JObject.Parse(body);
                var top = new Top { Total = (int?)d["total"] ?? 0 };
                foreach (var e in d["top"] as JArray ?? new JArray())
                    top.Entries.Add(new Entry { Name = (string)e["name"], Score = (long?)e["score"] ?? 0 });
                if (d["me"] is JObject me)
                {
                    top.MyRank = (int?)me["rank"] ?? 0;
                    top.MyScore = (long?)me["score"] ?? 0;
                }
                return top;
            }
            catch { return null; }
        }
    }
}
