using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Lvn.Sandbox.Editor
{
    /// <summary>
    /// Кладёт шейдер биллбордов в «всегда включённые».
    ///
    /// <para>Шейдер, который нигде не назначен в ассетах и берётся только через
    /// <c>Shader.Find</c> в рантайме, в сборку НЕ ПОПАДАЕТ: сборщик его просто
    /// не видит. В редакторе всё работает, на устройстве фигура пропадает —
    /// самый неприятный сорт расхождения.</para>
    /// </summary>
    public static class EnsureBoardShader
    {
        public static void Run()
        {
            var wanted = new[] { "Unlit/Transparent", "Sprites/Default", "Unlit/Texture" };
            var gs = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            SerializedObject so = null;
            foreach (var o in gs) { if (o != null && o.GetType().Name == "GraphicsSettings") so = new SerializedObject(o); }
            if (so == null) { Debug.LogError("SHADER: не открыть GraphicsSettings"); EditorApplication.Exit(1); return; }

            var list = so.FindProperty("m_AlwaysIncludedShaders");
            var have = new System.Collections.Generic.HashSet<string>();
            for (int i = 0; i < list.arraySize; i++)
            {
                var sh = list.GetArrayElementAtIndex(i).objectReferenceValue as Shader;
                if (sh != null) have.Add(sh.name);
            }
            int added = 0;
            foreach (var name in wanted)
            {
                if (have.Contains(name)) { Debug.Log($"SHADER: {name} уже включён"); continue; }
                var sh = Shader.Find(name);
                if (sh == null) { Debug.LogWarning($"SHADER: {name} не найден в проекте"); continue; }
                list.InsertArrayElementAtIndex(list.arraySize);
                list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = sh;
                added++;
                Debug.Log($"SHADER: {name} добавлен в сборку");
            }
            if (added > 0) so.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
            Debug.Log($"SHADER: готово, добавлено {added}");
            if (System.Environment.GetEnvironmentVariable("EXIT_AFTER") == "1") EditorApplication.Exit(0);
        }
    }
}
