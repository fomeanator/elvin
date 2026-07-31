using System.IO;
using UnityEditor;
using UnityEngine;

namespace Lvn.Sandbox.Editor
{
    /// <summary>
    /// Красит землю набора «кузница». Vendor-сцена приехала с НЕПОКРАШЕННЫМ
    /// террейном — Unity рисует такой дефолтной бело-серой шахматкой, и деревня
    /// стоит посреди кафеля. Даём террейну слои из текстур самого кита (трава,
    /// земля, брусчатка) и раскладываем их по высоте: низины — трава, склоны и
    /// вытоптанное у построек — земля.
    /// </summary>
    public static class PaintBlacksmithGround
    {
        private const string Prefab = "Assets/ServerSets/blacksmith.prefab";
        private const string Tex = "Assets/3DForge/FantasyExteriors/Village & Towns/Textures/";
        private const string Out = "Assets/ServerSets/";

        public static void Run()
        {
            var grass = Layer("fe_vil_grass_03_DIF.png", "bs-grass", 4f);
            var earth = Layer("fe_vil_earth_04_DIF.png", "bs-earth", 3.5f);
            var road  = Layer("fe_vil_cobblestone_02_DIF.png", "bs-road", 4f);
            if (grass == null || earth == null) { Debug.LogError("PAINT: нет текстур земли"); EditorApplication.Exit(1); return; }

            var root = AssetDatabase.LoadAssetAtPath<GameObject>(Prefab);
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(root);
            var terrain = inst.GetComponentInChildren<Terrain>(true);
            if (terrain == null) { Debug.LogError("PAINT: в наборе нет террейна"); EditorApplication.Exit(1); return; }

            var data = terrain.terrainData;
            data.terrainLayers = new[] { grass, earth, road };

            int w = data.alphamapWidth, h = data.alphamapHeight;
            var map = new float[w, h, 3];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    // Нормаль и высота решают, что здесь лежит: пологое и низкое —
                    // трава, крутое — голая земля. Мелкий шум по клеткам, чтобы
                    // граница не читалась линейкой.
                    float nx = (float)x / w, ny = (float)y / h;
                    float steep = data.GetSteepness(ny, nx) / 90f;
                    float height = data.GetInterpolatedHeight(ny, nx) / Mathf.Max(1f, data.size.y);
                    float noise = Mathf.PerlinNoise(nx * 14f, ny * 14f) * 0.25f;

                    float e = Mathf.Clamp01(0.45f + steep * 1.8f + height * 0.6f + noise - 0.1f);
                    float g = 1f - e;
                    map[y, x, 0] = g;
                    map[y, x, 1] = e;
                    map[y, x, 2] = 0f;
                }
            }
            data.SetAlphamaps(0, 0, map);
            EditorUtility.SetDirty(data);

            // GPU-инстансинг террейна был выключен — на телефоне это лишние
            // вызовы отрисовки на ровном месте.
            terrain.drawInstanced = true;
            terrain.heightmapPixelError = 8f;      // дальше от камеры — грубее сетка
            terrain.detailObjectDistance = 60f;    // трава за 60 м не видна, но считалась
            terrain.treeDistance = 180f;

            PrefabUtility.ApplyPrefabInstance(inst, InteractionMode.AutomatedAction);
            Object.DestroyImmediate(inst);
            AssetDatabase.SaveAssets();
            Debug.Log($"PAINT: земля покрашена ({w}×{h} карта), инстансинг включён");
            EditorApplication.Exit(0);
        }

        private static TerrainLayer Layer(string texName, string assetName, float tile)
        {
            var path = Out + assetName + ".terrainlayer";
            var existing = AssetDatabase.LoadAssetAtPath<TerrainLayer>(path);
            if (existing != null) return existing;
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(Tex + texName);
            if (tex == null) { Debug.LogWarning("PAINT: нет текстуры " + texName); return null; }
            var layer = new TerrainLayer
            {
                diffuseTexture = tex,
                tileSize = new Vector2(tile, tile),
                specular = Color.black,
                metallic = 0f,
                smoothness = 0f,
            };
            AssetDatabase.CreateAsset(layer, path);
            Debug.Log("PAINT: слой " + path);
            return layer;
        }
    }
}
