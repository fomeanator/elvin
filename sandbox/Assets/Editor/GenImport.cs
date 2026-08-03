using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Lvn.Sandbox.Editor
{
    /// <summary>
    /// Приём СГЕНЕРИРОВАННЫХ моделей (.glb) и приведение их к нашим правилам.
    ///
    /// <para>Генераторы (Hunyuan3D, TRELLIS, Tripo, Meshy, Rodin) выдают
    /// добротную болванку и запечённую текстуру — и ровно то, чего наши правила
    /// не терпят: сотни тысяч треугольников на бочку, текстуру 2048 на камень,
    /// свой материал у каждой модели. Само по себе это не «плохая модель», это
    /// другой контекст: их считают для рендера картинки, а не для сцены,
    /// которую стримят на телефон.</para>
    ///
    /// <para>Поэтому импорт здесь — не «перетащить в проект», а конвейер:
    /// упростить сетку, ужать текстуру, снять коллайдеры, включить инстансинг,
    /// проверить бюджеты. Что не проходит — печатается числом, а не мнением.</para>
    ///
    /// GLB_IN=путь/к/model.glb (или папка), GLB_OUT=имя набора,
    /// GLB_TRIS=целевые треугольники (по умолчанию 3000),
    /// GLB_TEX=сторона текстуры (по умолчанию 512).
    /// </summary>
    public static class GenImport
    {
        private const string Dir = "Assets/GenAssets/";

        public static async void Run()
        {
            var input = System.Environment.GetEnvironmentVariable("GLB_IN");
            if (string.IsNullOrEmpty(input) || !File.Exists(input) && !Directory.Exists(input))
            {
                Debug.LogError("[gen] GLB_IN не задан или не найден: " + input);
                EditorApplication.Exit(1);
                return;
            }
            int maxTris = Env("GLB_TRIS", 3000);
            int texSide = Env("GLB_TEX", 512);
            Directory.CreateDirectory(Dir);

            var files = Directory.Exists(input)
                ? Directory.GetFiles(input, "*.glb", SearchOption.TopDirectoryOnly)
                : new[] { input };

            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var go = new GameObject("gen_" + name);
                var import = new GLTFast.GltfImport();
                bool ok = await import.Load("file://" + Path.GetFullPath(file));
                if (!ok) { Debug.LogError("[gen] не читается: " + file); Object.DestroyImmediate(go); continue; }
                await import.InstantiateMainSceneAsync(go.transform);

                var report = Normalize(go, name, maxTris, texSide);
                PrefabUtility.SaveAsPrefabAsset(go, Dir + "gen_" + name + ".prefab");
                Object.DestroyImmediate(go);
                Debug.Log($"[gen] {name}: {report}");
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            if (System.Environment.GetEnvironmentVariable("EXIT_AFTER") == "1")
                EditorApplication.Exit(0);
        }

        /// <summary>Привести модель к правилам набора. Возвращает отчёт строкой —
        /// его же читает человек, решая, годится вещь или переделывать промпт.</summary>
        private static string Normalize(GameObject root, string name, int maxTris, int texSide)
        {
            foreach (var c in root.GetComponentsInChildren<Collider>(true))
                if (c != null) Object.DestroyImmediate(c);

            int trisBefore = 0, trisAfter = 0;
            var texes = new HashSet<Texture>();
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                trisBefore += mf.sharedMesh.triangles.Length / 3;

                // Упрощение: генераторы выдают равномерно плотную сетку, где
                // половина треугольников приходится на плоскости. Честная
                // децимация — задача отдельного инструмента (Simplygon,
                // meshoptimizer); здесь мы её НЕ подделываем, а честно
                // сообщаем, если модель тяжелее бюджета.
                trisAfter += mf.sharedMesh.triangles.Length / 3;

                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    m.enableInstancing = true;
                    if (m.mainTexture != null) texes.Add(m.mainTexture);
                }
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }

            // Текстуры: ужимаем импортом, а не в рантайме — иначе большой
            // оригинал всё равно уедет в бандл.
            int shrunk = 0;
            foreach (var t in texes)
            {
                var path = AssetDatabase.GetAssetPath(t);
                if (string.IsNullOrEmpty(path)) continue;
                if (AssetImporter.GetAtPath(path) is TextureImporter ti && ti.maxTextureSize > texSide)
                {
                    ti.maxTextureSize = texSide;
                    ti.textureCompression = TextureImporterCompression.Compressed;
                    ti.SaveAndReimport();
                    shrunk++;
                }
            }

            // Масштаб: генератор не знает наших метров. Приводим к росту 1 м по
            // большей стороне — дальше сцена ставит нужный размер сама.
            var bounds = new Bounds(root.transform.position, Vector3.zero);
            bool any = false;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            if (any)
            {
                float big = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
                if (big > 0.0001f) root.transform.localScale = Vector3.one / big;
            }

            var verdict = trisAfter > maxTris
                ? $"ТЯЖЕЛО: {trisAfter} тр. при бюджете {maxTris} — упрощать перед импортом"
                : "в бюджете";
            return $"{trisAfter} тр., {texes.Count} текс. (ужато {shrunk} до {texSide}), {verdict}";
        }

        private static int Env(string key, int def)
            => int.TryParse(System.Environment.GetEnvironmentVariable(key), out var v) ? v : def;
    }
}
