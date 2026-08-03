using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Lvn.Sandbox.Editor
{
    /// <summary>
    /// LVN Kit — примитивы для сцен новеллы, собираемые КОДОМ.
    ///
    /// <para>Замысел тот же, что у деталей Roblox или наборов Kenney: не
    /// библиотека уникальных моделей, а десяток параметрических кирпичей и ОДНА
    /// палитра на всех. Отсюда всё остальное: у кита один материал, значит сто
    /// объектов — одна пачка отрисовки; меши примитивны, значит бандл весит
    /// килобайты; цвет берётся из атласа координатами UV, значит перекрасить
    /// стену — это сдвинуть развёртку, а не завести вторую текстуру.</para>
    ///
    /// <para>Почему кодом, а не в редакторе: набор обязан пересобираться в
    /// batchmode на любой машине, и правки должны быть видимы в diff. Блендер
    /// понадобится там, где нужна органика (фигуры, статуи, деревья) — кирпичи
    /// он рисует медленнее, чем цикл.</para>
    ///
    /// Запуск: <c>-executeMethod Lvn.Sandbox.Editor.LvnKit.Build</c>
    /// </summary>
    public static class LvnKit
    {
        private const string Dir = "Assets/LvnKit/";
        private const int Cell = 64;   // клетка палитры, пикселей
        private const int Cols = 8;

        /// <summary>Палитра кита. Порядок — это КОНТРАКТ: примитивы ссылаются на
        /// цвет индексом, и вставка нового посередине перекрасит готовые сцены.
        /// Добавлять только в конец.</summary>
        private static readonly Color[] Palette =
        {
            new Color(0.72f, 0.68f, 0.60f), // 0 камень светлый
            new Color(0.58f, 0.56f, 0.52f), // 1 камень тёмный
            new Color(0.52f, 0.37f, 0.24f), // 2 дерево
            new Color(0.36f, 0.25f, 0.17f), // 3 дерево тёмное
            new Color(0.55f, 0.13f, 0.16f), // 4 ткань багровая
            new Color(0.16f, 0.22f, 0.34f), // 5 ткань синяя
            new Color(0.78f, 0.64f, 0.28f), // 6 золото
            new Color(0.62f, 0.65f, 0.70f), // 7 сталь
            new Color(0.20f, 0.34f, 0.20f), // 8 листва
            new Color(0.12f, 0.14f, 0.18f), // 9 сажа/тень
            new Color(0.86f, 0.82f, 0.70f), // 10 кость
            new Color(0.94f, 0.78f, 0.42f), // 11 огонь
        };

        [MenuItem("Elvin/3D Sets/Build LVN Kit")]
        public static void Build()
        {
            Directory.CreateDirectory(Dir);
            var mat = BuildPaletteMaterial();

            // Кирпичи: имя → построитель. Каждый строит меш вокруг СВОЕЙ
            // локальной точки опоры — низ по центру, чтобы объект ставился на
            // пол, а не проваливался в него наполовину.
            var kit = new Dictionary<string, Mesh>
            {
                ["wall"]     = Box(2f, 3f, 0.3f, 1),
                ["wall_low"] = Box(2f, 1.2f, 0.3f, 1),
                ["floor"]    = Box(2f, 0.2f, 2f, 0),
                ["column"]   = Cylinder(0.25f, 3f, 10, 0),
                ["arch"]     = Arch(2f, 3f, 0.3f, 1),
                ["step"]     = Box(2f, 0.25f, 0.6f, 0),
                ["table"]    = Table(1.6f, 0.8f, 0.9f, 2),
                ["bench"]    = Table(1.6f, 0.4f, 0.45f, 2),
                ["chest"]    = Box(0.9f, 0.6f, 0.6f, 3),
                ["barrel"]   = Cylinder(0.4f, 0.9f, 10, 2),
                ["crate"]    = Box(0.7f, 0.7f, 0.7f, 2),
                ["post"]     = Box(0.15f, 2.2f, 0.15f, 3),
                ["fence"]    = Fence(2f, 1.1f, 3),
                ["torch"]    = Torch(1.6f, 3, 11),
                ["rock"]     = Rock(0.8f, 0),
                ["stump"]    = Cylinder(0.45f, 0.5f, 8, 3),
            };

            foreach (var kv in kit)
            {
                var mesh = kv.Value;
                mesh.name = "kit_" + kv.Key;
                AssetDatabase.CreateAsset(mesh, Dir + "kit_" + kv.Key + ".asset");

                var go = new GameObject("kit_" + kv.Key);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                PrefabUtility.SaveAsPrefabAsset(go, Dir + "kit_" + kv.Key + ".prefab");
                Object.DestroyImmediate(go);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[kit] собрано {kit.Count} примитивов, один материал, палитра {Palette.Length} цветов → {Dir}");
            if (System.Environment.GetEnvironmentVariable("EXIT_AFTER") == "1")
                EditorApplication.Exit(0);
        }

        // ── палитра ───────────────────────────────────────────────────────
        // Одна текстура-полоска: каждый цвет занимает клетку, UV примитива
        // целятся в её ЦЕНТР. Поэтому нет ни швов, ни фильтрации между
        // соседями, а перекраска — это смена индекса, а не новой текстуры.
        private static Material BuildPaletteMaterial()
        {
            int rows = Mathf.CeilToInt(Palette.Length / (float)Cols);
            var tex = new Texture2D(Cols * Cell, rows * Cell, TextureFormat.RGBA32, false)
            {
                name = "kit_palette",
                filterMode = FilterMode.Point,   // цвет должен быть плоским
                wrapMode = TextureWrapMode.Clamp,
            };
            var px = new Color[tex.width * tex.height];
            for (int i = 0; i < px.Length; i++) px[i] = Color.magenta; // незанятое — заметно
            for (int i = 0; i < Palette.Length; i++)
            {
                int cx = (i % Cols) * Cell, cy = (i / Cols) * Cell;
                for (int y = 0; y < Cell; y++)
                    for (int x = 0; x < Cell; x++)
                        px[(cy + y) * tex.width + cx + x] = Palette[i];
            }
            tex.SetPixels(px);
            tex.Apply();
            AssetDatabase.CreateAsset(tex, Dir + "kit_palette.asset");

            var mat = new Material(Shader.Find("Standard"))
            {
                name = "kit_material",
                mainTexture = tex,
                enableInstancing = true,  // сто кирпичей = одна пачка
            };
            mat.SetFloat("_Glossiness", 0.08f);
            mat.SetFloat("_Metallic", 0f);
            AssetDatabase.CreateAsset(mat, Dir + "kit_material.mat");
            return mat;
        }

        private static Vector2 Uv(int colour)
        {
            int rows = Mathf.CeilToInt(Palette.Length / (float)Cols);
            colour = Mathf.Clamp(colour, 0, Palette.Length - 1);
            float u = ((colour % Cols) + 0.5f) / Cols;
            float v = ((colour / Cols) + 0.5f) / rows;
            return new Vector2(u, v);
        }

        // ── строители ─────────────────────────────────────────────────────
        private sealed class Builder
        {
            public readonly List<Vector3> V = new List<Vector3>();
            public readonly List<Vector2> T = new List<Vector2>();
            public readonly List<int> I = new List<int>();

            public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, int colour)
            {
                int s = V.Count;
                V.Add(a); V.Add(b); V.Add(c); V.Add(d);
                var uv = Uv(colour);
                for (int i = 0; i < 4; i++) T.Add(uv);
                I.Add(s); I.Add(s + 1); I.Add(s + 2);
                I.Add(s); I.Add(s + 2); I.Add(s + 3);
            }

            public void Box(Vector3 c, Vector3 half, int colour)
            {
                Vector3 p = c + half, m = c - half;
                Quad(new Vector3(m.x, m.y, m.z), new Vector3(m.x, p.y, m.z), new Vector3(p.x, p.y, m.z), new Vector3(p.x, m.y, m.z), colour);
                Quad(new Vector3(p.x, m.y, p.z), new Vector3(p.x, p.y, p.z), new Vector3(m.x, p.y, p.z), new Vector3(m.x, m.y, p.z), colour);
                Quad(new Vector3(m.x, m.y, p.z), new Vector3(m.x, p.y, p.z), new Vector3(m.x, p.y, m.z), new Vector3(m.x, m.y, m.z), colour);
                Quad(new Vector3(p.x, m.y, m.z), new Vector3(p.x, p.y, m.z), new Vector3(p.x, p.y, p.z), new Vector3(p.x, m.y, p.z), colour);
                Quad(new Vector3(m.x, p.y, m.z), new Vector3(m.x, p.y, p.z), new Vector3(p.x, p.y, p.z), new Vector3(p.x, p.y, m.z), colour);
                Quad(new Vector3(m.x, m.y, p.z), new Vector3(m.x, m.y, m.z), new Vector3(p.x, m.y, m.z), new Vector3(p.x, m.y, p.z), colour);
            }

            public Mesh Done()
            {
                var mesh = new Mesh();
                mesh.SetVertices(V);
                mesh.SetUVs(0, T);
                mesh.SetTriangles(I, 0);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                return mesh;
            }
        }

        private static Mesh Box(float w, float h, float d, int colour)
        {
            var b = new Builder();
            b.Box(new Vector3(0f, h * 0.5f, 0f), new Vector3(w * 0.5f, h * 0.5f, d * 0.5f), colour);
            return b.Done();
        }

        private static Mesh Cylinder(float r, float h, int sides, int colour)
        {
            var b = new Builder();
            for (int i = 0; i < sides; i++)
            {
                float a0 = i / (float)sides * Mathf.PI * 2f, a1 = (i + 1) / (float)sides * Mathf.PI * 2f;
                Vector3 p0 = new Vector3(Mathf.Cos(a0) * r, 0f, Mathf.Sin(a0) * r);
                Vector3 p1 = new Vector3(Mathf.Cos(a1) * r, 0f, Mathf.Sin(a1) * r);
                b.Quad(p0, p0 + Vector3.up * h, p1 + Vector3.up * h, p1, colour);
                b.Quad(new Vector3(0f, h, 0f), p0 + Vector3.up * h, p1 + Vector3.up * h,
                       new Vector3(0f, h, 0f), colour);
            }
            return b.Done();
        }

        // Арка: две опоры и перемычка. Проём — то, ради чего примитив нужен:
        // сквозь него ставится камера или виден следующий план.
        private static Mesh Arch(float w, float h, float d, int colour)
        {
            var b = new Builder();
            float leg = w * 0.22f;
            b.Box(new Vector3(-(w * 0.5f - leg * 0.5f), h * 0.45f, 0f), new Vector3(leg * 0.5f, h * 0.45f, d * 0.5f), colour);
            b.Box(new Vector3(w * 0.5f - leg * 0.5f, h * 0.45f, 0f), new Vector3(leg * 0.5f, h * 0.45f, d * 0.5f), colour);
            b.Box(new Vector3(0f, h * 0.95f, 0f), new Vector3(w * 0.5f, h * 0.05f, d * 0.5f), colour);
            return b.Done();
        }

        private static Mesh Table(float w, float d, float h, int colour)
        {
            var b = new Builder();
            float t = 0.08f, leg = 0.1f;
            b.Box(new Vector3(0f, h - t * 0.5f, 0f), new Vector3(w * 0.5f, t * 0.5f, d * 0.5f), colour);
            float lx = w * 0.5f - leg, lz = d * 0.5f - leg;
            foreach (var s in new[] { new Vector2(-lx, -lz), new Vector2(lx, -lz), new Vector2(-lx, lz), new Vector2(lx, lz) })
                b.Box(new Vector3(s.x, (h - t) * 0.5f, s.y), new Vector3(leg * 0.5f, (h - t) * 0.5f, leg * 0.5f), colour + 1);
            return b.Done();
        }

        private static Mesh Fence(float w, float h, int colour)
        {
            var b = new Builder();
            b.Box(new Vector3(-w * 0.5f, h * 0.5f, 0f), new Vector3(0.06f, h * 0.5f, 0.06f), colour);
            b.Box(new Vector3(w * 0.5f, h * 0.5f, 0f), new Vector3(0.06f, h * 0.5f, 0.06f), colour);
            b.Box(new Vector3(0f, h * 0.75f, 0f), new Vector3(w * 0.5f, 0.05f, 0.04f), colour);
            b.Box(new Vector3(0f, h * 0.35f, 0f), new Vector3(w * 0.5f, 0.05f, 0.04f), colour);
            return b.Done();
        }

        // Факел: единственный примитив со СВЕТЯЩЕЙСЯ частью. Огонь отдельным
        // цветом палитры, чтобы его было видно и без источника света — набор
        // обязан читаться при одном ключевом свете.
        private static Mesh Torch(float h, int wood, int fire)
        {
            var b = new Builder();
            b.Box(new Vector3(0f, h * 0.45f, 0f), new Vector3(0.05f, h * 0.45f, 0.05f), wood);
            b.Box(new Vector3(0f, h * 0.95f, 0f), new Vector3(0.12f, h * 0.08f, 0.12f), fire);
            return b.Done();
        }

        // Камень: куб со сбитыми углами через неравные полуоси — дешевле любой
        // сферы и в кадре читается лучше, чем правильная форма.
        private static Mesh Rock(float s, int colour)
        {
            var b = new Builder();
            b.Box(new Vector3(0f, s * 0.35f, 0f), new Vector3(s * 0.5f, s * 0.35f, s * 0.42f), colour);
            b.Box(new Vector3(s * 0.18f, s * 0.62f, -s * 0.1f), new Vector3(s * 0.28f, s * 0.22f, s * 0.24f), colour);
            return b.Done();
        }
    }
}
