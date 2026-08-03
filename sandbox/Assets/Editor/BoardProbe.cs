using UnityEditor;
using UnityEngine;

namespace Lvn.Sandbox.Editor
{
    /// <summary>Проверка того, ЧТО реально происходит с фигурой в наборе:
    /// где пол, куда встанет спрайт, совпадают ли пространства. Считает то же
    /// самое, что движок, но печатает числа.</summary>
    public static class BoardProbe
    {
        public static void Run()
        {
            var set = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/ServerSets/duelwood.prefab");
            if (set == null) { Debug.LogError("PROBE: нет duelwood.prefab"); EditorApplication.Exit(1); return; }
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(set);
            inst.transform.position = Vector3.zero;

            // 1. Что движок сочтёт полом
            MeshFilter widest = null; float area = 0f;
            foreach (var mf in inst.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null) continue;
                var b = mf.sharedMesh.bounds.size;
                if (b.y > b.x || b.y > b.z) continue;
                float a = b.x * b.z;
                if (a > area) { area = a; widest = mf; }
            }
            Debug.Log(widest == null
                ? "PROBE: пол НЕ НАЙДЕН"
                : $"PROBE: пол = {widest.name}, площадь {area:0}, bounds {widest.sharedMesh.bounds}");

            // 2. Высота пола в точке (0,0) — там, где стоит боец
            if (widest != null)
            {
                var verts = widest.sharedMesh.vertices;
                var m = widest.transform.localToWorldMatrix;
                float best = float.MaxValue, y = 0f;
                Vector3 nearest = Vector3.zero;
                foreach (var v in verts)
                {
                    var w = m.MultiplyPoint3x4(v);
                    float d = w.x * w.x + w.z * w.z;
                    if (d < best) { best = d; y = w.y; nearest = w; }
                }
                Debug.Log($"PROBE: под точкой (0,0) пол на y={y:0.###} (ближайшая вершина {nearest}, " +
                          $"расстояние {Mathf.Sqrt(best):0.##} м)");
                Debug.Log($"PROBE: фигура ростом 2.2 встанет центром на y={y + 1.1f:0.###}, " +
                          $"ноги {y:0.###}, макушка {y + 2.2f:0.###}");
            }

            // 3. Габариты всей сцены — не улетел ли набор
            var rends = inst.GetComponentsInChildren<Renderer>(true);
            if (rends.Length > 0)
            {
                var bb = rends[0].bounds;
                foreach (var r in rends) bb.Encapsulate(r.bounds);
                Debug.Log($"PROBE: набор занимает {bb.center} ± {bb.extents}");
            }

            Object.DestroyImmediate(inst);
            if (System.Environment.GetEnvironmentVariable("EXIT_AFTER") == "1") EditorApplication.Exit(0);
        }
    }
}
