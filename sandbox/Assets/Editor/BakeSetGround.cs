using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Lvn.Sandbox.Editor
{
    /// <summary>
    /// Перепекает ВСЕ террейны набора в обычные объекты.
    ///
    /// <para>Зачем: <c>TerrainData</c> — отдельный ассет, и в AssetBundle он не
    /// уезжает. На устройстве это выглядит как геометрия, висящая в пустоте
    /// (ловили дважды: деревня над ничем, потом забор в воздухе). Сценовый
    /// бандл эту проблему решает, но грузить сцену в рантайме опасно — она
    /// вешает главный поток; поэтому вся работа делается ЗДЕСЬ, в редакторе,
    /// а на устройство едет обычный префаб.</para>
    ///
    /// <para>Переносится всё, что несёт террейн: рельеф (мешем по высотам),
    /// деревья и трава-детали — настоящими объектами из тех же префабов, что
    /// террейн расставлял сам. Тогда набор самодостаточен.</para>
    ///
    /// SET_NAME=имя набора, GROUND_RES=плотность сетки на террейн (по умолчанию 96),
    /// GRASS_STEP=шаг прореживания травы (по умолчанию 6 — брать каждую шестую).
    /// </summary>
    public static class BakeSetGround
    {
        public static void Run()
        {
            var setName = System.Environment.GetEnvironmentVariable("SET_NAME") ?? "set";
            int res = Env("GROUND_RES", 96);
            int grassStep = Env("GRASS_STEP", 6);
            var dir = "Assets/ServerSets/";
            var path = dir + setName + ".prefab";

            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (root == null) { Debug.LogError("GROUND: нет набора " + path); EditorApplication.Exit(1); return; }

            var inst = (GameObject)PrefabUtility.InstantiatePrefab(root);
            var terrains = inst.GetComponentsInChildren<Terrain>(true);
            if (terrains.Length == 0)
            {
                Debug.Log("GROUND: террейнов нет — перепекать нечего");
                Object.DestroyImmediate(inst); EditorApplication.Exit(0); return;
            }
            Debug.Log($"GROUND: террейнов в наборе — {terrains.Length}");

            var holder = new GameObject("baked-ground");
            holder.transform.SetParent(inst.transform, false);
            var plants = new GameObject("baked-plants");
            plants.transform.SetParent(inst.transform, false);

            int n = 0, trees = 0, grass = 0;
            foreach (var terrain in terrains)
            {
                if (terrain == null) continue;
                var data = terrain.terrainData;
                if (data == null) continue;
                var origin = terrain.transform.position;

                BakeMesh(data, origin, holder.transform, dir, setName + "-ground-" + n, res);
                trees += BakeTrees(data, origin, plants.transform);
                grass += BakeDetails(data, origin, plants.transform, grassStep);
                n++;
            }

            foreach (var terrain in terrains)
                if (terrain != null) Object.DestroyImmediate(terrain.gameObject);

            PrefabUtility.ApplyPrefabInstance(inst, InteractionMode.AutomatedAction);
            Object.DestroyImmediate(inst);
            AssetDatabase.SaveAssets();
            Debug.Log($"GROUND: перепечено {n} террейн(ов), деревьев {trees}, травы {grass}");
            EditorApplication.Exit(0);
        }

        private static void BakeMesh(TerrainData data, Vector3 origin, Transform parent,
                                     string dir, string assetName, int res)
        {
            var size = data.size;
            var mesh = new Mesh
            {
                name = assetName,
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
            };
            var verts = new Vector3[res * res];
            var uvs = new Vector2[res * res];
            var norms = new Vector3[res * res];
            for (int z = 0; z < res; z++)
            {
                for (int x = 0; x < res; x++)
                {
                    float u = (float)x / (res - 1), v = (float)z / (res - 1);
                    int i = z * res + x;
                    verts[i] = new Vector3(u * size.x, data.GetInterpolatedHeight(u, v), v * size.z);
                    uvs[i] = new Vector2(u * size.x / 8f, v * size.z / 8f);
                    norms[i] = data.GetInterpolatedNormal(u, v);
                }
            }
            var tris = new int[(res - 1) * (res - 1) * 6];
            int t = 0;
            for (int z = 0; z < res - 1; z++)
                for (int x = 0; x < res - 1; x++)
                {
                    int i = z * res + x;
                    tris[t++] = i; tris[t++] = i + res; tris[t++] = i + 1;
                    tris[t++] = i + 1; tris[t++] = i + res; tris[t++] = i + res + 1;
                }
            mesh.vertices = verts; mesh.uv = uvs; mesh.normals = norms; mesh.triangles = tris;
            mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, dir + assetName + ".asset");

            var mat = new Material(Shader.Find("Legacy Shaders/Diffuse") ?? Shader.Find("Standard"))
            {
                name = assetName,
            };
            var layer = data.terrainLayers?.FirstOrDefault();
            if (layer?.diffuseTexture != null)
            {
                mat.mainTexture = layer.diffuseTexture;
                mat.mainTextureScale = new Vector2(8f / Mathf.Max(0.01f, layer.tileSize.x),
                                                   8f / Mathf.Max(0.01f, layer.tileSize.y));
            }
            AssetDatabase.CreateAsset(mat, dir + assetName + ".mat");

            var go = new GameObject("ground", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            go.transform.position = origin;
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        }

        /// Деревья террейна — обычными объектами из тех же префабов.
        private static int BakeTrees(TerrainData data, Vector3 origin, Transform parent)
        {
            var protos = data.treePrototypes;
            if (protos == null || protos.Length == 0) return 0;
            int made = 0;
            foreach (var t in data.treeInstances)
            {
                if (t.prototypeIndex < 0 || t.prototypeIndex >= protos.Length) continue;
                var src = protos[t.prototypeIndex].prefab;
                if (src == null) continue;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(src, parent);
                go.transform.position = origin + Vector3.Scale(t.position, data.size);
                go.transform.rotation = Quaternion.Euler(0f, t.rotation * Mathf.Rad2Deg, 0f);
                go.transform.localScale = new Vector3(t.widthScale, t.heightScale, t.widthScale);
                made++;
            }
            return made;
        }

        /// Трава-детали: прореженная, иначе объектов вышли бы десятки тысяч.
        private static int BakeDetails(TerrainData data, Vector3 origin, Transform parent, int step)
        {
            var protos = data.detailPrototypes;
            if (protos == null || protos.Length == 0 || step <= 0) return 0;
            int w = data.detailWidth, h = data.detailHeight;
            if (w == 0 || h == 0) return 0;
            int made = 0;
            for (int layer = 0; layer < protos.Length; layer++)
            {
                var src = protos[layer].prototype;
                if (src == null) continue;                    // текстурная трава — пропускаем
                var map = data.GetDetailLayer(0, 0, w, h, layer);
                for (int y = 0; y < h; y += step)
                    for (int x = 0; x < w; x += step)
                    {
                        if (map[x, y] <= 0) continue;
                        float u = (float)y / w, v = (float)x / h;
                        var pos = origin + new Vector3(u * data.size.x,
                                                       data.GetInterpolatedHeight(u, v),
                                                       v * data.size.z);
                        var go = (GameObject)PrefabUtility.InstantiatePrefab(src, parent);
                        go.transform.position = pos;
                        go.transform.rotation = Quaternion.Euler(0f, (x * 37 + y * 71) % 360, 0f);
                        made++;
                    }
            }
            return made;
        }

        private static int Env(string name, int fallback) =>
            int.TryParse(System.Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;
    }
}
