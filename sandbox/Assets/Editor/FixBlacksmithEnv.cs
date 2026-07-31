using System.IO;
using Lvn.UI.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Lvn.Sandbox.Editor
{
    /// <summary>
    /// Чинит атмосферу набора «кузница»: у исходной vendor-сцены небо НЕ
    /// назначено (`m_SkyboxMaterial: 0`), а камера набора чистит кадр небом —
    /// без него игрок видит чёрный экран вместо деревни. Даём процедурное небо
    /// (не тянет текстуры, всегда с солнцем), мягкую воздушную дымку вдаль и
    /// приглушаем засветку: у сцены был почти белый ambient на 0.94.
    /// </summary>
    public static class FixBlacksmithEnv
    {
        private const string Prefab = "Assets/ServerSets/blacksmith.prefab";
        private const string SkyMat = "Assets/ServerSets/blacksmith-sky.mat";

        public static void Run()
        {
            // Небо живёт рядом с набором, чтобы уехать в тот же бандл.
            var sky = AssetDatabase.LoadAssetAtPath<Material>(SkyMat);
            if (sky == null)
            {
                var shader = Shader.Find("Skybox/Procedural");
                if (shader == null) { Debug.LogError("FIX: нет шейдера Skybox/Procedural"); EditorApplication.Exit(1); return; }
                sky = new Material(shader) { name = "blacksmith-sky" };
                sky.SetFloat("_SunSize", 0.04f);
                sky.SetFloat("_AtmosphereThickness", 0.85f);
                sky.SetColor("_SkyTint", new Color(0.42f, 0.56f, 0.80f));
                sky.SetColor("_GroundColor", new Color(0.32f, 0.30f, 0.27f));
                sky.SetFloat("_Exposure", 1.0f);
                AssetDatabase.CreateAsset(sky, SkyMat);
                Debug.Log("FIX: небо создано " + SkyMat);
            }

            var root = AssetDatabase.LoadAssetAtPath<GameObject>(Prefab);
            if (root == null) { Debug.LogError("FIX: нет префаба " + Prefab); EditorApplication.Exit(1); return; }

            var inst = (GameObject)PrefabUtility.InstantiatePrefab(root);
            var env = inst.GetComponentInChildren<Lvn3DSetEnv>(true);
            if (env == null) { Debug.LogError("FIX: в наборе нет Lvn3DSetEnv"); EditorApplication.Exit(1); return; }

            var so = new SerializedObject(env);
            so.FindProperty("skybox").objectReferenceValue = sky;
            // Дымка вдаль: деревня стоит на плоской земле, без неё дальний край
            // обрывается в пустоту. Linear, чтобы ближний план остался чистым.
            so.FindProperty("fog").boolValue = true;
            so.FindProperty("fogMode").enumValueIndex = (int)FogMode.Linear - 1;
            so.FindProperty("fogColor").colorValue = new Color(0.62f, 0.68f, 0.76f);
            so.FindProperty("fogStart").floatValue = 30f;
            so.FindProperty("fogEnd").floatValue = 190f;
            // Засветка: было 0.94 почти белым — дерево и камень уходили в молоко.
            // (`ambient` здесь — выключатель, режим внутри Apply() всегда трёхцветный.)
            so.FindProperty("ambient").boolValue = true;
            so.FindProperty("ambientSky").colorValue = new Color(0.46f, 0.53f, 0.64f);
            so.FindProperty("ambientEquator").colorValue = new Color(0.32f, 0.31f, 0.28f);
            so.FindProperty("ambientGround").colorValue = new Color(0.18f, 0.16f, 0.13f);
            so.ApplyModifiedPropertiesWithoutUndo();

            // Солнце: у сцены оно есть, но пересвечивает. Сажаем на разумную
            // силу и тёплый оттенок — камень и дранка должны читаться, а не гореть.
            foreach (var l in inst.GetComponentsInChildren<Light>(true))
            {
                if (l.type != LightType.Directional) continue;
                l.intensity = 1.05f;
                l.color = new Color(1f, 0.95f, 0.86f);
                l.shadows = LightShadows.Soft;
                l.shadowStrength = 0.7f;
                // Солнце лежало у горизонта: процедурное небо мазало горизонт
                // жёлтым, а тени тянулись через всю деревню. Поднимаем в утро.
                l.transform.rotation = Quaternion.Euler(42f, 143f, 0f);
                Debug.Log($"FIX: солнце '{l.name}' → {l.intensity}, угол {l.transform.eulerAngles}");
            }

            PrefabUtility.ApplyPrefabInstance(inst, InteractionMode.AutomatedAction);
            Object.DestroyImmediate(inst);
            AssetDatabase.SaveAssets();
            Debug.Log("FIX: атмосфера кузницы записана в префаб");
            EditorApplication.Exit(0);
        }
    }
}
