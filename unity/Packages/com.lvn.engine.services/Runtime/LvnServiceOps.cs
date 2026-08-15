using Lvn;
using Newtonsoft.Json.Linq;

namespace Lvn.Services
{
    /// <summary>
    /// Script-facing bridges to the product services — one registration call
    /// and a writer talks to the backend from .lvns:
    ///
    ///   ext wallet_earn currency=gold amount=10 reason="quest"
    ///   ext wallet_spend currency=gold amount=5 reason="shop" sku=sword
    ///   ext leaderboard_submit board=quiz_score score_var=score name_var=player_name
    ///   ext daily_claim
    ///   ext track name=secret_found
    ///
    /// All fire-and-forget and offline-safe: the story never blocks on the
    /// network. NovelApp registers these automatically; a custom host calls
    /// <see cref="RegisterAll"/> once (or picks its own ops via LvnOps).
    /// </summary>
    public static class LvnServiceOps
    {
        private static bool _done;

        public static void RegisterAll()
        {
            if (_done) return;
            _done = true;

            LvnOps.Register("wallet_earn", (cmd, ctx) =>
            {
                var (cur, amt) = MoneyArgs(cmd, ctx.Vars);
                if (amt > 0) _ = LvnWallet.EarnAsync(cur, amt, (string)cmd["reason"] ?? "script");
            });

            LvnOps.Register("wallet_spend", (cmd, ctx) =>
            {
                var (cur, amt) = MoneyArgs(cmd, ctx.Vars);
                if (amt > 0) _ = LvnWallet.SpendAsync(cur, amt, (string)cmd["reason"] ?? "script", (string)cmd["sku"]);
            });

            LvnOps.Register("leaderboard_submit", (cmd, ctx) =>
            {
                var board = (string)cmd["board"];
                if (string.IsNullOrEmpty(board)) return;
                long score = NumFrom(cmd, "score", "score_var", ctx.Vars);
                string name = null;
                var nameVar = (string)cmd["name_var"];
                if (!string.IsNullOrEmpty(nameVar) && ctx.Vars.TryGetValue(nameVar, out var nv))
                    name = nv?.ToString();
                _ = LvnLeaderboard.SubmitAsync(board, score, name);
            });

            LvnOps.Register("daily_claim", (cmd, ctx) => _ = LvnDaily.ClaimAsync());

            // ext ad_reward placement=gold_small — a story-placed rewarded ad
            // (the wall between chapters, the "double your loot" beat). Holds
            // the script while the ad runs; no ad SDK plugged → no-op flow-on.
            LvnOps.Register("ad_reward", (cmd, ctx) =>
            {
                var placement = (string)cmd["placement"];
                if (string.IsNullOrEmpty(placement) || !LvnAds.Available) return;
                ctx.Hold();
                _ = RunAdAsync(placement, ctx);
            });

            // ── дуэль вдвоём ────────────────────────────────────────────
            // Три опа на весь сетевой бой: завести комнату, войти по коду,
            // обменяться ходом. Правила боя остаются в скрипте — сюда уходит
            // только «что я выбрал» и приходит «что выбрал он».
            LvnOps.Register("duel_host", (cmd, ctx) =>
            {
                ctx.Hold();
                _ = DuelHostAsync(cmd, ctx);
            });

            LvnOps.Register("duel_join", (cmd, ctx) =>
            {
                ctx.Hold();
                _ = DuelJoinAsync(Arg(cmd, "code", ctx.Vars), cmd, ctx);
            });

            // ext duel_exchange round_var=обмен plan_var=мой_план into=его_план
            //
            // Держит скрипт, пока партнёр думает. Это НЕ недостаток: одновременный
            // выбор тем и держится, что никто не видит чужой ход раньше времени,
            // а значит кто-то обязан ждать.
            LvnOps.Register("duel_exchange", (cmd, ctx) =>
            {
                ctx.Hold();
                _ = DuelExchangeAsync(cmd, ctx);
            });

            LvnOps.Register("duel_leave", (cmd, ctx) => LvnDuelOnline.Leave());

            LvnOps.Register("track", (cmd, ctx) =>
            {
                var name = (string)cmd["name"];
                if (!string.IsNullOrEmpty(name)) LvnAnalytics.Track(name);
            });
        }


        // ── дуэль: тела операций ────────────────────────────────────────────
        //
        // Каждая кладёт результат в переменные истории и снимает удержание в
        // finally. Пропущенный Resume означает намертво вставший скрипт, и
        // случиться это должно только при исключении — поэтому finally, а не
        // «в конце удачной ветки».

        private static async System.Threading.Tasks.Task DuelHostAsync(JObject cmd, ILvnOpContext ctx)
        {
            try
            {
                bool ok = await LvnDuelOnline.HostAsync();
                ctx.Vars["duel_code"] = ok ? LvnDuelOnline.Code : "";
                ctx.Vars["duel_seat"] = ok ? LvnDuelOnline.Seat : "";
                ctx.Vars["duel_error"] = ok ? "" : (LvnDuelOnline.LastError ?? "нет связи");
            }
            finally { ctx.Resume(); }
        }

        private static async System.Threading.Tasks.Task DuelJoinAsync(string code, JObject cmd, ILvnOpContext ctx)
        {
            try
            {
                bool ok = await LvnDuelOnline.JoinAsync(code);
                ctx.Vars["duel_code"] = ok ? LvnDuelOnline.Code : "";
                ctx.Vars["duel_seat"] = ok ? LvnDuelOnline.Seat : "";
                ctx.Vars["duel_error"] = ok ? "" : (LvnDuelOnline.LastError ?? "нет связи");
            }
            finally { ctx.Resume(); }
        }

        private static async System.Threading.Tasks.Task DuelExchangeAsync(JObject cmd, ILvnOpContext ctx)
        {
            // Куда положить чужой ход. Имя переменной задаёт автор: пакет дуэли
            // называет её по-своему, и навязывать ему наше имя незачем.
            string into = (string)cmd["into"] ?? "враг_план";
            try
            {
                int round = (int)NumFrom(cmd, "round", "round_var", ctx.Vars);
                string plan = Arg(cmd, "plan", ctx.Vars);
                string theirs = await LvnDuelOnline.ExchangeAsync(round, plan);
                ctx.Vars[into] = theirs ?? "";
                ctx.Vars["duel_error"] = theirs != null ? "" : (LvnDuelOnline.LastError ?? "нет связи");
            }
            finally { ctx.Resume(); }
        }

        /// <summary>Значение параметра: либо прямо (<c>code="A7K2"</c>), либо из
        /// переменной (<c>code_var=введённый</c>). Обе формы нужны: код комнаты
        /// игрок ВВОДИТ, а свой план скрипт собирает сам.</summary>
        private static string Arg(JObject cmd, string name,
                                  System.Collections.Generic.IDictionary<string, JToken> vars)
        {
            var direct = (string)cmd[name];
            if (!string.IsNullOrEmpty(direct)) return direct;
            var byVar = (string)cmd[name + "_var"];
            if (!string.IsNullOrEmpty(byVar) && vars.TryGetValue(byVar, out var v)) return v?.ToString();
            return "";
        }

        private static async System.Threading.Tasks.Task RunAdAsync(string placement, ILvnOpContext ctx)
        {
            try { await LvnAds.WatchAndRewardAsync(placement); }
            finally { ctx.Resume(); }
        }

        private static (string currency, long amount) MoneyArgs(
            JObject cmd, System.Collections.Generic.IDictionary<string, JToken> vars)
        {
            var cur = (string)cmd["currency"] ?? "gold";
            return (cur, NumFrom(cmd, "amount", "amount_var", vars));
        }

        // A literal field, or *_var naming a story variable — the writer's
        // "submit whatever the play earned".
        private static long NumFrom(JObject cmd, string field, string varField,
            System.Collections.Generic.IDictionary<string, JToken> vars)
        {
            var v = cmd[field];
            if (v != null) { try { return (long)v; } catch { } }
            var name = (string)cmd[varField];
            if (!string.IsNullOrEmpty(name) && vars.TryGetValue(name, out var t))
            {
                try { return (long)t; } catch { }
                if (long.TryParse(t?.ToString(), out var parsed)) return parsed;
            }
            return 0;
        }
    }
}
