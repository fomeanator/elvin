using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Lvn.Sandbox.Editor
{
    /// <summary>Небольшой детектор состава потокового набора: ловит случай,
    /// когда имя попало в манифест из prefab, но исчезло из самого bundle.</summary>
    public static class InspectFlowerFieldSet
    {
        public static void Run()
        {
            var path = Path.GetFullPath(Path.Combine(Application.dataPath,
                "..", "..", "server", "content", "sets", "flowerfield.macos.bundle"));
            var bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null) throw new System.Exception("Не открылся " + path);
            var prefab = bundle.LoadAsset<GameObject>("flowerfield");
            if (prefab == null) throw new System.Exception("В bundle нет flowerfield");
            var names = prefab.GetComponentsInChildren<Transform>(true)
                .Select(t => t.name).OrderBy(n => n).ToArray();
            Debug.Log("[flowerfield-inspect] " + string.Join(" | ", names));
            bundle.Unload(true);
        }
    }
}
