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
                if (amt > 0) LvnAsync.Fire(LvnWallet.EarnAsync(cur, amt, (string)cmd["reason"] ?? "script"), "Earn");
            });

            LvnOps.Register("wallet_spend", (cmd, ctx) =>
            {
                var (cur, amt) = MoneyArgs(cmd, ctx.Vars);
                if (amt > 0) LvnAsync.Fire(LvnWallet.SpendAsync(cur, amt, (string)cmd["reason"] ?? "script", (string)cmd["sku"]), "Spend");
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
                LvnAsync.Fire(LvnLeaderboard.SubmitAsync(board, score, name), "Submit");
            });

            LvnOps.Register("daily_claim", (cmd, ctx) => LvnAsync.Fire(LvnDaily.ClaimAsync(), "Claim"));

            // ext ad_reward placement=gold_small — a story-placed rewarded ad
            // (the wall between chapters, the "double your loot" beat). Holds
            // the script while the ad runs; no ad SDK plugged → no-op flow-on.
            LvnOps.Register("ad_reward", (cmd, ctx) =>
            {
                var placement = (string)cmd["placement"];
                if (string.IsNullOrEmpty(placement) || !LvnAds.Available) return;
                ctx.Hold();
                LvnAsync.Fire(RunAdAsync(placement, ctx), "RunAd");
            });

            // ── КОМНАТА НА ДВОИХ И БОЛЬШЕ ───────────────────────────────
            // Ни одного слова про конкретную игру. Комната, места и ящики с
            // правилом раскрытия — из этого собирается и одновременный выбор
            // (дуэль), и ход по очереди (карты), и гонка «кто первый». Новая
            // игра не требует ни строчки в движке: меняется ключ ящика и
            // правило, а не код.
            LvnOps.Register("net_open", (cmd, ctx) => { ctx.Hold(); LvnAsync.Fire(NetOpenAsync(ctx), "NetOpen"); });

            LvnOps.Register("net_join", (cmd, ctx) =>
            {
                ctx.Hold();
                LvnAsync.Fire(NetJoinAsync(Arg(cmd, "code", ctx.Vars), ctx), "NetJoin");
            });

            // ext net_wait need=2 — держит, пока за стол не сядут все.
            LvnOps.Register("net_wait", (cmd, ctx) =>
            {
                ctx.Hold();
                LvnAsync.Fire(NetWaitAsync((int)NumFrom(cmd, "need", "need_var", ctx.Vars), ctx), "NetWait");
            });

            // ext net_put key="обмен:3" value_var=план reveal=all
            LvnOps.Register("net_put", (cmd, ctx) =>
            {
                ctx.Hold();
                LvnAsync.Fire(NetPutAsync(Arg(cmd, "key", ctx.Vars), Packed(cmd, "value", ctx.Vars),
                                (string)cmd["reveal"] ?? "all", ctx), "NetPut");
            });

            // ext net_get key="обмен:3" into=чужой [one=1] [wait=0]
            //
            // Держит скрипт, пока ящик не откроется. Это НЕ недостаток:
            // одновременный выбор тем и держится, что чужое не видно раньше
            // времени, а значит кто-то обязан ждать.
            LvnOps.Register("net_get", (cmd, ctx) => { ctx.Hold(); LvnAsync.Fire(NetGetAsync(cmd, ctx), "NetGet"); });

            // ext net_rng — ОДИН ПОТОК СЛУЧАЙНОСТИ НА ВСЮ КОМНАТУ.
            //
            // Приём из сетевых игр девяностых: не пересылать случайные числа, а
            // договориться о ЗЕРНЕ. Дальше оба клиента тянут одни и те же числа
            // в одном порядке, и по проводу не идёт ни одного лишнего байта —
            // случайность перестаёт быть источником расхождения.
            //
            // Зерно раздаёт комната при входе, поэтому оп без аргументов: он
            // просто берёт зерно стола и перезапускает с него общий поток.
            LvnOps.Register("net_rng", (cmd, ctx) =>
            {
                if (!LvnNetRoom.InRoom || LvnNetRoom.Seed == 0UL)
                {
                    ctx.Vars["net_error"] = "не в комнате";
                    return;
                }
                LvnExpression.Random = new LvnRandom(LvnNetRoom.Seed);
                ctx.Vars["net_seed"] = LvnNetRoom.Seed.ToString();
                ctx.Vars["net_error"] = "";
            });

            // ext net_check key="сверка:3" — СТОРОЖ РАСХОЖДЕНИЯ.
            //
            // Общий поток спасает от случайности, но не от всего: разные
            // стартовые числа или несимметричное правило всё равно разведут
            // клиентов, и разведут МОЛЧА — каждый будет уверен в своей картине.
            // Старые игры на этот случай раз в несколько ходов сверяли
            // контрольную сумму и честно объявляли рассинхрон, вместо того
            // чтобы дать партии тихо превратиться в две разные.
            //
            // Здесь сверяется отпечаток: зерно, число сделанных бросков и то,
            // что автор сам считает важным (value_var). Разошлось — net_desync
            // становится 1, и сценарий решает, что с этим делать.
            LvnOps.Register("net_check", (cmd, ctx) =>
            {
                ctx.Hold();
                LvnAsync.Fire(NetCheckAsync(cmd, ctx), "NetCheck");
            });

            LvnOps.Register("net_leave", (cmd, ctx) => LvnNetRoom.Leave());

            LvnOps.Register("track", (cmd, ctx) =>
            {
                var name = (string)cmd["name"];
                if (!string.IsNullOrEmpty(name)) LvnAnalytics.Track(name);
            });
        }





        // ── комната: тела операций ──────────────────────────────────────────
        //
        // Каждая кладёт результат в переменные истории и снимает удержание в
        // finally. Пропущенный Resume означает намертво вставший скрипт, и
        // случиться это должно только при исключении — поэтому finally, а не
        // «в конце удачной ветки».

        private static async System.Threading.Tasks.Task NetOpenAsync(ILvnOpContext ctx)
        {
            try { NetState(ctx, await LvnNetRoom.OpenAsync()); }
            finally { ctx.Resume(); }
        }

        private static async System.Threading.Tasks.Task NetJoinAsync(string code, ILvnOpContext ctx)
        {
            try { NetState(ctx, await LvnNetRoom.JoinAsync(code)); }
            finally { ctx.Resume(); }
        }

        private static async System.Threading.Tasks.Task NetWaitAsync(int need, ILvnOpContext ctx)
        {
            try { NetState(ctx, await LvnNetRoom.WaitSeatsAsync(need > 0 ? need : 2)); }
            finally { ctx.Resume(); }
        }

        private static async System.Threading.Tasks.Task NetPutAsync(
            string key, string value, string reveal, ILvnOpContext ctx)
        {
            try
            {
                bool ok = await LvnNetRoom.PutAsync(key, value, reveal);
                ctx.Vars["net_error"] = ok ? "" : (LvnNetRoom.LastError ?? "нет связи");
            }
            finally { ctx.Resume(); }
        }

        private static async System.Threading.Tasks.Task NetGetAsync(JObject cmd, ILvnOpContext ctx)
        {
            string into = (string)cmd["into"] ?? "net_value";
            try
            {
                bool wait = !Off(cmd, "wait");
                var others = await LvnNetRoom.GetAsync(Arg(cmd, "key", ctx.Vars), wait);
                ctx.Vars["net_error"] = others != null ? "" : (LvnNetRoom.LastError ?? "нет связи");
                if (others == null) { ctx.Vars[into] = new JArray(); }
                else if (On(cmd, "one"))
                {
                    // За столом ровно двое — отдаём соседа напрямую, без
                    // лишнего уровня вложенности. Самый частый случай, и
                    // заставлять автора писать get(get(…)) ради него незачем.
                    string first = "";
                    foreach (var kv in others) { first = kv.Value; break; }
                    ctx.Vars[into] = Unpacked(first);
                }
                else
                {
                    var bySeat = new JObject();
                    foreach (var kv in others) bySeat[kv.Key] = Unpacked(kv.Value);
                    ctx.Vars[into] = bySeat;
                }
                // Порядок — то, чего клиент сам не узнает: кто нажал раньше.
                var order = new JArray();
                foreach (var seat in LvnNetRoom.Order) order.Add(seat);
                ctx.Vars["net_order"] = order;
            }
            finally { ctx.Resume(); }
        }

        /// <summary>
        /// Флаг у опа: <c>one=1</c> компилируется ЧИСЛОМ, а не строкой, и
        /// сравнение со строкой молча не срабатывало бы. Принимаем все три
        /// формы, в которых автор может это написать.
        /// </summary>
        // Этот словарь и был самым полным из шести — он переехал в
        // Lvn.LvnBool и стал общим для всего движка.
        private static bool On(JObject cmd, string name) => Lvn.LvnBool.On(cmd[name]);

        /// <summary>Явно выключено. Отсутствие поля — НЕ выключено: у wait
        /// умолчание «ждать», и написать надо ровно <c>wait=0</c>.</summary>
        private static bool Off(JObject cmd, string name) => Lvn.LvnBool.Off(cmd[name]);


        private static async System.Threading.Tasks.Task NetCheckAsync(JObject cmd, ILvnOpContext ctx)
        {
            string key = Arg(cmd, "key", ctx.Vars);
            if (string.IsNullOrEmpty(key)) key = "сверка";
            try
            {
                var rng = LvnExpression.Random;
                // Отпечаток состояния: зерно, позиция потока и добавка автора.
                string mine = rng.Seed + ":" + rng.Draws + ":" + Packed(cmd, "value", ctx.Vars);
                if (!await LvnNetRoom.PutAsync(key, mine, "all"))
                {
                    ctx.Vars["net_error"] = LvnNetRoom.LastError ?? "нет связи";
                    return;
                }
                var others = await LvnNetRoom.GetAsync(key, true);
                if (others == null)
                {
                    ctx.Vars["net_error"] = LvnNetRoom.LastError ?? "нет связи";
                    return;
                }
                bool same = true;
                foreach (var kv in others) if (kv.Value != mine) same = false;
                ctx.Vars["net_desync"] = same ? 0 : 1;
                ctx.Vars["net_error"] = "";
                if (!same)
                    UnityEngine.Debug.LogWarning($"[lvn-net] РАССИНХРОН на «{key}»: у меня {mine}");
            }
            finally { ctx.Resume(); }
        }

        private static void NetState(ILvnOpContext ctx, bool ok)
        {
            ctx.Vars["net_code"] = ok ? LvnNetRoom.Code : "";
            ctx.Vars["net_seat"] = ok ? LvnNetRoom.Seat : "";
            ctx.Vars["net_seats"] = LvnNetRoom.Seats;
            ctx.Vars["net_seed"] = LvnNetRoom.Seed.ToString();
            ctx.Vars["net_error"] = ok ? "" : (LvnNetRoom.LastError ?? "нет связи");
        }

        /// <summary>
        /// Значение на провод: список упаковывается в строку через запятую.
        ///
        /// <para>В скрипте очередь ходов — СПИСОК (его собирают через push при
        /// нажатии кнопок), и работать с ним списком естественно. По сети
        /// список ехать не обязан: строка короче, читается в логах и не требует
        /// от языка ничего нового.</para>
        /// </summary>
        private static string Packed(JObject cmd, string name,
                                     System.Collections.Generic.IDictionary<string, JToken> vars)
        {
            var direct = (string)cmd[name];
            if (!string.IsNullOrEmpty(direct)) return direct;
            var varName = (string)cmd[name + "_var"];
            if (string.IsNullOrEmpty(varName) || !vars.TryGetValue(varName, out var v) || v == null) return "";
            if (v is JArray arr)
            {
                var parts = new string[arr.Count];
                for (int i = 0; i < arr.Count; i++) parts[i] = arr[i]?.ToString() ?? "";
                return string.Join(",", parts);
            }
            return v.ToString();
        }

        /// <summary>Обратно в список — в том виде, в каком его ждёт скрипт.</summary>
        private static JToken Unpacked(string wire)
        {
            var arr = new JArray();
            if (string.IsNullOrEmpty(wire)) return arr;
            foreach (var part in wire.Split(','))
            {
                var t = part.Trim();
                if (t.Length > 0) arr.Add(t);
            }
            return arr;
        }

        /// <summary>Значение параметра: либо прямо (<c>code="A7K2"</c>), либо из
        /// переменной (<c>code_var=введённый</c>). Обе формы нужны: код комнаты
        /// игрок ВВОДИТ, а ключ ящика скрипт собирает сам.</summary>
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
            if (v != null) { try { return (long)v; } catch { } }   // сервис недоступен — оп остаётся тихим, история идёт дальше
            var name = (string)cmd[varField];
            if (!string.IsNullOrEmpty(name) && vars.TryGetValue(name, out var t))
            {
                try { return (long)t; } catch { }   // то же: продуктовый слой не имеет права остановить сцену
                if (long.TryParse(t?.ToString(), out var parsed)) return parsed;
            }
            return 0;
        }
    }
}
