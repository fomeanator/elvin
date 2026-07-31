using System.IO;
using System.Linq;
using Lvn.UI.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Lvn.Sandbox.Editor
{
    /// <summary>
    /// Превращает ГОТОВУЮ сцену из магазина в набор для `bg3d`.
    ///
    /// <para>Урок кузницы: покупная сцена держит половину своего вида не в
    /// объектах, а в настройках сцены — небо, туман, засветка, солнце. Префаб
    /// это не увозит, и на устройстве остаётся геометрия без воздуха (а без
    /// неба — вообще чёрный экран). Поэтому здесь атмосфера СНИМАЕТСЯ со сцены
    /// и кладётся на набор карточкой <see cref="Lvn3DSetEnv"/>, которая едет
    /// вместе с ним.</para>
    ///
    /// <para>Сохраняется СЦЕНОЙ, а не префабом — и это главное. Префаб теряет
    /// всё, что живёт не в объектах: террейн (его данные — отдельный ассет),
    /// деревья и траву террейна, запечённый свет. На кузнице это дало деревню
    /// над пустотой, на луге — забор, висящий в воздухе.</para>
    ///
    /// PROBE_SCENE=путь/к.unity, SET_NAME=имя набора.
    /// </summary>
    public static class SceneToSet
    {
        public static void Run()
        {
            var scenePath = System.Environment.GetEnvironmentVariable("PROBE_SCENE");
            var setName = System.Environment.GetEnvironmentVariable("SET_NAME") ?? "set";
            var outDir = "Assets/ServerSets/";

            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            if (!scene.IsValid()) { Debug.LogError("SET: сцена не открылась: " + scenePath); EditorApplication.Exit(1); return; }

            var root = new GameObject(setName);
            // Всё содержимое сцены — под один корень, кроме камер: у набора своя.
            foreach (var go in scene.GetRootGameObjects())
            {
                if (go == root) continue;
                if (go.GetComponentInChildren<Camera>(true) != null && go.GetComponentsInChildren<Renderer>(true).Length == 0)
                {
                    Debug.Log("SET: пропускаю камеру " + go.name);
                    continue;
                }
                var terr = go.GetComponentsInChildren<Terrain>(true);
                if (terr.Length > 0) Debug.Log($"SET: террейнов в '{go.name}': {terr.Length} — едут в сценовом бандле");
                go.transform.SetParent(root.transform, true);
            }

            // Атмосфера сцены — на карточку набора.
            var env = root.AddComponent<Lvn3DSetEnv>();
            var so = new SerializedObject(env);
            so.FindProperty("skybox").objectReferenceValue = RenderSettings.skybox;
            so.FindProperty("fog").boolValue = RenderSettings.fog;
            so.FindProperty("fogMode").enumValueIndex = (int)RenderSettings.fogMode - 1;
            so.FindProperty("fogColor").colorValue = RenderSettings.fogColor;
            so.FindProperty("fogDensity").floatValue = RenderSettings.fogDensity;
            so.FindProperty("fogStart").floatValue = RenderSettings.fogStartDistance;
            so.FindProperty("fogEnd").floatValue = RenderSettings.fogEndDistance;
            so.FindProperty("ambient").boolValue = true;
            so.FindProperty("ambientSky").colorValue = RenderSettings.ambientSkyColor;
            so.FindProperty("ambientEquator").colorValue = RenderSettings.ambientEquatorColor;
            so.FindProperty("ambientGround").colorValue = RenderSettings.ambientGroundColor;
            so.ApplyModifiedPropertiesWithoutUndo();

            Debug.Log($"SET: атмосфера снята — небо={(RenderSettings.skybox ? RenderSettings.skybox.name : "НЕТ")} " +
                      $"туман={RenderSettings.fog} ({RenderSettings.fogMode}) " +
                      $"ambient=({RenderSettings.ambientSkyColor}|{RenderSettings.ambientEquatorColor}|{RenderSettings.ambientGroundColor})");

            int rend = root.GetComponentsInChildren<Renderer>(true).Length;
            int lights = root.GetComponentsInChildren<Light>(true).Length;
            Debug.Log($"SET: в наборе {rend} рендереров, {lights} света");

            Directory.CreateDirectory(outDir);
            // Префаб — то, что умеет грузить рантайм. Всё, что префаб теряет
            // (террейн, его деревья и трава), переносит следующим шагом
            // BakeSetGround: в редакторе это безопасно, в рантайме — нет.
            var path = outDir + setName + ".prefab";
            PrefabUtility.SaveAsPrefabAsset(root, path);
            AssetDatabase.SaveAssets();
            Debug.Log("SET: сохранён " + path + " — дальше BakeSetGround перепечёт террейны");
            EditorApplication.Exit(0);
        }
    }
}
