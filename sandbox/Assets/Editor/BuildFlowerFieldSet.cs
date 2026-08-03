using System.IO;
using System.Collections.Generic;
using Lvn.UI.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Lvn.Sandbox.Editor
{
    /// <summary>
    /// Потоковая библиотека растений Poly Haven для сцены «бесконечное поле».
    /// Модели на полке выключены: .lvns сам задаёт плотность трёх поясов поля.
    /// </summary>
    public static class BuildFlowerFieldSet
    {
        private const string Root = "Assets/PolyhavenFlowerField/";
        private const string Out = "Assets/ServerSets/flowerfield.prefab";

        private readonly struct Plant
        {
            public readonly string Source;
            public readonly string Texture;
            public readonly string Alias;

            public Plant(string source, string texture, string alias)
            {
                Source = source;
                Texture = texture;
                Alias = alias;
            }
        }

        private static readonly Plant[] Plants =
        {
            new Plant("flower_empodium_2k.fbx", "flower_empodium_rgba_1k.png", "цветок_светлый"),
            new Plant("flower_gazania_2k.fbx", "flower_gazania_rgba_1k.png", "цветок_газания"),
            new Plant("grass_medium_02_2k.fbx", "grass_medium_02_rgba_1k.png", "трава_луговая"),
        };

        [MenuItem("Elvin/3D Sets/Собрать цветочное поле")]
        public static void Build()
        {
            Directory.CreateDirectory("Assets/ServerSets");
            var root = new GameObject("flowerfield");
            BuildGround(root.transform);
            var shelf = new GameObject("shelf");
            shelf.transform.SetParent(root.transform, false);
            shelf.transform.localPosition = new Vector3(0f, -50f, 0f);

            int added = 0;
            foreach (var plant in Plants)
            {
                var modelPath = Root + "Models/" + plant.Source;
                var texturePath = Root + "Textures/" + plant.Texture;
                ConfigureTexture(texturePath);

                var src = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
                if (src == null || tex == null)
                {
                    Debug.LogWarning($"[flowerfield] нет пары модель/текстура: {modelPath}");
                    continue;
                }

                var material = MakeCutoutMaterial(plant.Alias, tex);
                var go = (GameObject)PrefabUtility.InstantiatePrefab(src, shelf.transform);
                go.name = plant.Alias;
                PruneLods(go);

                foreach (var collider in go.GetComponentsInChildren<Collider>(true))
                    Object.DestroyImmediate(collider);
                foreach (var renderer in go.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var mats = renderer.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++) mats[i] = material;
                    renderer.sharedMaterials = mats;
                    renderer.shadowCastingMode = ShadowCastingMode.On;
                    renderer.receiveShadows = true;
                    renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                }

                CollapseIntoPatch(go, plant.Alias, material);

                go.SetActive(false);
                added++;
            }

            // Паспорт нужен и до строк weather=: если набор откроют напрямую,
            // растения всё равно не окажутся под чёрным стандартным небом.
            var env = root.AddComponent<Lvn3DSetEnv>();
            env.fog = true;
            env.fogMode = FogMode.Linear;
            env.fogColor = new Color(0.74f, 0.67f, 0.58f);
            env.fogStart = 34f;
            env.fogEnd = 150f;
            env.ambient = true;
            env.ambientSky = new Color(0.55f, 0.66f, 0.82f);
            env.ambientEquator = new Color(0.62f, 0.59f, 0.55f);
            env.ambientGround = new Color(0.23f, 0.27f, 0.18f);
            env.shadowDistance = 58f;

            PrefabUtility.SaveAsPrefabAsset(root, Out);
            Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();
            Debug.Log($"[flowerfield] набор собран: {added} растения Poly Haven → {Out}");
        }

        private static void BuildGround(Transform parent)
        {
            const string meshPath = "Assets/ServerSets/flowerfield-ground.asset";
            const string matPath = "Assets/ServerSets/flowerfield-ground.mat";
            AssetDatabase.DeleteAsset(meshPath);
            AssetDatabase.DeleteAsset(matPath);

            const int cells = 72;
            const float side = 190f;
            var vertices = new Vector3[(cells + 1) * (cells + 1)];
            var uvs = new Vector2[vertices.Length];
            var triangles = new int[cells * cells * 6];
            int vi = 0;
            for (int z = 0; z <= cells; z++)
                for (int x = 0; x <= cells; x++)
                {
                    float px = ((float)x / cells - 0.5f) * side;
                    float pz = ((float)z / cells - 0.5f) * side + 55f;
                    float y = Mathf.Sin(px * 0.045f + 0.7f) * 0.12f
                              + Mathf.Sin(pz * 0.031f - 0.4f) * 0.09f;
                    vertices[vi] = new Vector3(px, y, pz);
                    uvs[vi] = new Vector2((float)x / cells, (float)z / cells);
                    vi++;
                }
            int ti = 0;
            for (int z = 0; z < cells; z++)
                for (int x = 0; x < cells; x++)
                {
                    int a = z * (cells + 1) + x;
                    int b = a + 1;
                    int c = a + cells + 1;
                    int d = c + 1;
                    triangles[ti++] = a; triangles[ti++] = c; triangles[ti++] = b;
                    triangles[ti++] = b; triangles[ti++] = c; triangles[ti++] = d;
                }

            var mesh = new Mesh { name = "flowerfield-ground" };
            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, meshPath);

            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(
                Root + "Textures/meadow_flower_carpet_1k.jpg");
            var shader = Shader.Find("Lvn/Triplanar") ?? Shader.Find("Standard");
            var material = new Material(shader)
            {
                name = "flowerfield-ground",
                mainTexture = texture,
                color = Color.white,
                enableInstancing = true,
            };
            if (material.HasProperty("_Tiling")) material.SetFloat("_Tiling", 4.2f);
            if (material.HasProperty("_Variety")) material.SetFloat("_Variety", 0.55f);
            if (material.HasProperty("_BumpScale")) material.SetFloat("_BumpScale", 0f);
            AssetDatabase.CreateAsset(material, matPath);

            var ground = new GameObject("ground", typeof(MeshFilter), typeof(MeshRenderer));
            ground.transform.SetParent(parent, false);
            ground.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = ground.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = true;
        }

        private static void PruneLods(GameObject root)
        {
            // Poly Haven FBX хранит все LOD как одновременно активные меши.
            // После удаления LODGroup движком они иначе рисуются ВСЕ разом.
            // Оставляем LOD0 каждого варианта (a/b/c...), остальные удаляем.
            foreach (var group in root.GetComponentsInChildren<LODGroup>(true))
                Object.DestroyImmediate(group);
            var filters = root.GetComponentsInChildren<MeshFilter>(true);
            foreach (var filter in filters)
            {
                var name = filter.gameObject.name;
                if (name.Contains("_LOD") && !name.EndsWith("_LOD0"))
                    Object.DestroyImmediate(filter.gameObject);
            }
        }

        /// <summary>
        /// FBX хранит 5–8 самостоятельных вариантов растения в одной модели.
        /// Если клонировать корень буквально, Unity создаёт столько же
        /// Renderer/GameObject на КАЖДУЮ куртину, а варианты лежат один в одном.
        /// Раскладываем их по маленькому пятну и запекаем в один меш: поле
        /// остаётся густым, но для CPU одна куртина снова является одним телом.
        /// </summary>
        private static void CollapseIntoPatch(GameObject root, string alias, Material material)
        {
            var offsets = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(-0.46f, 0f, 0.27f),
                new Vector3(0.52f, 0f, 0.2f),
                new Vector3(-0.28f, 0f, -0.48f),
                new Vector3(0.34f, 0f, -0.5f),
                new Vector3(-0.68f, 0f, -0.16f),
                new Vector3(0.7f, 0f, -0.12f),
                new Vector3(0.04f, 0f, 0.67f),
            };
            var combines = new List<CombineInstance>();
            var filters = root.GetComponentsInChildren<MeshFilter>(true);
            int variant = 0;
            foreach (var filter in filters)
            {
                var mesh = filter.sharedMesh;
                if (mesh == null) continue;
                var place = Matrix4x4.TRS(
                    offsets[variant % offsets.Length],
                    Quaternion.Euler(0f, variant * 137.5f, 0f),
                    Vector3.one * (0.9f + (variant % 4) * 0.06f));
                var local = root.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                    combines.Add(new CombineInstance
                    {
                        mesh = mesh,
                        subMeshIndex = sub,
                        transform = place * local,
                    });
                variant++;
            }
            if (combines.Count == 0) return;

            var path = $"Assets/ServerSets/flowerfield-{alias}-patch.asset";
            AssetDatabase.DeleteAsset(path);
            var patchMesh = new Mesh
            {
                name = "flowerfield-" + alias + "-patch",
                indexFormat = IndexFormat.UInt32,
            };
            patchMesh.CombineMeshes(combines.ToArray(), true, true, false);
            patchMesh.RecalculateBounds();
            AssetDatabase.CreateAsset(patchMesh, path);

            while (root.transform.childCount > 0)
                Object.DestroyImmediate(root.transform.GetChild(0).gameObject);
            var ownRenderer = root.GetComponent<MeshRenderer>();
            if (ownRenderer != null) Object.DestroyImmediate(ownRenderer);
            var ownFilter = root.GetComponent<MeshFilter>();
            if (ownFilter != null) Object.DestroyImmediate(ownFilter);

            var patch = new GameObject("patch", typeof(MeshFilter), typeof(MeshRenderer));
            patch.transform.SetParent(root.transform, false);
            patch.GetComponent<MeshFilter>().sharedMesh = patchMesh;
            var renderer = patch.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        }

        private static void ConfigureTexture(string path)
        {
            if (!(AssetImporter.GetAtPath(path) is TextureImporter importer)) return;
            bool changed = importer.alphaIsTransparency == false
                           || importer.mipmapEnabled == false
                           || importer.maxTextureSize != 1024;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = true;
            importer.maxTextureSize = 1024;
            importer.textureCompression = TextureImporterCompression.Compressed;
            if (changed) importer.SaveAndReimport();
        }

        private static Material MakeCutoutMaterial(string alias, Texture2D texture)
        {
            var path = $"Assets/ServerSets/flowerfield-{alias}.mat";
            AssetDatabase.DeleteAsset(path);
            var material = new Material(Shader.Find("Standard"))
            {
                name = "flowerfield-" + alias,
                mainTexture = texture,
                color = Color.white,
                enableInstancing = true,
            };
            material.SetFloat("_Mode", 1f);
            material.SetFloat("_Cutoff", 0.42f);
            material.SetOverrideTag("RenderType", "TransparentCutout");
            material.EnableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHABLEND_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            material.renderQueue = (int)RenderQueue.AlphaTest;
            AssetDatabase.CreateAsset(material, path);
            return material;
        }
    }
}
