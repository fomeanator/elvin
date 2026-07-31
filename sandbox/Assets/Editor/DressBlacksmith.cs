using System.Collections.Generic;
using System.Linq;
using Lvn.UI.World;
using UnityEditor;
using UnityEngine;

namespace Lvn.Sandbox.Editor
{
    /// <summary>
    /// Доводит набор «кузница» до вида, в котором его не стыдно показать:
    ///
    /// <list type="number">
    ///   <item><b>Гасит блеск.</b> Материалы кита — Standard PBR с включёнными
    ///   отражениями и бликами. Камень и мокрое дерево ловят небо и блестят как
    ///   пластик. Для стилизованной новеллы это чистый вред и лишний счёт на
    ///   каждый пиксель кадра.</item>
    ///   <item><b>Ставит настоящее небо.</b> Процедурный градиент читается как
    ///   «неба нет»: голая заливка без облаков и горизонт, обрывающийся в серое.
    ///   Берём небо соседнего кита — оно с облаками.</item>
    ///   <item><b>Заселяет пустырь.</b> В наборе всего пара построек, вокруг —
    ///   голая земля до горизонта. Обсаживаем деревню деревьями и кустами по
    ///   кольцу: кадр получает и глубину, и край, за который не видно пустоты.</item>
    /// </list>
    /// </summary>
    public static class DressBlacksmith
    {
        private const string Prefab = "Assets/ServerSets/blacksmith.prefab";
        private const string Kit = "Assets/Proxy Games/Stylized Nature Kit Lite/";

        public static void Run()
        {
            MatteMaterials();

            var root = AssetDatabase.LoadAssetAtPath<GameObject>(Prefab);
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(root);

            Sky(inst);
            Plant(inst);

            PrefabUtility.ApplyPrefabInstance(inst, InteractionMode.AutomatedAction);
            Object.DestroyImmediate(inst);
            AssetDatabase.SaveAssets();
            EditorApplication.Exit(0);
        }

        /// Матовость: блики и отражения прочь, шероховатость на максимум.
        private static void MatteMaterials()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets/3DForge" }))
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                if (m == null) continue;
                if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0f);
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
                m.SetFloat("_SpecularHighlights", 0f);
                m.SetFloat("_GlossyReflections", 0f);
                m.DisableKeyword("_SPECULARHIGHLIGHTS_OFF");
                m.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
                m.EnableKeyword("_GLOSSYREFLECTIONS_OFF");
                EditorUtility.SetDirty(m);
                Debug.Log("DRESS: матовый " + m.name);
            }
        }

        /// Небо с облаками вместо процедурного градиента.
        private static void Sky(GameObject inst)
        {
            var sky = AssetDatabase.LoadAssetAtPath<Material>(Kit + "Materials/Skybox.mat")
                   ?? AssetDatabase.LoadAssetAtPath<Material>(Kit + "Materials/Skybox 2.mat");
            var env = inst.GetComponentInChildren<Lvn3DSetEnv>(true);
            if (sky == null || env == null) { Debug.LogWarning("DRESS: небо не найдено"); return; }

            var so = new SerializedObject(env);
            so.FindProperty("skybox").objectReferenceValue = sky;
            // Дымку подводим ПОД цвет неба у горизонта, иначе земля обрывается
            // полосой — именно это читалось как «горизонта нет».
            so.FindProperty("fogColor").colorValue = new Color(0.70f, 0.78f, 0.85f);
            so.FindProperty("fogStart").floatValue = 45f;
            so.FindProperty("fogEnd").floatValue = 260f;
            so.ApplyModifiedPropertiesWithoutUndo();
            Debug.Log("DRESS: небо → " + sky.name);
        }

        /// Кольцо зелени вокруг деревни: пустырь до горизонта — главная причина
        /// ощущения «тут ничего нет».
        private static void Plant(GameObject inst)
        {
            var trees = Load("Prefabs/Foliage/Trees");
            var bushes = Load("Prefabs/Foliage/Bush", "Prefabs/Foliage/Grass", "Prefabs/Rocks");
            if (trees.Count == 0) { Debug.LogWarning("DRESS: деревьев в ките нет"); return; }

            var ground = inst.GetComponentsInChildren<MeshFilter>(true)
                             .FirstOrDefault(f => f.name == "ground");
            if (ground == null) { Debug.LogWarning("DRESS: нет земли — сажать не на что"); return; }
            var bounds = ground.GetComponent<MeshRenderer>().bounds;
            var centre = bounds.center;

            var holder = new GameObject("greens");
            holder.transform.SetParent(inst.transform, false);

            // Детерминированная раскладка: один и тот же набор при каждой сборке,
            // иначе бандл менял бы хеш на ровном месте.
            var rnd = new System.Random(20260729);
            int planted = 0;
            for (int i = 0; i < 120; i++)
            {
                double a = i * 2.399963;                    // золотой угол — без «полос»
                double r = 26 + (i % 7) * 5.5 + rnd.NextDouble() * 7;
                var pos = new Vector3(
                    centre.x + (float)(System.Math.Cos(a) * r),
                    0f,
                    centre.z + (float)(System.Math.Sin(a) * r));
                if (!Ray(pos, out float y)) continue;
                pos.y = y;

                bool tree = i % 3 != 0 || bushes.Count == 0;
                var src = tree ? trees[rnd.Next(trees.Count)] : bushes[rnd.Next(bushes.Count)];
                var go = (GameObject)PrefabUtility.InstantiatePrefab(src, holder.transform);
                go.transform.position = pos;
                go.transform.rotation = Quaternion.Euler(0f, (float)(rnd.NextDouble() * 360.0), 0f);
                float s = tree ? 1.5f + (float)rnd.NextDouble() * 1.1f : 1.1f + (float)rnd.NextDouble() * 0.6f;
                go.transform.localScale = Vector3.one * s;
                planted++;
            }
            Debug.Log($"DRESS: высажено {planted} (деревьев в ките {trees.Count}, кустов {bushes.Count})");

            bool Ray(Vector3 p, out float y)
            {
                y = 0f;
                var m = ground.sharedMesh;
                // Земля — сетка по XZ: берём ближайшую вершину, этого хватает.
                var local = ground.transform.InverseTransformPoint(p);
                float best = float.MaxValue; Vector3 hit = default;
                foreach (var v in m.vertices)
                {
                    float d = (v.x - local.x) * (v.x - local.x) + (v.z - local.z) * (v.z - local.z);
                    if (d < best) { best = d; hit = v; }
                }
                if (best > 64f) return false;               // мимо земли
                y = ground.transform.TransformPoint(hit).y;
                return true;
            }
        }

        private static List<GameObject> Load(params string[] folders)
        {
            var list = new List<GameObject>();
            foreach (var f in folders)
            {
                var dir = Kit + f;
                if (!AssetDatabase.IsValidFolder(dir)) continue;
                foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { dir }))
                {
                    var go = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                    if (go != null) list.Add(go);
                }
            }
            return list;
        }
    }
}
