using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lvn.UI.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Lvn.Sandbox.Editor
{
    /// <summary>
    /// Собирает набор <c>duelwood</c> — ночную лесную тропу, арену дуэли.
    ///
    /// <para>Почему кодом, а не руками в редакторе: набор должен собираться
    /// одинаково на любой машине и в batchmode, где сцену не открыть. Отсюда же
    /// фиксированное зерно — «случайность» расстановки воспроизводима, и правка
    /// одного дерева не перетасовывает весь лес.</para>
    ///
    /// <para>Композиция подчинена одной задаче: скелет — спрайт, стоящий у
    /// начала координат, и лес должен его ВЫДЕЛЯТЬ, а не соперничать с ним.
    /// Поэтому кулисы (ближние стволы) уходят за края кадра тёмными силуэтами,
    /// середина держится пустой, а стена дальнего леса светлеет туманом — на
    /// ней тёмная фигура читается силуэтом.</para>
    ///
    /// Запуск: <c>-executeMethod Lvn.Sandbox.Editor.BuildDuelWood.Run</c>
    /// </summary>
    public static class BuildDuelWood
    {
        private const string Dir = "Assets/ServerSets/";
        private const string SetName = "duelwood";

        // Сцена строится вокруг нуля: камера смотрит из −Z в +Z, скелет стоит
        // в начале координат. Все числа ниже — метры в этой системе.
        private const float PathHalfWidth = 1.9f;

        public static void Run()
        {
            var root = new GameObject(SetName);
            Random.InitState(20260731);

            var ground = BuildGround(root.transform);
            BuildPath(root.transform);
            PlantForest(root.transform);
            ScatterRocks(root.transform);
            ScatterUndergrowth(root.transform);
            BuildLights(root.transform);
            ApplyAtmosphere(root);

            // ── ПРИВЕДЕНИЕ К ПРАВИЛАМ (docs/3d-set-rules.md) ──────────────
            // Лес из отдельных деревьев — это сотни вызовов отрисовки, и на
            // слабом Android они дороже полигонов. Склеиваем всё, что не
            // двигается, по материалам: одна сосна и тысяча сосен должны
            // стоить одинаково, раз они всё равно неподвижны.
            StripColliders(root);
            // Склейку мешей пробовал первой — она убирает вызовы отрисовки, но
            // ДУБЛИРУЕТ вершины: бандл вырос с 2 до 17 МБ, то есть я разменял
            // мобильный стрим на кадры. Инстансинг делает ту же работу иначе:
            // девяносто одинаковых сосен рисуются одной пачкой, а в бандле
            // остаётся ОДИН меш и список матриц.
            EnableInstancing(root);

            Directory.CreateDirectory(Dir);
            var path = Dir + SetName + ".prefab";
            PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"WOOD: набор собран → {path} (земля {ground})");
            if (System.Environment.GetEnvironmentVariable("EXIT_AFTER") == "1")
                EditorApplication.Exit(0);
        }

        // ── земля ─────────────────────────────────────────────────────────
        // Плоскость 90×90 с лёгким рельефом: идеально ровный пол выдаёт
        // «сцену из примитивов» мгновенно, а неровность в пару сантиметров
        // ловится глазом на тенях и убирает это ощущение.
        private static string BuildGround(Transform parent)
        {
            var mat = MakeMaterial("duelwood-ground", new Color(0.10f, 0.125f, 0.09f), 0.92f);
            var mesh = BuildUndulatingPlane(160f, 56, 0.22f);
            AssetDatabase.CreateAsset(mesh, Dir + "duelwood-ground.asset");

            var go = new GameObject("ground");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return mesh.vertexCount + " верш.";
        }

        // Тропа — не текстура, а отдельная утоптанная полоса чуть выше земли:
        // так она читается и в тумане, и на скользящем лунном свете, а главное
        // ведёт взгляд к фигуре в центре кадра.
        private static void BuildPath(Transform parent)
        {
            var mat = MakeMaterial("duelwood-path", new Color(0.175f, 0.15f, 0.115f), 0.95f);
            var mesh = BuildUndulatingPlane(1f, 2, 0f);
            AssetDatabase.CreateAsset(mesh, Dir + "duelwood-path.asset");

            var go = new GameObject("path");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0.012f, 6f);
            go.transform.localScale = new Vector3(PathHalfWidth * 2f, 1f, 46f);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        }

        // ── лес ───────────────────────────────────────────────────────────
        private static readonly string[] Pines =
        {
            "Assets/Polytope Studio/Lowpoly_Environments/Prefabs/Trees/PT_Pine_Tree_03_green.prefab",
            "Assets/Polytope Studio/Lowpoly_Environments/Prefabs/Trees/PT_Pine_Tree_03_dead.prefab",
        };

        private static void PlantForest(Transform parent)
        {
            var holder = new GameObject("forest");
            holder.transform.SetParent(parent, false);
            var pines = Pines.Select(AssetDatabase.LoadAssetAtPath<GameObject>)
                             .Where(p => p != null).ToArray();
            if (pines.Length == 0) { Debug.LogWarning("WOOD: сосен не нашлось"); return; }

            // 1. КУЛИСЫ — два ближних ствола по краям кадра. Они и не должны
            //    помещаться целиком: обрезанный кадром ствол даёт глубину
            //    дешевле любого тумана и запирает взгляд в центре.
            Plant(pines[0], holder.transform, new Vector3(-4.6f, 0f, -3.4f), 2.3f, 12f);
            Plant(pines[0], holder.transform, new Vector3(5.1f, 0f, -2.6f), 2.5f, -140f);

            // 2. СРЕДНИЙ ПЛАН — редкие деревья по бокам тропы. Сухие ставим
            //    ближе к центру: голые ветви силуэтнее и не забивают фигуру.
            var mid = new[]
            {
                (x: -5.4f, z: 7.5f, s: 1.5f, dead: true),
                (x: 6.2f, z: 9.0f, s: 1.7f, dead: false),
                (x: -8.0f, z: 13.0f, s: 1.4f, dead: false),
                (x: 7.8f, z: 15.5f, s: 1.6f, dead: true),
                (x: -4.2f, z: 18.0f, s: 1.3f, dead: false),
            };
            foreach (var t in mid)
                Plant(pines[t.dead && pines.Length > 1 ? 1 : 0], holder.transform,
                      new Vector3(t.x, 0f, t.z), t.s, Random.Range(0f, 360f));

            // 3. ДАЛЬНЯЯ СТЕНА — плотный лес, который туман превратит в светлый
            //    задник. Тропа остаётся коридором: полоса вдоль неё пустая.
            // Коридор тропы держится пустым только до z≈15 — дальше лес
            // смыкается ПОПЕРЁК кадра. Это не декор: скелет светлый, и на
            // светящемся ночном небе он бы просто растворился. Стена стволов
            // даёт ему тёмный фон, а туман отодвигает её на задник.
            for (int i = 0; i < 90; i++)
            {
                float z = Random.Range(15f, 46f);
                float span = Mathf.Lerp(4f, 0f, Mathf.InverseLerp(15f, 24f, z));
                float x = Random.Range(span, 30f) * (Random.value < 0.5f ? -1f : 1f);
                Plant(pines[Random.Range(0, pines.Length)], holder.transform,
                      new Vector3(x, -0.15f, z), Random.Range(1.3f, 2.1f), Random.Range(0f, 360f));
            }

            // 4. ЗА СПИНОЙ у камеры — тоже лес: он не виден в кадре, но его
            //    тени ложатся на тропу, и «улица посреди леса» перестаёт
            //    выглядеть подсвеченной ниоткуда.
            for (int i = 0; i < 10; i++)
                Plant(pines[Random.Range(0, pines.Length)], holder.transform,
                      new Vector3(Random.Range(-16f, 16f), -0.15f, Random.Range(-16f, -7f)),
                      Random.Range(1.2f, 2f), Random.Range(0f, 360f));
        }

        private static void Plant(GameObject prefab, Transform parent, Vector3 pos, float scale, float yaw)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            go.transform.localScale = Vector3.one * scale;
        }

        // ── камни, пни, трава ─────────────────────────────────────────────
        private static void ScatterRocks(Transform parent)
        {
            var holder = new GameObject("rocks");
            holder.transform.SetParent(parent, false);
            var rocks = new[] { 1, 2, 3, 4, 5 }
                .Select(i => AssetDatabase.LoadAssetAtPath<GameObject>(
                    $"Assets/Proxy Games/Stylized Nature Kit Lite/Prefabs/Rocks/Standard Rocks/Standard Rock {i}.prefab"))
                .Where(p => p != null).ToArray();
            var tiny = new[] { 1, 2, 3, 4, 5 }
                .Select(i => AssetDatabase.LoadAssetAtPath<GameObject>(
                    $"Assets/Proxy Games/Stylized Nature Kit Lite/Prefabs/Rocks/Tiny Rocks/Tiny Rock {i}.prefab"))
                .Where(p => p != null).ToArray();
            if (rocks.Length == 0) { Debug.LogWarning("WOOD: камней не нашлось"); return; }

            // Валуны кладём ПО КРАЯМ тропы — они держат её границу и заодно
            // дают тени поперёк кадра, оживляя пустой пол среднего плана.
            for (int i = 0; i < 14; i++)
            {
                float side = Random.value < 0.5f ? -1f : 1f;
                var pos = new Vector3(side * Random.Range(PathHalfWidth + 0.6f, 9f),
                                      -0.1f, Random.Range(3f, 24f));
                var go = (GameObject)PrefabUtility.InstantiatePrefab(
                    rocks[Random.Range(0, rocks.Length)], holder.transform);
                go.transform.localPosition = pos;
                go.transform.localRotation = Quaternion.Euler(Random.Range(-8f, 8f), Random.Range(0f, 360f), Random.Range(-8f, 8f));
                go.transform.localScale = Vector3.one * Random.Range(0.35f, 0.85f);
            }

            foreach (var _ in Enumerable.Range(0, tiny.Length == 0 ? 0 : 22))
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(
                    tiny[Random.Range(0, tiny.Length)], holder.transform);
                go.transform.localPosition = new Vector3(Random.Range(-10f, 10f), 0f, Random.Range(2f, 22f));
                go.transform.localRotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
                go.transform.localScale = Vector3.one * Random.Range(0.4f, 0.9f);
            }
        }

        private static void ScatterUndergrowth(Transform parent)
        {
            var holder = new GameObject("undergrowth");
            holder.transform.SetParent(parent, false);
            var kinds = new[]
            {
                "Assets/Polytope Studio/Lowpoly_Environments/Prefabs/Plants/PT_Grass_02.prefab",
                "Assets/Polytope Studio/Lowpoly_Environments/Prefabs/Plants/PT_Generic_Shrub_01_dead.prefab",
                "Assets/Polytope Studio/Lowpoly_Environments/Prefabs/Plants/PT_Generic_Shrub_01_green.prefab",
                "Assets/Proxy Games/Stylized Nature Kit Lite/Prefabs/Foliage/Stump/Stump.prefab",
            }.Select(AssetDatabase.LoadAssetAtPath<GameObject>).Where(p => p != null).ToArray();
            if (kinds.Length == 0) { Debug.LogWarning("WOOD: подлеска не нашлось"); return; }

            for (int i = 0; i < 70; i++)
            {
                float x = Random.Range(-14f, 14f);
                float z = Random.Range(1f, 28f);
                // Тропу не зарастаем: по ней ходят, и пустая полоса — половина
                // того, что делает её тропой.
                if (Mathf.Abs(x) < PathHalfWidth && z > -2f) continue;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(
                    kinds[Random.Range(0, kinds.Length)], holder.transform);
                go.transform.localPosition = new Vector3(x, 0f, z);
                go.transform.localRotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
                go.transform.localScale = Vector3.one * Random.Range(0.6f, 1.2f);
            }
        }

        // ── свет ──────────────────────────────────────────────────────────
        // Луна стоит ЗА сценой и светит навстречу камере: контровой свет
        // обводит стволы холодной каймой и отделяет их от тумана. Прямого
        // света на фигуру почти нет — она и должна быть тёмной.
        private static void BuildLights(Transform parent)
        {
            var moon = new GameObject("moon");
            moon.transform.SetParent(parent, false);
            moon.transform.localRotation = Quaternion.Euler(38f, 204f, 0f);
            var l = moon.AddComponent<Light>();
            l.type = LightType.Directional;
            l.color = new Color(0.62f, 0.72f, 1f);
            l.intensity = 1.4f;
            l.shadows = LightShadows.Soft;
            l.shadowStrength = 0.6f;

            // Второй источник — не «реализм», а читаемость: без него ближние
            // стволы уходят в абсолютно чёрное пятно и кадр разваливается.
            var fill = new GameObject("fill");
            fill.transform.SetParent(parent, false);
            fill.transform.localRotation = Quaternion.Euler(28f, 20f, 0f);
            var f = fill.AddComponent<Light>();
            f.type = LightType.Directional;
            f.color = new Color(0.30f, 0.36f, 0.52f);
            f.intensity = 0.5f;
            f.shadows = LightShadows.None;
        }

        // ── воздух ────────────────────────────────────────────────────────
        private static void ApplyAtmosphere(GameObject root)
        {
            var sky = new Material(Shader.Find("Skybox/Procedural"));
            sky.SetFloat("_SunSize", 0.03f);
            sky.SetFloat("_AtmosphereThickness", 0.45f);
            sky.SetColor("_SkyTint", new Color(0.07f, 0.09f, 0.17f));
            sky.SetColor("_GroundColor", new Color(0.04f, 0.05f, 0.07f));
            sky.SetFloat("_Exposure", 0.42f);
            AssetDatabase.CreateAsset(sky, Dir + "duelwood-sky.mat");

            var env = root.AddComponent<Lvn3DSetEnv>();
            env.skybox = sky;
            env.fog = true;
            env.fogMode = FogMode.ExponentialSquared;
            // Туман здесь — главный инструмент композиции, а не погода: он
            // светлеет с расстоянием и превращает дальний лес в задник, на
            // котором тёмная фигура наконец читается.
            env.fogColor = new Color(0.075f, 0.10f, 0.155f);
            env.fogDensity = 0.045f;
            env.ambient = true;
            env.ambientSky = new Color(0.19f, 0.24f, 0.38f);
            env.ambientEquator = new Color(0.13f, 0.16f, 0.24f);
            env.ambientGround = new Color(0.04f, 0.045f, 0.055f);
            env.freezeShaderWind = true;
            env.overrideShadows = true;
            env.shadowQuality = ShadowQuality.All;
            env.shadowResolution = ShadowResolution.Medium;
            env.shadowCascades = 2;
            env.shadowDistance = 22f;
        }

        /// <summary>Убрать коллайдеры: фону не с чем сталкиваться, а классы
        /// физики вырезаны из сборки — каждый даёт «Could not produce class»
        /// в логе устройства.</summary>
        private static void StripColliders(GameObject root)
        {
            // Коллайдеров в наборе быть не должно ВООБЩЕ: фону не с чем
            // сталкиваться, а классы физики вырезаны из плеера стриппингом —
            // каждый оставшийся даёт «Could not produce class» в логе. Высоту
            // пола движок берёт из геометрии, а не лучом.
            foreach (var c in root.GetComponentsInChildren<Collider>(true))
                if (c != null) Object.DestroyImmediate(c);
        }

        /// <summary>Разрешить GPU-инстансинг на материалах набора: повторяющаяся
        /// геометрия рисуется пачками, не раздувая бандл.</summary>
        private static void EnableInstancing(GameObject root)
        {
            var seen = new HashSet<Material>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || !seen.Add(m)) continue;
                    if (!m.enableInstancing)
                    {
                        m.enableInstancing = true;
                        EditorUtility.SetDirty(m);
                    }
                }
            Debug.Log($"WOOD: инстансинг включён на {seen.Count} материалах");
        }

        /// <summary>Склеить неподвижную геометрию в один меш на материал.
        /// НЕ используется: дублирует вершины и раздувает бандл — оставлено
        /// как справка о том, что этот путь пробовали.</summary>
        private static void CombineByMaterial(GameObject root)
        {
            var groups = new Dictionary<Material, List<MeshFilter>>();
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                var mr = mf.GetComponent<MeshRenderer>();
                if (mf.sharedMesh == null || mr == null || mr.sharedMaterials.Length != 1) continue;
                var mat = mr.sharedMaterial;
                if (mat == null) continue;
                if (!groups.TryGetValue(mat, out var list)) groups[mat] = list = new List<MeshFilter>();
                list.Add(mf);
            }
            var merged = new GameObject("merged");
            merged.transform.SetParent(root.transform, false);
            int n = 0;
            foreach (var kv in groups)
            {
                if (kv.Value.Count < 2) continue; // одиночке склейка не нужна
                var combines = new List<CombineInstance>();
                foreach (var mf in kv.Value)
                    combines.Add(new CombineInstance
                    {
                        mesh = mf.sharedMesh,
                        transform = root.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix,
                    });
                var mesh = new Mesh { name = SetName + "-merged-" + n, indexFormat = IndexFormat.UInt32 };
                mesh.CombineMeshes(combines.ToArray(), true, true);
                mesh.RecalculateBounds();
                AssetDatabase.CreateAsset(mesh, Dir + SetName + "-merged-" + n + ".asset");

                var go = new GameObject("merged-" + n);
                go.transform.SetParent(merged.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = kv.Key;
                n++;
                foreach (var mf in kv.Value)
                    if (mf != null) Object.DestroyImmediate(mf.gameObject);
            }
        }

        // ── утилиты ───────────────────────────────────────────────────────
        private static Material MakeMaterial(string name, Color color, float smoothnessInverse)
        {
            var m = new Material(Shader.Find("Standard"));
            m.color = color;
            m.SetFloat("_Glossiness", 1f - smoothnessInverse);
            m.SetFloat("_Metallic", 0f);
            AssetDatabase.CreateAsset(m, Dir + name + ".mat");
            return m;
        }

        /// <summary>Плоскость size×size из res×res клеток с шумом по высоте.</summary>
        private static Mesh BuildUndulatingPlane(float size, int res, float amplitude)
        {
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            float step = size / res, half = size * 0.5f;
            for (int z = 0; z <= res; z++)
                for (int x = 0; x <= res; x++)
                {
                    float px = -half + x * step, pz = -half + z * step;
                    float h = amplitude == 0f ? 0f
                        : (Mathf.PerlinNoise(px * 0.12f + 3.7f, pz * 0.12f + 9.1f) - 0.5f) * 2f * amplitude;
                    // АРЕНА РОВНАЯ. Неровность оживляет дальний план, но там,
                    // где стоят бойцы и ходит камера, она вредна: фигура на
                    // отметке 0 висит над ямой или тонет в бугре, а объектив
                    // у земли ныряет под грунт. Гасим рельеф к центру.
                    float r = Mathf.Sqrt(px * px + pz * pz);
                    h *= Mathf.Clamp01((r - 4.5f) / 5.5f);
                    verts.Add(new Vector3(px, h, pz));
                    uvs.Add(new Vector2((float)x / res, (float)z / res));
                }
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                {
                    int i = z * (res + 1) + x;
                    tris.Add(i); tris.Add(i + res + 1); tris.Add(i + 1);
                    tris.Add(i + 1); tris.Add(i + res + 1); tris.Add(i + res + 2);
                }
            var mesh = new Mesh { name = "duelwood-plane" };
            mesh.indexFormat = verts.Count > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
