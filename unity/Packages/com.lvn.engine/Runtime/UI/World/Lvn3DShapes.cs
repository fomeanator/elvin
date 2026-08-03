using System.Collections.Generic;
using UnityEngine;

namespace Lvn.UI.World
{
    /// <summary>
    /// Примитивы сцены: коробка, плоскость, шар, цилиндр, конус, диск.
    ///
    /// <para>Строим ВЕРШИНАМИ, а не через <c>GameObject.CreatePrimitive</c>.
    /// Причина не в чистоте: встроенные меши Unity — ресурсы редактора, и в
    /// собранной игре они вырезаются сборщиком, если на них нет прямой ссылки.
    /// Примитив тогда приезжает без геометрии, и объект молча исчезает —
    /// в редакторе всё работает, на устройстве нет. Этот урок уже оплачен
    /// фигурами, которые пропадали в бою.</para>
    ///
    /// <para>Меш на форму ОДИН и общий: двадцать камней ссылаются на одну
    /// геометрию, а размер задаётся масштабом. Это же включает объединение
    /// одинаковых тел в один вызов отрисовки.</para>
    ///
    /// <para>Все примитивы единичные и с началом координат В ОСНОВАНИИ (кроме
    /// шара — у него в центре). Так `pos` в скрипте значит «куда поставить», а
    /// не «где окажется середина», и тело садится на землю само.</para>
    /// </summary>
    public static class Lvn3DShapes
    {
        private static readonly Dictionary<string, Mesh> _cache = new Dictionary<string, Mesh>();

        /// <summary>Меш по имени формы. Неизвестное имя — коробка: сцена должна
        /// показать ЧТО-ТО и дать увидеть опечатку, а не промолчать.</summary>
        public static Mesh Get(string shape)
        {
            var key = string.IsNullOrEmpty(shape) ? "box" : shape.ToLowerInvariant();
            if (_cache.TryGetValue(key, out var m) && m != null) return m;
            switch (key)
            {
                case "plane": m = Plane(); break;
                case "sphere": m = Sphere(); break;
                case "cylinder": m = Tube(1f, 1f); break;
                case "cone": m = Tube(1f, 0f); break;
                case "disc": m = Disc(); break;
                default: m = Box(); break;
            }
            // КАСАТЕЛЬНЫЕ. Шейдеры читают карту нормалей, а для этого нужен
            // тангентный базис — без него Unity подставляет мусор, и освещение
            // на гладких телах уходит в чёрное. Ловилось как «металлический шар
            // чёрный, а куб нормальный»: у куба плоские нормали, и мусор в них
            // менее заметен. Один вызов на форму, не на объект.
            m.RecalculateTangents();
            m.name = "lvn-shape-" + key;
            _cache[key] = m;
            return m;
        }

        public static bool Known(string shape)
        {
            switch ((shape ?? "").ToLowerInvariant())
            {
                case "box": case "plane": case "sphere":
                case "cylinder": case "cone": case "disc": return true;
                default: return false;
            }
        }

        // Куб 1×1×1, основание в нуле. Вершины НЕ переиспользуются между
        // гранями: у каждой свои нормали, иначе куб освещается как шар.
        private static Mesh Box()
        {
            var v = new List<Vector3>();
            var n = new List<Vector3>();
            var uv = new List<Vector2>();
            var tri = new List<int>();

            void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
            {
                int i = v.Count;
                v.Add(a); v.Add(b); v.Add(c); v.Add(d);
                for (int k = 0; k < 4; k++) n.Add(normal);
                uv.Add(new Vector2(0, 0)); uv.Add(new Vector2(0, 1));
                uv.Add(new Vector2(1, 1)); uv.Add(new Vector2(1, 0));
                tri.Add(i); tri.Add(i + 1); tri.Add(i + 2);
                tri.Add(i); tri.Add(i + 2); tri.Add(i + 3);
            }

            const float h = 0.5f;
            // низ 0, верх 1 — основание в нуле
            var p000 = new Vector3(-h, 0f, -h); var p001 = new Vector3(-h, 0f, h);
            var p101 = new Vector3(h, 0f, h); var p100 = new Vector3(h, 0f, -h);
            var q000 = new Vector3(-h, 1f, -h); var q001 = new Vector3(-h, 1f, h);
            var q101 = new Vector3(h, 1f, h); var q100 = new Vector3(h, 1f, -h);

            Face(p000, q000, q100, p100, Vector3.back);
            Face(p100, q100, q101, p101, Vector3.right);
            Face(p101, q101, q001, p001, Vector3.forward);
            Face(p001, q001, q000, p000, Vector3.left);
            Face(q000, q001, q101, q100, Vector3.up);
            Face(p001, p000, p100, p101, Vector3.down);

            var mesh = new Mesh();
            mesh.SetVertices(v); mesh.SetNormals(n); mesh.SetUVs(0, uv);
            mesh.SetTriangles(tri, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // Горизонтальная плоскость 1×1 — пол, стол, вода. Двусторонней НЕ
        // делаем: пол, видимый снизу, стоит вдвое дороже и никому не нужен.
        private static Mesh Plane()
        {
            var mesh = new Mesh();
            mesh.SetVertices(new List<Vector3>
            {
                new Vector3(-0.5f, 0f, -0.5f), new Vector3(-0.5f, 0f, 0.5f),
                new Vector3(0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, -0.5f),
            });
            mesh.SetNormals(new List<Vector3> { Vector3.up, Vector3.up, Vector3.up, Vector3.up });
            mesh.SetUVs(0, new List<Vector2>
            {
                new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0),
            });
            mesh.SetTriangles(new List<int> { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // Диск в плоскости земли — лужа, круглый ковёр, пятно света.
        private static Mesh Disc(int seg = 32)
        {
            var v = new List<Vector3> { Vector3.zero };
            var n = new List<Vector3> { Vector3.up };
            var uv = new List<Vector2> { new Vector2(0.5f, 0.5f) };
            for (int i = 0; i <= seg; i++)
            {
                float a = i / (float)seg * Mathf.PI * 2f;
                float x = Mathf.Cos(a) * 0.5f, z = Mathf.Sin(a) * 0.5f;
                v.Add(new Vector3(x, 0f, z));
                n.Add(Vector3.up);
                uv.Add(new Vector2(x + 0.5f, z + 0.5f));
            }
            var tri = new List<int>();
            for (int i = 1; i <= seg; i++) { tri.Add(0); tri.Add(i); tri.Add(i + 1); }
            var mesh = new Mesh();
            mesh.SetVertices(v); mesh.SetNormals(n); mesh.SetUVs(0, uv);
            mesh.SetTriangles(tri, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // Шар. UV-сфера, 16×24 — грубее того, что рисуют в редакторе, и ровно
        // столько, сколько видно на заднем плане новеллы.
        private static Mesh Sphere(int rings = 16, int seg = 24)
        {
            var v = new List<Vector3>();
            var n = new List<Vector3>();
            var uv = new List<Vector2>();
            for (int y = 0; y <= rings; y++)
            {
                float vf = y / (float)rings;
                float phi = vf * Mathf.PI;
                for (int x = 0; x <= seg; x++)
                {
                    float uf = x / (float)seg;
                    float theta = uf * Mathf.PI * 2f;
                    var p = new Vector3(
                        Mathf.Sin(phi) * Mathf.Cos(theta),
                        Mathf.Cos(phi),
                        Mathf.Sin(phi) * Mathf.Sin(theta)) * 0.5f;
                    v.Add(p);
                    n.Add(p.normalized);
                    uv.Add(new Vector2(uf, 1f - vf));
                }
            }
            var tri = new List<int>();
            int row = seg + 1;
            for (int y = 0; y < rings; y++)
                for (int x = 0; x < seg; x++)
                {
                    int i = y * row + x;
                    // Обход ПО ЧАСОВОЙ при взгляде снаружи — иначе грани
                    // вывернуты: Unity отсекает лицевые, на экран идёт изнанка,
                    // и шар выглядит чёрным при любом освещении. Ловилось как
                    // «металл на кубе работает, на шаре — нет».
                    tri.Add(i); tri.Add(i + row + 1); tri.Add(i + row);
                    tri.Add(i); tri.Add(i + 1); tri.Add(i + row + 1);
                }
            var mesh = new Mesh();
            mesh.SetVertices(v); mesh.SetNormals(n); mesh.SetUVs(0, uv);
            mesh.SetTriangles(tri, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // Цилиндр и конус — одна форма с разным верхним радиусом: ствол,
        // колонна, факел, ель, шляпа. Основание в нуле, высота 1.
        private static Mesh Tube(float rBottom, float rTop, int seg = 24)
        {
            var v = new List<Vector3>();
            var n = new List<Vector3>();
            var uv = new List<Vector2>();
            var tri = new List<int>();

            for (int i = 0; i <= seg; i++)
            {
                float a = i / (float)seg * Mathf.PI * 2f;
                float cx = Mathf.Cos(a), cz = Mathf.Sin(a);
                var nb = new Vector3(cx, (rBottom - rTop), cz).normalized; // скос конуса
                v.Add(new Vector3(cx * rBottom * 0.5f, 0f, cz * rBottom * 0.5f));
                n.Add(nb); uv.Add(new Vector2(i / (float)seg, 0f));
                v.Add(new Vector3(cx * rTop * 0.5f, 1f, cz * rTop * 0.5f));
                n.Add(nb); uv.Add(new Vector2(i / (float)seg, 1f));
            }
            for (int i = 0; i < seg; i++)
            {
                int b = i * 2;
                tri.Add(b); tri.Add(b + 1); tri.Add(b + 3);
                tri.Add(b); tri.Add(b + 3); tri.Add(b + 2);
            }

            void Cap(float y, float r, Vector3 normal, bool up)
            {
                if (r <= 0.0001f) return; // у конуса верхней крышки нет
                int center = v.Count;
                v.Add(new Vector3(0f, y, 0f)); n.Add(normal); uv.Add(new Vector2(0.5f, 0.5f));
                for (int i = 0; i <= seg; i++)
                {
                    float a = i / (float)seg * Mathf.PI * 2f;
                    float cx = Mathf.Cos(a), cz = Mathf.Sin(a);
                    v.Add(new Vector3(cx * r * 0.5f, y, cz * r * 0.5f));
                    n.Add(normal);
                    uv.Add(new Vector2(cx * 0.5f + 0.5f, cz * 0.5f + 0.5f));
                }
                for (int i = 1; i <= seg; i++)
                {
                    if (up) { tri.Add(center); tri.Add(center + i); tri.Add(center + i + 1); }
                    else { tri.Add(center); tri.Add(center + i + 1); tri.Add(center + i); }
                }
            }
            Cap(1f, rTop, Vector3.up, true);
            Cap(0f, rBottom, Vector3.down, false);

            var mesh = new Mesh();
            mesh.SetVertices(v); mesh.SetNormals(n); mesh.SetUVs(0, uv);
            mesh.SetTriangles(tri, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
