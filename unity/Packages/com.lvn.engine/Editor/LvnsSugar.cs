using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Lvn.Editor
{
    /// <summary>
    /// Конструкции времени компиляции — ЗЕРКАЛО Go-компилятора.
    ///
    /// <para>Скрипт собирают два разных инструмента: <c>lvnconv</c> (Go — на
    /// сервере, в пайплайне, в панели автора) и <see cref="LvnsCompiler"/> (C# —
    /// в редакторе Unity). Всё, что один разворачивает, а другой нет, — это
    /// ТИХИЙ отказ худшего рода: автор пишет правильную строку и получает
    /// разный результат в зависимости от того, кто собирал. Причём разный не
    /// «чуть-чуть»: неразвёрнутая строка не остаётся собой, она становится
    /// РЕПЛИКОЙ и произносится вслух персонажем.</para>
    ///
    /// <para>Так и было до этого файла: <c>achieve</c>, <c>grid</c> и
    /// привязки <c>near=</c> работали только через Go. Расхождение поймал
    /// golden-тест на фикстурах, и он же держит их в согласии дальше — любая
    /// правка в Go без парной правки здесь красит тест.</para>
    ///
    /// <para>Соответствие файлов: <c>achieve.go</c>, <c>relations.go</c>,
    /// <c>grid.go</c> из <c>tools/lvnconv/internal/lvns/</c>.</para>
    /// </summary>
    static class LvnsSugar
    {
        // ── достижения (зеркало achieve.go) ──────────────────────────────────

        static readonly Regex ReAchieve = new Regex(
            "^\\s*(?:achieve|достижение)\\s+([^\\s\"]+)\\s+\"([^\"]*)\"(?:\\s+\"([^\"]*)\")?\\s*$");

        /// <summary>`achieve id "Название" ["Описание"]` → запись в состояние.
        /// Достижение — не команда сцены, а состояние игрока: его хранят между
        /// новеллами, показывают отдельным экраном и синхронизируют с сервером.
        /// Всё это уже умеют межновелльные переменные.</summary>
        public static string ExpandAchievements(string src)
        {
            string[] lines = SplitLines(src);
            var outLines = new List<string>(lines.Length);
            foreach (string line in lines)
            {
                Match m = ReAchieve.Match(line);
                if (!m.Success) { outLines.Add(line); continue; }
                string id = m.Groups[1].Value, title = m.Groups[2].Value, desc = m.Groups[3].Value;
                var b = new StringBuilder();
                b.Append("set global.ach_").Append(id).Append(" = ").Append(Quote(title));
                if (!string.IsNullOrEmpty(desc))
                    b.Append('\n').Append("set global.achd_").Append(id).Append(" = ").Append(Quote(desc));
                outLines.Add(b.ToString());
            }
            return string.Join("\n", outLines.ToArray());
        }

        // ── отношения (зеркало relations.go) ─────────────────────────────────

        struct RelBody { public double X, Y, Z, SizeY, SizeXZ; }

        /// <summary>`near=фонарь dist=1.4 side=left`, `on=надгробие` →
        /// вычисленный `pos`. Автор думает «скамья у фонаря», а не в метрах;
        /// перевод — механическая работа, в которой ошибаются тем чаще, чем
        /// больше в сцене предметов.</summary>
        public static string ExpandRelations(string src)
        {
            string[] lines = SplitLines(src);
            var outLines = new List<string>(lines);
            var known = new Dictionary<string, RelBody>();

            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                if (!t.StartsWith("o3d ")) continue;
                string id = FieldId(t);
                string near = StripQ(Field(t, "near"));
                string on = StripQ(Field(t, "on"));

                if (near.Length == 0 && on.Length == 0)
                {
                    double px, py, pz;
                    if (Point(t, "pos", out px, out py, out pz))
                        known[id] = new RelBody { X = px, Y = py, Z = pz,
                                                  SizeY = SizeAxis(t, 1), SizeXZ = SizeFirst(t) };
                    continue;
                }

                string anchor = near.Length > 0 ? near : on;
                RelBody b;
                if (!known.TryGetValue(anchor, out b)) continue;   // привязка только к тому, что ВЫШЕ

                double x = b.X, y = b.Y, z = b.Z;
                if (on.Length > 0)
                {
                    y = b.Y + b.SizeY;      // «на» — это поверх, а не внутрь
                }
                else
                {
                    double dist = Num(t, "dist");
                    if (dist == 0) dist = b.SizeXZ * 0.5 + 0.5;   // вплотную, но не внутрь
                    switch (StripQ(Field(t, "side")))
                    {
                        case "left": case "слева": x -= dist; break;
                        case "right": case "справа": x += dist; break;
                        case "back": case "сзади": case "behind": z += dist; break;
                        default: z -= dist; break;                // ближе к камере: она смотрит вдоль +Z
                    }
                }

                string line = SetField(lines[i], "pos",
                    Trim(x) + "," + Trim(y) + "," + Trim(z));
                line = DropFields(line, "near", "on", "dist", "side");
                outLines[i] = line;
                known[id] = new RelBody { X = x, Y = y, Z = z,
                                          SizeY = SizeAxis(t, 1), SizeXZ = SizeFirst(t) };
            }
            return string.Join("\n", outLines.ToArray());
        }

        // ── сетка (зеркало grid.go) ──────────────────────────────────────────

        static readonly Regex ReGrid = new Regex(
            "^\\s*(?:grid|сетка)\\s+(off|выкл|[0-9]*\\.?[0-9]+)(?:\\s+(?:sub|под)\\s+([0-9]+))?\\s*$");
        static readonly Regex ReSizeField = new Regex("\\bsize=\"?([0-9]*\\.?[0-9]+)");

        static readonly HashSet<string> GridPointFields = new HashSet<string> { "pos", "at" };
        static readonly HashSet<string> GridScalarFields = new HashSet<string> { "x", "y", "z", "gap" };
        static readonly HashSet<string> GridAreaFields = new HashSet<string> { "area" };

        /// <summary>`grid 2 sub 10` — координаты клетками вместо метров.
        /// Крупное меряется клетками, мелочь — подклетками; движок различает их
        /// по габариту тела, а не по слову автора.</summary>
        public static string ExpandGrid(string src)
        {
            string[] lines = SplitLines(src);
            var outLines = new List<string>(lines.Length);
            double cell = 0;   // 0 — сетка выключена, координаты уже в метрах
            int sub = 0;

            foreach (string line in lines)
            {
                Match g = ReGrid.Match(line);
                if (g.Success)
                {
                    string v = g.Groups[1].Value;
                    if (v == "off" || v == "выкл") { cell = 0; sub = 0; }
                    else
                    {
                        sub = 0;
                        int n;
                        if (g.Groups[2].Value.Length > 0 &&
                            int.TryParse(g.Groups[2].Value, out n) && n > 1) sub = n;
                        double f;
                        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out f) && f > 0)
                            cell = f;
                    }
                    outLines.Add("");   // директива не оставляет команды
                    continue;
                }
                outLines.Add(cell <= 0 ? line : GridLine(line, cell, sub));
            }
            return string.Join("\n", outLines.ToArray());
        }

        static string GridLine(string line, double cell, int sub)
        {
            string t = line.TrimStart();
            if (!(t.StartsWith("o3d ") || t.StartsWith("bg3d ") || t.StartsWith("light "))) return line;

            string[] tokens = SplitTokens(line);
            for (int i = 0; i < tokens.Length; i++)
            {
                int eq = tokens[i].IndexOf('=');
                if (eq <= 0) continue;
                string key = tokens[i].Substring(0, eq);
                string val = StripQ(tokens[i].Substring(eq + 1));
                if (GridPointFields.Contains(key))
                    tokens[i] = key + "=" + Quote(ScalePoints(val, cell, sub, key == "at"));
                else if (GridAreaFields.Contains(key))
                    tokens[i] = key + "=" + Quote(ScaleList(val, cell));
                else if (GridScalarFields.Contains(key))
                {
                    double f;
                    if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out f))
                        tokens[i] = key + "=" + Trim(f * cell);
                }
            }
            return string.Join(" ", tokens);
        }

        static string ScalePoints(string v, double cell, int sub, bool isList)
        {
            if (!isList) return ScaleOne(v, cell, sub);
            string[] items = v.Split(';');
            for (int i = 0; i < items.Length; i++) items[i] = ScaleOne(items[i], cell, sub);
            return string.Join(";", items);
        }

        static string ScaleOne(string one, double cell, int sub)
        {
            string[] parts = one.Split(',');
            var res = new string[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                double f;
                if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out f))
                    return one;
                // Прилипание к подсетке: без него координаты копятся мусорными
                // хвостами и проверка занятости перестаёт что-либо значить.
                if (sub > 0) f = System.Math.Round(f * sub) / sub;
                res[i] = Trim(f * cell);
            }
            return string.Join(",", res);
        }

        static string ScaleList(string v, double cell)
        {
            string[] parts = v.Split(',');
            var res = new string[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                double f;
                if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out f))
                    return v;
                res[i] = Trim(f * cell);
            }
            return string.Join(",", res);
        }

        // ── общие мелочи ─────────────────────────────────────────────────────

        static string[] SplitLines(string s) =>
            s.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

        static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

        static string StripQ(string s) =>
            s != null && s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"'
                ? s.Substring(1, s.Length - 2) : (s ?? "");

        static string Field(string line, string key)
        {
            Match m = new Regex("\\b" + Regex.Escape(key) + "=(\"[^\"]*\"|\\S+)").Match(line);
            return m.Success ? m.Groups[1].Value : "";
        }

        static string FieldId(string line)
        {
            string v = Field(line, "id");
            return v.Length > 0 ? StripQ(v) : "без имени";
        }

        static double Num(string line, string key)
        {
            double f;
            return double.TryParse(StripQ(Field(line, key)), NumberStyles.Float,
                CultureInfo.InvariantCulture, out f) ? f : 0;
        }

        static bool Point(string line, string key, out double x, out double y, out double z)
        {
            x = y = z = 0;
            string[] p = StripQ(Field(line, key)).Split(',');
            if (p.Length < 3) return false;
            return double.TryParse(p[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                && double.TryParse(p[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                && double.TryParse(p[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out z);
        }

        static double SizeAxis(string line, int axis)
        {
            string v = StripQ(Field(line, "size"));
            if (v.Length == 0) return 0;
            string[] p = v.Split(',');
            double f;
            if (p.Length == 1)
                return double.TryParse(p[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out f) ? f : 0;
            if (axis >= p.Length) return 0;
            return double.TryParse(p[axis].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out f) ? f : 0;
        }

        static double SizeFirst(string line) => SizeAxis(line, 0);

        static string SetField(string line, string key, string value)
        {
            var re = new Regex("\\b" + Regex.Escape(key) + "=(\"[^\"]*\"|\\S+)");
            if (re.IsMatch(line)) return re.Replace(line, key + "=\"" + value + "\"");
            return line.TrimEnd(' ') + " " + key + "=" + Quote(value);
        }

        static string DropFields(string line, params string[] keys)
        {
            foreach (string k in keys)
                line = new Regex("\\s*\\b" + Regex.Escape(k) + "=(\"[^\"]*\"|\\S+)").Replace(line, "");
            return line;
        }

        // Токены строки команды: пробел внутри кавычек не разделяет.
        static string[] SplitTokens(string s)
        {
            var outList = new List<string>();
            var cur = new StringBuilder();
            bool inQuote = false;
            foreach (char r in s)
            {
                if (r == '"') { inQuote = !inQuote; cur.Append(r); }
                else if (r == ' ' && !inQuote)
                {
                    if (cur.Length > 0) { outList.Add(cur.ToString()); cur.Length = 0; }
                }
                else cur.Append(r);
            }
            if (cur.Length > 0) outList.Add(cur.ToString());
            return outList.ToArray();
        }

        /// <summary>Число в точности так, как печатает Go (trimFloat).</summary>
        static string Trim(double f)
        {
            f = System.Math.Round(f * 10000.0) / 10000.0;
            return f.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
