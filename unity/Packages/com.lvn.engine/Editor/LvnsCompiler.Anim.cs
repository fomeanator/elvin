using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Lvn.Editor
{
    /// <summary>
    /// ЗАПИСЬ АНИМАЦИИ — как читается авторская строка `anim`.
    ///
    /// <para>У неё две формы: по местам (`anim hero left 2s yoyo`) и по ключам
    /// (`anim id=hero to=left dur=2`), и обе сводятся к одной команде. Отдельная
    /// тема, потому что здесь живут СЛОВА автора — «yoyo», «loop», «2s», путь в
    /// квадратных скобках, — а не устройство команд.</para>
    /// </summary>
    public static partial class LvnsCompiler
    {
        static bool IsDur(string t)
        {
            if (!t.EndsWith("s") || t.Length < 2) return false;
            return double.TryParse(t.Substring(0, t.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        }

        static bool IsAnimWord(string t) =>
            t == "yoyo" || t == "loop" || t == "pingpong" || t == "stop" || IsDur(t);

        static Dictionary<string, object> ParseAnimPositional(string op, string rest)
        {
            var p = new Dictionary<string, object>();
            string[] bracket = null;
            int lb = rest.IndexOf('[');
            if (lb >= 0)
            {
                int rel = rest.Substring(lb).IndexOf(']');
                if (rel < 0) throw new LvnsCompileException("unclosed '[' in keys");
                bracket = SplitFields(rest.Substring(lb + 1, rel - 1).Trim());
                rest = (rest.Substring(0, lb) + " " + rest.Substring(lb + rel + 1)).Trim();
            }
            string[] toks = SplitFields(rest);
            if (toks.Length == 0) throw new LvnsCompileException("need an id");
            p["id"] = toks[0];
            int idx = 1;
            if (op == "anim" && idx < toks.Length && !toks[idx].Contains("=") &&
                !IsAnimWord(toks[idx]) && !toks[idx].Contains(":"))
            {
                p["prop"] = toks[idx];
                idx++;
            }
            var inlineKeys = new List<string>();
            for (int t = idx; t < toks.Length; t++)
            {
                string tok = toks[t];
                if (tok.Contains("="))
                {
                    int e = tok.IndexOf('=');
                    p[tok.Substring(0, e)] = ScalarVal(tok.Substring(e + 1));
                }
                else if (IsDur(tok))
                {
                    double dv = double.Parse(tok.Substring(0, tok.Length - 1), CultureInfo.InvariantCulture);
                    p["dur"] = dv;
                }
                else if (tok == "yoyo" || tok == "loop" || tok == "pingpong")
                {
                    p["loop"] = tok;
                }
                else if (tok == "stop")
                {
                    p["stop"] = true;
                }
                else if (tok.Contains(":"))
                {
                    inlineKeys.Add(tok);
                }
                else if (op == "move")
                {
                    if (p.TryGetValue("path", out object cur) && cur is string cs)
                        p["path"] = cs + " " + tok;
                    else
                        p["path"] = tok;
                }
            }
            if (inlineKeys.Count > 0)
            {
                p["keys"] = string.Join(" ", inlineKeys);
            }
            else if (bracket != null && bracket.Length > 0)
            {
                double d = 1.0;
                if (NumParam(p.TryGetValue("dur", out var dd) ? dd : null, out double dv) && dv > 0) d = dv;
                int nn = bracket.Length;
                var parts = new string[nn];
                for (int k = 0; k < nn; k++)
                {
                    double tt = 0.0;
                    if (nn > 1) tt = (double)k / (nn - 1) * d;
                    parts[k] = G(tt) + ":" + bracket[k];
                }
                p["keys"] = string.Join(" ", parts);
            }
            return p;
        }

        static double[] ParseAnimKeysMaxT(string s, out JArray keys)
        {
            keys = new JArray();
            double maxT = 0;
            foreach (string tok in SplitFields(s))
            {
                string[] parts = tok.Split(new[] { ':' }, 2);
                if (parts.Length != 2)
                    throw new LvnsCompileException($"bad keyframe \"{tok}\" (want t:v)");
                if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double t))
                    throw new LvnsCompileException($"bad time in \"{tok}\"");
                if (!double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    throw new LvnsCompileException($"bad value in \"{tok}\"");
                keys.Add(new JArray { t, v });
                if (t > maxT) maxT = t;
            }
            if (keys.Count == 0) throw new LvnsCompileException("no keyframes");
            return new[] { maxT };
        }

        static JArray ParsePathPoints(string s)
        {
            var pts = new JArray();
            int count = 0;
            foreach (string tok in SplitFields(s))
            {
                string[] parts = tok.Split(new[] { ',' }, 2);
                if (parts.Length != 2)
                    throw new LvnsCompileException($"bad point \"{tok}\" (want x,y)");
                if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double x))
                    throw new LvnsCompileException($"bad x in \"{tok}\"");
                if (!double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
                    throw new LvnsCompileException($"bad y in \"{tok}\"");
                pts.Add(new JArray { x, y });
                count++;
            }
            if (count < 2) throw new LvnsCompileException("path needs at least 2 points");
            return pts;
        }

        static JObject BuildAnimCmd(string op, Dictionary<string, object> p)
        {
            string id = p.TryGetValue("id", out var idv) ? idv as string : null;
            if (string.IsNullOrEmpty(id))
                throw new LvnsCompileException($"{op}: id required");

            // Stop form
            if (p.TryGetValue("stop", out object sv))
            {
                bool isBool = sv is bool;
                bool b = sv is bool bb && bb;
                if (!isBool || b)
                {
                    string target = "all";
                    if (sv is string ss && ss != "" && ss != "true") target = ss;
                    return new JObject { ["op"] = "anim", ["id"] = id, ["stop"] = target };
                }
            }

            string channel = p.TryGetValue("channel", out var ch) ? ch as string : null;
            string mode = p.TryGetValue("mode", out var md) ? md as string : null;
            ParseLoop(p.TryGetValue("loop", out var lp) ? lp : null, out bool loop, out bool yoyo);
            string ease = p.TryGetValue("ease", out var es) ? es as string : null;
            string interp = p.TryGetValue("interp", out var ip) ? ip as string : null;
            // Опечатка в способе сглаживания — ОШИБКА, а не молчание. Рантайм
            // считает незнакомое значение линейным, и кривая автора молча
            // выпрямляется: он видит не ошибку, а «анимация какая-то не такая».
            // Go отвечает на это ошибкой с номером строки; редакторный путь
            // переносил значение как есть.
            if (!string.IsNullOrEmpty(interp)
                && interp != "linear" && interp != "spline" && interp != "step")
                throw new LvnsCompileException($"{op}: interp=\"{interp}\" is not linear|spline|step");
            bool durSet = NumParam(p.TryGetValue("dur", out var du) ? du : null, out double dur);

            JObject WithShaping(JObject tr)
            {
                if (!string.IsNullOrEmpty(ease)) tr["ease"] = ease;
                if (!string.IsNullOrEmpty(interp)) tr["interp"] = interp;
                return tr;
            }

            var tracks = new JArray();
            double duration;

            if (op == "move")
            {
                double d = dur;
                if (!durSet || d <= 0) d = 1;
                var xs = new JArray();
                var ys = new JArray();
                if (p.TryGetValue("to", out var toObj) && toObj is string to && to != "")
                {
                    JArray pt = ParsePathPoints(to + " " + to);
                    var p0 = (JArray)pt[0];
                    xs.Add(new JArray { 0.0, 0.0 });
                    xs.Add(new JArray { d, p0[0] });
                    ys.Add(new JArray { 0.0, 0.0 });
                    ys.Add(new JArray { d, p0[1] });
                }
                else
                {
                    string pathStr = p.TryGetValue("path", out var pa) ? pa as string : null;
                    JArray pts = ParsePathPoints(pathStr ?? "");
                    int nn = pts.Count;
                    for (int k = 0; k < nn; k++)
                    {
                        var pk = (JArray)pts[k];
                        double t = 0.0;
                        if (nn > 1) t = (double)k / (nn - 1) * d;
                        xs.Add(new JArray { t, pk[0] });
                        ys.Add(new JArray { t, pk[1] });
                    }
                }
                tracks.Add(WithShaping(new JObject { ["prop"] = "screen_x", ["keys"] = xs }));
                tracks.Add(WithShaping(new JObject { ["prop"] = "screen_y", ["keys"] = ys }));
                duration = d;
                if (p.TryGetValue("orient", out var orv) && orv is bool ob && ob)
                    ((JObject)tracks[0])["orient"] = true;
            }
            else // anim
            {
                string prop = p.TryGetValue("prop", out var pr) ? pr as string : null;
                if (string.IsNullOrEmpty(prop))
                    throw new LvnsCompileException("anim: prop required");
                var tr = new JObject { ["prop"] = prop };
                // to="{выражение}" — цель СЧИТАЕТСЯ, а не задана числом. Так
                // тянут полосу здоровья к доле, которую ещё предстоит
                // вычислить. Компилятор выражение не трогает: считать его
                // здесь нечем, переменные появятся только во время игры. Он
                // лишь переносит его в трек, а игрок подставляет число перед
                // самым запуском (ResolveAnimTargets).
                //
                // Редакторный путь этого не умел вовсе: число не разбиралось,
                // управление уходило в ветку ключей, и глава ПАДАЛА с «no
                // keyframes». Та же глава через CLI собиралась.
                var toRaw = p.TryGetValue("to", out var tov) ? tov : null;
                if (toRaw is string toExpr && toExpr.IndexOf('{') >= 0)
                {
                    double d = dur;
                    if (!durSet || d <= 0) d = 1;
                    var rest = PropIdentity(prop);
                    tr["to_expr"] = toExpr.Trim();
                    tr["keys"] = new JArray { new JArray { 0.0, rest }, new JArray { d, rest } };
                    duration = d;
                }
                else if (NumParam(toRaw, out double toNum))
                {
                    double d = dur;
                    if (!durSet || d <= 0) d = 1;
                    tr["keys"] = new JArray { new JArray { 0.0, PropIdentity(prop) }, new JArray { d, toNum } };
                    duration = d;
                }
                else
                {
                    string keysStr = p.TryGetValue("keys", out var ks) ? ks as string : null;
                    double maxT = ParseAnimKeysMaxT(keysStr ?? "", out JArray keys)[0];
                    tr["keys"] = keys;
                    duration = maxT;
                    if (durSet && dur > 0) duration = dur;
                }
                if (p.TryGetValue("layer", out var ly) && ly is string lstr && lstr != "")
                    tr["layer"] = lstr;
                tracks.Add(WithShaping(tr));
            }

            var anim = new JObject { ["loop"] = loop, ["duration"] = duration, ["tracks"] = tracks };
            if (yoyo) anim["yoyo"] = true;
            var cmd = new JObject { ["op"] = "anim", ["id"] = id, ["anim"] = anim };
            if (!string.IsNullOrEmpty(channel)) cmd["channel"] = channel;
            if (!string.IsNullOrEmpty(mode)) cmd["mode"] = mode;
            return cmd;
        }
    }
}
