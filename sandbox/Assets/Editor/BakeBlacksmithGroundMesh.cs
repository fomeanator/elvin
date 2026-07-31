using System.IO;
using UnityEditor;
using UnityEngine;

namespace Lvn.Sandbox.Editor
{
    /// <summary>
    /// Переводит землю набора «кузница» с Unity Terrain на обычный меш.
    ///
    /// <para>Две причины. Первая — практическая: TerrainData это отдельный
    /// ассет, и в AssetBundle он не уезжает — на устройстве деревня висела над
    /// пустотой, хотя в редакторе земля была. Вторая — производительность: для
    /// стилизованных мобильных сцен меш-земля предсказуемее террейна (своя
    /// плотность вершин, обычный LOD, простой шейдер вместо сплат-блендинга),
    /// а 11 МБ террейновых данных просто уходят.</para>
    ///
    /// Высоты снимаются с террейна, поэтому рельеф сохраняется один в один.
    /// </summary>
    public static class BakeBlacksmithGroundMesh
    {
        private const string Prefab = "Assets/ServerSets/blacksmith.prefab";
        private const string Out = "Assets/ServerSets/";
        private const string Tex = "Assets/3DForge/FantasyExteriors/Village & Towns/Textures/";
        private const int Res = 96;   // вершин по стороне: рельеф здесь пологий

        public static void Run()
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(Prefab);
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(root);
            var terrain = inst.GetComponentInChildren<Terrain>(true);
            if (terrain == null) { Debug.LogError("BAKE: террейна уже нет"); EditorApplication.Exit(1); return; }

            var data = terrain.terrainData;
            var size = data.size;
            var mesh = new Mesh { name = "blacksmith-ground", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };

            var verts = new Vector3[Res * Res];
            var uvs = new Vector2[Res * Res];
            var cols = new Color[Res * Res];
            for (int z = 0; z < Res; z++)
            {
                for (int x = 0; x < Res; x++)
                {
                    float u = (float)x / (Res - 1), v = (float)z / (Res - 1);
                    float y = data.GetInterpolatedHeight(u, v);
                    int i = z * Res + x;
                    verts[i] = new Vector3(u * size.x, y, v * size.z);
                    uvs[i] = new Vector2(u * 26f, v * 26f);   // тайлинг текстуры земли
                    // Вершинный цвет вместо сплат-карты: склоны темнее и суше,
                    // низины уходят в травяной. Шейдеру хватает одного прохода.
                    float steep = data.GetSteepness(u, v) / 90f;
                    float k = Mathf.Clamp01(steep * 2f + Mathf.PerlinNoise(u * 12f, v * 12f) * 0.35f);
                    cols[i] = Color.Lerp(new Color(0.74f, 0.76f, 0.62f), new Color(0.52f, 0.46f, 0.36f), k);
                }
            }
            var tris = new int[(Res - 1) * (Res - 1) * 6];
            int t = 0;
            for (int z = 0; z < Res - 1; z++)
            {
                for (int x = 0; x < Res - 1; x++)
                {
                    int i = z * Res + x;
                    tris[t++] = i; tris[t++] = i + Res; tris[t++] = i + 1;
                    tris[t++] = i + 1; tris[t++] = i + Res; tris[t++] = i + Res + 1;
                }
            }
            mesh.vertices = verts; mesh.uv = uvs; mesh.colors = cols; mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, Out + "blacksmith-ground.asset");

            var mat = AssetDatabase.LoadAssetAtPath<Material>(Out + "blacksmith-ground.mat");
            if (mat == null)
            {
                // Diffuse, не Standard: земля стилизованная, PBR ей нечего дать,
                // а на телефоне это лишний расчёт на каждый пиксель кадра.
                var sh = Shader.Find("Legacy Shaders/Diffuse") ?? Shader.Find("Mobile/Diffuse") ?? Shader.Find("Standard");
                mat = new Material(sh) { name = "blacksmith-ground" };
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(Tex + "fe_vil_earth_04_DIF.png");
                if (tex != null) mat.mainTexture = tex;
                mat.color = new Color(0.86f, 0.85f, 0.78f);
                AssetDatabase.CreateAsset(mat, Out + "blacksmith-ground.mat");
            }

            var go = new GameObject("ground", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(terrain.transform.parent, false);
            go.transform.position = terrain.transform.position;
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;

            Object.DestroyImmediate(terrain.gameObject);
            PrefabUtility.ApplyPrefabInstance(inst, InteractionMode.AutomatedAction);
            Object.DestroyImmediate(inst);
            AssetDatabase.SaveAssets();
            Debug.Log($"BAKE: земля стала мешем {Res}×{Res} ({tris.Length / 3} тр.), террейн удалён");
            EditorApplication.Exit(0);
        }
    }
}
