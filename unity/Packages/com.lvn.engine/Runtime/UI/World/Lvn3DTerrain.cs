using UnityEngine;

namespace Lvn.UI.World
{
    /// <summary>
    /// ЗЕМЛЯ С РЕЛЬЕФОМ — холмы, ложбины, неровность под ногами.
    ///
    /// <para>Плоскость выдаёт сцену с головой. Настоящее место никогда не бывает
    /// ровным: земля идёт волной, у могилы просела, к лесу поднимается. Пока пол
    /// плоский, любой кадр читается как декорация, сколько на неё ни ставь
    /// моделей.</para>
    ///
    /// <para>Почему не Unity Terrain, хотя он «для этого и сделан». Террейн — это
    /// отдельный компонент со своим модулем в сборке, своим форматом данных
    /// (TerrainData), своими слоями текстур и своим редактором. Он предполагает,
    /// что рельеф ЛЕПЯТ РУКОЙ и хранят файлом — а у нас сцену пишут ТЕКСТОМ, и
    /// автором нередко работает нейросеть, которая кисточку в руки взять не
    /// может. Хранить рядом со скриптом ещё и бинарную карту высот значит
    /// потерять главное свойство формата: сцену видно целиком в одной строке.</para>
    ///
    /// <para>Поэтому рельеф ЗАДАЁТСЯ ЧИСЛАМИ и считается формулой: высота холмов
    /// в метрах, размер холма в метрах, доля мелкой неровности, зерно случайности.
    /// Одна строка описывает и пологие дюны, и разбитую колею. А раз это формула,
    /// высоту в любой точке можно спросить точно — и туда сами встают надгробия,
    /// деревья и всё, что просило поставить себя на землю.</para>
    /// </summary>
    public static class Lvn3DTerrain
    {
        /// <summary>Описание рельефа — ровно то, что автор пишет в строке.</summary>
        public struct Spec
        {
            /// <summary>Размах высот в метрах: разница между гребнем и ложбиной.</summary>
            public float Hills;
            /// <summary>Длина волны в метрах: сколько шагов от холма до холма.</summary>
            public float HillSize;
            /// <summary>Доля мелкой неровности поверх крупной формы (0…1).</summary>
            public float Detail;
            /// <summary>Зерно: то же число — тот же рельеф, всегда.</summary>
            public int Seed;

            public bool Flat => Hills <= 0.0001f;
        }

        /// <summary>Высота земли в точке. Та же формула, по которой построен меш,
        /// поэтому предмет, поставленный по ней, ложится ровно на поверхность,
        /// а не парит и не тонет.</summary>
        public static float Height(float x, float z, Spec s)
        {
            if (s.Flat) return 0f;
            float L = Mathf.Max(1f, s.HillSize);
            float d = Mathf.Clamp01(s.Detail);

            // Три октавы: крупная форма несёт холмы, две мелкие ломают их
            // правильность. Смещения между октавами не круглые — иначе гребни
            // совпадают и по земле идёт различимая клетка.
            float n = Noise(x / L, z / L, s.Seed);
            n += Noise(x / L * 2.37f + 5.2f, z / L * 2.37f + 1.3f, s.Seed + 17) * 0.5f * d;
            n += Noise(x / L * 4.91f + 9.1f, z / L * 4.91f + 7.7f, s.Seed + 41) * 0.25f * d;
            n /= 1f + 0.5f * d + 0.25f * d;

            return (n - 0.5f) * 2f * s.Hills;
        }

        /// <summary>Собрать меш земли: сетка размером <paramref name="sizeX"/>×
        /// <paramref name="sizeZ"/> метров, поднятая по формуле.</summary>
        public static Mesh Build(float sizeX, float sizeZ, Spec s, int cells = 0)
        {
            // Плотность сетки: примерно клетка на полтора метра. Реже — холмы
            // становятся гранёными, чаще — платим вершинами за неразличимое.
            // Потолок держим низким намеренно: это ЗАДНИК, а не карта уровня.
            if (cells <= 0)
                cells = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(sizeX, sizeZ) / 1.5f), 8, 160);
            cells = Mathf.Clamp(cells, 2, 240);

            int n = cells + 1;
            var verts = new Vector3[n * n];
            var uv = new Vector2[n * n];
            var tris = new int[cells * cells * 6];

            float hx = sizeX * 0.5f, hz = sizeZ * 0.5f;
            for (int j = 0; j < n; j++)
            {
                float tz = (float)j / cells;
                float z = -hz + sizeZ * tz;
                for (int i = 0; i < n; i++)
                {
                    float tx = (float)i / cells;
                    float x = -hx + sizeX * tx;
                    int k = j * n + i;
                    verts[k] = new Vector3(x, Height(x, z, s), z);
                    uv[k] = new Vector2(tx, tz);
                }
            }

            int t = 0;
            for (int j = 0; j < cells; j++)
                for (int i = 0; i < cells; i++)
                {
                    int k = j * n + i;
                    // Обход по часовой при взгляде СВЕРХУ — иначе земля видна
                    // только снизу, а сцена оказывается под ней.
                    tris[t++] = k; tris[t++] = k + n; tris[t++] = k + 1;
                    tris[t++] = k + 1; tris[t++] = k + n; tris[t++] = k + n + 1;
                }

            var mesh = new Mesh { name = "lvn-ground" };
            // Сетка 160×160 — это 25 тысяч вершин, что уже за границей short.
            mesh.indexFormat = verts.Length > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.vertices = verts;
            mesh.uv = uv;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();   // без них рельеф из карты нормалей плоский
            mesh.RecalculateBounds();
            return mesh;
        }

        // Шум значений: решётка случайных чисел со сглаженной интерполяцией.
        // Берём его, а не Mathf.PerlinNoise, по одной причине — ЗЕРНО: у Perlin
        // в Unity его нет, и сдвигать координатами значит получать одинаковый
        // рельеф во всех сценах, где автор не догадался сместить землю.
        private static float Noise(float x, float z, int seed)
        {
            int xi = Mathf.FloorToInt(x), zi = Mathf.FloorToInt(z);
            float xf = x - xi, zf = z - zi;
            // Сглаживание Кена Перлина: у краёв клетки производная нулевая, и
            // стыки клеток не читаются гранями.
            float u = xf * xf * xf * (xf * (xf * 6f - 15f) + 10f);
            float v = zf * zf * zf * (zf * (zf * 6f - 15f) + 10f);

            float a = Hash(xi, zi, seed);
            float b = Hash(xi + 1, zi, seed);
            float c = Hash(xi, zi + 1, seed);
            float d = Hash(xi + 1, zi + 1, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, u), Mathf.Lerp(c, d, u), v);
        }

        private static float Hash(int x, int z, int seed)
        {
            unchecked
            {
                int h = x * 374761393 + z * 668265263 + seed * 1274126177;
                h = (h ^ (h >> 13)) * 1274126177;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0xFFFFFF;
            }
        }
    }
}
