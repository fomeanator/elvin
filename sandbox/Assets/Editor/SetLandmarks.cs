using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Lvn.Sandbox.Editor
{
    /// <summary>
    /// Перечисляет ПРИМЕЧАТЕЛЬНЫЕ объекты набора с координатами — чтобы камеру
    /// вести по конкретным вещам (камень, мост, яблоня, ограда), а не по
    /// абстрактным точкам. Мелочь вроде травы отсеивается по размеру: интересно
    /// то, что заметно в кадре.
    ///
    /// SET_NAME=имя набора, MIN_SIZE=минимальный габарит объекта (по умолчанию 2 м).
    /// </summary>
    public static class SetLandmarks
    {
        public static void Run()
        {
            var setName = System.Environment.GetEnvironmentVariable("SET_NAME") ?? "meadow";
            float minSize = float.TryParse(System.Environment.GetEnvironmentVariable("MIN_SIZE"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : 2f;

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/ServerSets/" + setName + ".prefab");
            if (prefab == null) { Debug.LogError("MARK: нет набора " + setName); EditorApplication.Exit(1); return; }

            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var groups = new Dictionary<string, List<(Vector3 pos, float size)>>();

            foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
            {
                var b = r.bounds;
                float size = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
                if (size < minSize) continue;
                // Имя без хвостов вида "(1)" — объекты одного вида в одну кучу.
                var key = r.transform.root == r.transform ? r.name : TopName(r.transform, inst.transform);
                key = System.Text.RegularExpressions.Regex.Replace(key, @"\s*\(\d+\)$", "");
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<(Vector3, float)>();
                list.Add((b.center, size));
            }

            Debug.Log($"MARK: примечательных видов — {groups.Count} (порог {minSize} м)");
            foreach (var g in groups.OrderByDescending(g => g.Value.Max(v => v.size)).Take(20))
            {
                var biggest = g.Value.OrderByDescending(v => v.size).First();
                Debug.Log($"MARK| {g.Key} ×{g.Value.Count} крупнейший={biggest.size:F1}м " +
                          $"в ({biggest.pos.x:F1}, {biggest.pos.y:F1}, {biggest.pos.z:F1})");
            }

            Object.DestroyImmediate(inst);
            EditorApplication.Exit(0);
        }

        private static string TopName(Transform t, Transform root)
        {
            var cur = t;
            while (cur.parent != null && cur.parent != root) cur = cur.parent;
            return cur.name;
        }
    }
}
