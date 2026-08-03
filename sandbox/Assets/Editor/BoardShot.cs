using System.IO;
using Lvn.UI.World;
using UnityEditor;
using UnityEngine;

namespace Lvn.Sandbox.Editor
{
    /// <summary>Снимок сцены РЕАЛЬНЫМ кодом движка: ставит набор через
    /// Lvn3DBackdrop, кладёт в него фигуру биллбордом и пишет кадр в PNG.
    /// Это единственный способ увидеть то же, что увидит игрок, не гоняя
    /// сборку на устройство.</summary>
    public static class BoardShot
    {
        public static void Run()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/ServerSets/duelwood.prefab");
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Screenshots/skeleton_idle.png");
            if (tex == null)
            {
                // арт лежит в пакете новеллы, а не в проекте — грузим файлом
                var path = Path.GetFullPath("../packages/lvn-duel/assets/enemies/skeleton_idle.png");
                if (File.Exists(path))
                {
                    tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    tex.LoadImage(File.ReadAllBytes(path));
                    tex.Apply();
                }
            }
            if (prefab == null || tex == null)
            {
                Debug.LogError($"SHOT: нет данных (набор={prefab != null}, текстура={tex != null})");
                EditorApplication.Exit(1); return;
            }

            var host = new GameObject("shot-host");
            var backdrop = Lvn3DBackdrop.Ensure(host.transform);
            backdrop.SetSet(prefab);
            backdrop.Frame(0f, 1.1f, -2.6f, 2f, 0f, 45f, 0f);
            bool ok = backdrop.SetBillboard("hero", tex, Vector3.zero, 2.2f, false);
            Debug.Log($"SHOT: биллборд встал = {ok}");

            // В редакторе цикла обновления нет — снимаем явно.
            backdrop.ShootNow();
            var rt = backdrop.Texture;
            if (rt == null) { Debug.LogError("SHOT: нет кадра"); EditorApplication.Exit(1); return; }

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var shot = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            shot.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            shot.Apply();
            RenderTexture.active = prev;

            var outDir = Path.GetFullPath("../qa/board");
            Directory.CreateDirectory(outDir);
            File.WriteAllBytes(Path.Combine(outDir, "shot.png"), shot.EncodeToPNG());
            Debug.Log($"SHOT: кадр {rt.width}×{rt.height} → qa/board/shot.png");

            // Сколько объектов реально в наборе и где фигура
            foreach (Transform ch in backdrop.transform)
                Debug.Log($"SHOT: под backdrop — {ch.name} @ {ch.localPosition}");

            Object.DestroyImmediate(host);
            if (System.Environment.GetEnvironmentVariable("EXIT_AFTER") == "1") EditorApplication.Exit(0);
        }
    }
}
