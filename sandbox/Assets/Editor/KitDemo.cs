using System.IO;
using Lvn.UI.World;
using UnityEditor;
using UnityEngine;

namespace Lvn.Sandbox.Editor
{
    /// <summary>Сцена из одних кирпичей кита — проверка, что из них вообще
    /// складывается место. Зал с колоннадой, столом и факелами.</summary>
    public static class KitDemo
    {
        public static void Run()
        {
            var root = new GameObject("kithall");
            GameObject P(string name, Vector3 pos, float yaw = 0f, float scale = 1f)
            {
                var src = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/LvnKit/kit_" + name + ".prefab");
                if (src == null) { Debug.LogWarning("[kit] нет " + name); return null; }
                var go = (GameObject)PrefabUtility.InstantiatePrefab(src, root.transform);
                go.transform.localPosition = pos;
                go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
                go.transform.localScale = Vector3.one * scale;
                return go;
            }

            for (int x = -4; x <= 4; x++)
                for (int z = -1; z <= 8; z++)
                    P("floor", new Vector3(x * 2f, 0f, z * 2f));
            for (int z = 0; z <= 8; z += 2)
            {
                P("column", new Vector3(-4f, 0f, z * 2f));
                P("column", new Vector3(4f, 0f, z * 2f));
            }
            for (int x = -4; x <= 4; x += 2)
            {
                P("wall", new Vector3(x, 0f, 17f));
                P("wall_low", new Vector3(x, 0f, -3f));
            }
            P("arch", new Vector3(0f, 0f, 16.6f));
            P("table", new Vector3(0f, 0.2f, 6f));
            P("bench", new Vector3(-1.4f, 0.2f, 6f), 90f);
            P("bench", new Vector3(1.4f, 0.2f, 6f), 90f);
            P("chest", new Vector3(3f, 0.2f, 3f), 20f);
            P("barrel", new Vector3(-3f, 0.2f, 2f));
            P("crate", new Vector3(-3.2f, 0.2f, 3.2f), 15f);
            P("torch", new Vector3(-3.8f, 1.2f, 4f));
            P("torch", new Vector3(3.8f, 1.2f, 4f));
            P("rock", new Vector3(2.6f, 0.2f, 10f), 30f);
            P("stump", new Vector3(-2.6f, 0.2f, 11f));
            for (int i = 0; i < 4; i++) P("fence", new Vector3(-6f + i * 2f, 0f, 14f));

            var sun = new GameObject("sun");
            sun.transform.SetParent(root.transform, false);
            sun.transform.localRotation = Quaternion.Euler(34f, 200f, 0f);
            var l = sun.AddComponent<Light>();
            l.type = LightType.Directional; l.intensity = 1.15f;
            l.color = new Color(1f, 0.94f, 0.82f); l.shadows = LightShadows.Soft;

            var fill = new GameObject("fill");
            fill.transform.SetParent(root.transform, false);
            fill.transform.localRotation = Quaternion.Euler(24f, 30f, 0f);
            var f = fill.AddComponent<Light>();
            f.type = LightType.Directional; f.intensity = 0.32f;
            f.color = new Color(0.55f, 0.6f, 0.75f); f.shadows = LightShadows.None;

            var env = root.AddComponent<Lvn3DSetEnv>();
            var sky = new Material(Shader.Find("Skybox/Procedural"));
            sky.SetFloat("_SunSize", 0.04f);
            sky.SetFloat("_AtmosphereThickness", 0.6f);
            sky.SetColor("_SkyTint", new Color(0.16f, 0.18f, 0.26f));
            sky.SetColor("_GroundColor", new Color(0.07f, 0.07f, 0.09f));
            sky.SetFloat("_Exposure", 0.5f);
            AssetDatabase.CreateAsset(sky, "Assets/ServerSets/kithall-sky.mat");
            env.skybox = sky;
            env.fog = true; env.fogMode = FogMode.ExponentialSquared;
            env.fogColor = new Color(0.12f, 0.13f, 0.17f); env.fogDensity = 0.035f;
            env.ambient = true;
            env.ambientSky = new Color(0.24f, 0.26f, 0.34f);
            env.ambientEquator = new Color(0.16f, 0.17f, 0.22f);
            env.ambientGround = new Color(0.07f, 0.07f, 0.09f);
            env.overrideShadows = true;
            env.shadowProjection = ShadowProjection.StableFit;
            env.shadowResolution = ShadowResolution.High;
            env.shadowCascades = 1; env.shadowDistance = 26f;

            Directory.CreateDirectory("Assets/ServerSets");
            PrefabUtility.SaveAsPrefabAsset(root, "Assets/ServerSets/kithall.prefab");
            Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
            Debug.Log("[kit] демо-зал собран → Assets/ServerSets/kithall.prefab");
            if (System.Environment.GetEnvironmentVariable("EXIT_AFTER") == "1") EditorApplication.Exit(0);
        }
    }
}
