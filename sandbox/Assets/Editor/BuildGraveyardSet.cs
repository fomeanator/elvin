using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Lvn.UI.World;

namespace Lvn.Sandbox.Editor
{
    /// <summary>
    /// Набор «кладбище» из моделей Kenney Graveyard Kit (CC0).
    ///
    /// <para>Это первая сцена, собранная не из наших примитивов, а из ЧУЖИХ
    /// моделей — и потому главная проверка всеядности: движок обязан привести
    /// их к своему виду сам, ничего не прося у автора. Модели приходят со
    /// своими материалами (общий атлас Kenney); стилизатор перекладывает их на
    /// наш toon, сохраняя текстуру и цвет.</para>
    ///
    /// <para>Набор устроен как БИБЛИОТЕКА: земля и ограда стоят на месте, а
    /// надгробия, кресты, склепы и фонари лежат выключенными — скрипт достаёт
    /// их по имени (<c>o3d model=gravestone-cross</c>) и расставляет сам.
    /// Так одно и то же кладбище можно разложить по-разному в разных главах,
    /// не пересобирая бандл.</para>
    /// </summary>
    public static class BuildGraveyardSet
    {
        private const string Dir = "Assets/Graveyard/";
        // ServerSets — наборы, которые СТРИМЯТСЯ бандлом, а не едут в APK.
        // Кладбище на 19 моделей в плеере не нужно: его качают, когда
        // сцена до него дошла.
        private const string Out = "Assets/ServerSets/graveyard.prefab";

        [MenuItem("Elvin/3D Sets/Собрать кладбище")]
        public static void Build()
        {
            Directory.CreateDirectory("Assets/ServerSets");
            var root = new GameObject("graveyard");

            // ЗЕМЛИ В НАБОРЕ НЕТ намеренно. Материал, созданный в редакторе
            // через Shader.Find, в бандл не уезжает: шейдер не попадает в
            // сборку, и на устройстве плоскость приезжает magenta. Землю ставит
            // скрипт (`o3d shape=plane`) — она получает наш шейдер, который
            // едет в игре всегда.

            // Библиотека моделей: выключены и стоят в стороне. Скрипт клонирует
            // их по имени — сам набор при этом почти ничего не рисует.
            var shelf = new GameObject("shelf");
            shelf.transform.SetParent(root.transform, false);
            shelf.transform.localPosition = new Vector3(0f, -50f, 0f);

            // ОБЩИЙ АТЛАС. Kenney красит весь кит одной текстурой-палитрой, но
            // Unity при импорте FBX создаёт материалы БЕЗ неё: имя текстуры в
            // файле не совпадает с тем, что лежит рядом. Модели приезжают
            // белыми, и это видно только в кадре — в редакторе на них смотрят
            // с материалом по умолчанию. Привязываем атлас сами, один материал
            // на весь набор: это ещё и один вызов отрисовки вместо девятнадцати.
            var atlas = AssetDatabase.LoadAssetAtPath<Texture2D>(Dir + "Textures/colormap.png");
            var kitMat = new Material(Shader.Find("Standard")) { name = "graveyard-kit", mainTexture = atlas };
            kitMat.enableInstancing = true;
            AssetDatabase.CreateAsset(kitMat, "Assets/ServerSets/graveyard-kit.mat");

            int added = 0;
            foreach (var path in Directory.GetFiles(Dir + "Models", "*.fbx").OrderBy(p => p))
            {
                var src = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (src == null) continue;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(src, shelf.transform);
                go.name = Path.GetFileNameWithoutExtension(path);   // имя = то, что пишет автор
                foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true))
                    r.sharedMaterial = kitMat;
                go.SetActive(false);
                added++;
            }

            // ── РАСТИТЕЛЬНОСТЬ ИЗ ГОТОВЫХ ПАКЕТОВ ───────────────────────────
            //
            // Своё дерево делать незачем: в проекте уже лежат низкополигональные
            // наборы, и в них есть ровно то, чего просит кладбище, — СУХИЕ
            // деревья, пни и брёвна. Их материалы мы не трогаем (в отличие от
            // кита Kenney, у которого отваливался атлас): у этих пакетов
            // текстуры прописаны верно, а стилизатор всё равно переложит их на
            // наш свет при постановке набора.
            //
            // Имена на полке даём СВОИ и по-человечески: автор пишет
            // `model=дерево_сухое`, а не `PT_Pine_Tree_03_dead`.
            var vegetation = new (string path, string name)[]
            {
                ("Assets/Polytope Studio/Lowpoly_Environments/Prefabs/Trees/PT_Pine_Tree_03_dead.prefab", "дерево_сухое"),
                ("Assets/Polytope Studio/Lowpoly_Environments/Prefabs/Trees/PT_Fruit_Tree_01_dead.prefab", "дерево_голое"),
                ("Assets/Polytope Studio/Lowpoly_Environments/Prefabs/Trees/PT_Fruit_Tree_01_dead_cut.prefab", "дерево_сломанное"),
                ("Assets/Polytope Studio/Lowpoly_Environments/Prefabs/Trees/PT_Pine_Tree_03_stump.prefab", "пень"),
                ("Assets/Polytope Studio/Lowpoly_Environments/Prefabs/Trees/PT_Pine_Tree_03_logs.prefab", "брёвна"),
                ("Assets/Polytope Studio/Lowpoly_Environments/Prefabs/Shrubs/PT_Generic_Shrub_01_dead.prefab", "куст_сухой"),
                ("Assets/Polytope Studio/Lowpoly_Environments/Prefabs/Plants/PT_Grass_02.prefab", "трава"),
                ("Assets/Proxy Games/Stylized Nature Kit Lite/Prefabs/Rocks/Standard Rocks/Standard Rock 1.prefab", "валун"),
                ("Assets/Proxy Games/Stylized Nature Kit Lite/Prefabs/Rocks/Tiny Rocks/Tiny Rock 1.prefab", "камешек"),
            };
            int plants = 0;
            foreach (var (path, name) in vegetation)
            {
                var src = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (src == null) { Debug.LogWarning($"[graveyard] нет модели: {path}"); continue; }
                var go = (GameObject)PrefabUtility.InstantiatePrefab(src, shelf.transform);
                go.name = name;
                go.SetActive(false);
                plants++;
            }

            // Атмосфера набора: ночь, туман, длинные мягкие тени.
            var env = root.AddComponent<Lvn3DSetEnv>();
            env.fog = true;
            env.fogMode = FogMode.Linear;
            env.fogColor = new Color(0.07f, 0.09f, 0.13f);
            env.fogStart = 6f; env.fogEnd = 42f;
            env.ambient = true;
            env.ambientSky = new Color(0.10f, 0.14f, 0.22f);
            env.ambientEquator = new Color(0.09f, 0.11f, 0.16f);
            env.ambientGround = new Color(0.05f, 0.06f, 0.08f);

            PrefabUtility.SaveAsPrefabAsset(root, Out);
            Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();
            Debug.Log($"[graveyard] набор собран: {added} из кита + {plants} растений/камней → {Out}");
        }
    }
}
