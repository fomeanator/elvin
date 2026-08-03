using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Lvn.UI.World
{
    /// <summary>
    /// Модель ТЕКСТОМ: разбор формата OBJ прямо в игре.
    ///
    /// <para>Зачем это движку новеллы. Всё остальное в сцене автор описывает
    /// словами — тело, свет, погоду, — и только модель до сих пор требовала
    /// собранного набора, то есть открытого Unity и человека, который умеет им
    /// пользоваться. Между «опиши сцену текстом» и «попроси нейросеть сделать
    /// надгробие» стояла стена.</para>
    ///
    /// <para>OBJ выбран именно потому, что он ТЕКСТОВЫЙ и древний: его умеет
    /// экспортировать всё, включая языковые модели, которым проще выписать
    /// список вершин, чем собрать двоичный файл. Модель в полсотни граней —
    /// это тридцать строк, которые можно прочитать глазами и починить руками.
    /// Для стиля, где геометрия всё равно приводится к нашему освещению, этого
    /// достаточно: силуэт решает, а число граней почти нет.</para>
    ///
    /// <para>Разбирается намеренно небольшое подмножество: вершины, нормали,
    /// координаты текстуры и грани. Материалы (mtl) не читаются вовсе — вид
    /// объекта задаёт скрипт, как и у всего остального в сцене.</para>
    /// </summary>
    public static class LvnObjMesh
    {
        // Один и тот же камень ставят десятками — разбирать его текст каждый
        // раз значит платить за одно и то же.
        private static readonly Dictionary<string, Mesh> _cache = new Dictionary<string, Mesh>();

        public static Mesh Cached(string key) =>
            key != null && _cache.TryGetValue(key, out var m) && m != null ? m : null;

        /// <summary>Похоже ли это на ссылку на файл модели, а не на имя объекта
        /// внутри набора.</summary>
        public static bool LooksLikePath(string s) =>
            !string.IsNullOrEmpty(s) &&
            (s.EndsWith(".obj", System.StringComparison.OrdinalIgnoreCase) ||
             s.EndsWith(".OBJ", System.StringComparison.Ordinal));

        /// <summary>Разобрать текст OBJ в меш. null — если в тексте нет ни одной
        /// грани (обычно это значит, что скачалась страница ошибки, а не файл).</summary>
        public static Mesh Parse(string text, string name)
        {
            if (string.IsNullOrEmpty(text)) return null;

            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();

            // Готовые вершины: в OBJ одна позиция может встречаться с разными
            // нормалями и развёртками, а в меше вершина — это тройка целиком.
            var verts = new List<Vector3>();
            var vnorm = new List<Vector3>();
            var vuv = new List<Vector2>();
            var tris = new List<int>();
            var seen = new Dictionary<string, int>();

            var inv = CultureInfo.InvariantCulture;
            var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var t = line.Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries);
                if (t.Length == 0) continue;

                switch (t[0])
                {
                    case "v":
                        if (t.Length >= 4) positions.Add(new Vector3(
                            F(t[1], inv), F(t[2], inv), F(t[3], inv)));
                        break;
                    case "vn":
                        if (t.Length >= 4) normals.Add(new Vector3(
                            F(t[1], inv), F(t[2], inv), F(t[3], inv)));
                        break;
                    case "vt":
                        if (t.Length >= 3) uvs.Add(new Vector2(F(t[1], inv), F(t[2], inv)));
                        break;
                    case "f":
                    {
                        // Грань может быть не только треугольником: четырёхугольники
                        // в ручных моделях обычное дело. Режем веером от первой
                        // вершины — для выпуклых граней это верно, а невыпуклые в
                        // модели на полсотни граней не встречаются.
                        int n = t.Length - 1;
                        if (n < 3) break;
                        var idx = new int[n];
                        for (int i = 0; i < n; i++)
                            idx[i] = Vertex(t[i + 1], positions, normals, uvs,
                                            verts, vnorm, vuv, seen);
                        for (int i = 1; i < n - 1; i++)
                        {
                            tris.Add(idx[0]); tris.Add(idx[i]); tris.Add(idx[i + 1]);
                        }
                        break;
                    }
                }
            }

            if (tris.Count == 0) return null;

            var mesh = new Mesh { name = name ?? "lvn-obj" };
            mesh.indexFormat = verts.Count > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(verts);
            if (vnorm.Count == verts.Count) mesh.SetNormals(vnorm);
            if (vuv.Count == verts.Count) mesh.SetUVs(0, vuv);
            mesh.SetTriangles(tris, 0);
            // Нормалей может не быть вовсе — их часто не пишут. Считаем сами:
            // без них модель освещается плоско и выглядит бумажной.
            if (vnorm.Count != verts.Count) mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Разобрать и запомнить под ключом (обычно это url).</summary>
        public static Mesh ParseCached(string key, string text)
        {
            var hit = Cached(key);
            if (hit != null) return hit;
            var mesh = Parse(text, key);
            if (mesh != null && key != null) _cache[key] = mesh;
            return mesh;
        }

        /// <summary>Забыть разобранное: сменилась глава, модели больше не нужны.</summary>
        public static void Clear()
        {
            foreach (var m in _cache.Values)
                if (m != null) Object.Destroy(m);
            _cache.Clear();
        }

        private static float F(string s, CultureInfo inv) =>
            float.TryParse(s, NumberStyles.Float, inv, out var f) ? f : 0f;

        // «12/7/3» — позиция/развёртка/нормаль, любые части могут отсутствовать.
        private static int Vertex(string token,
            List<Vector3> positions, List<Vector3> normals, List<Vector2> uvs,
            List<Vector3> verts, List<Vector3> vnorm, List<Vector2> vuv,
            Dictionary<string, int> seen)
        {
            if (seen.TryGetValue(token, out var found)) return found;

            var parts = token.Split('/');
            int pi = Index(parts.Length > 0 ? parts[0] : null, positions.Count);
            int ti = Index(parts.Length > 1 ? parts[1] : null, uvs.Count);
            int ni = Index(parts.Length > 2 ? parts[2] : null, normals.Count);

            verts.Add(pi >= 0 ? positions[pi] : Vector3.zero);
            if (ni >= 0) vnorm.Add(normals[ni]);
            if (ti >= 0) vuv.Add(uvs[ti]);

            int id = verts.Count - 1;
            seen[token] = id;
            return id;
        }

        // Номера в OBJ начинаются с единицы, а отрицательные считаются с конца.
        private static int Index(string s, int count)
        {
            if (string.IsNullOrEmpty(s)) return -1;
            if (!int.TryParse(s, out var i)) return -1;
            if (i > 0) return Mathf.Min(i - 1, count - 1);
            if (i < 0) return Mathf.Max(0, count + i);
            return -1;
        }
    }
}
