using System;
using System.Collections;
using System.IO;
using System.Reflection;
using Lvn.UI;
using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Lvn.Tests
{
    public sealed class BootVeilPanelTests
    {
        [UnityTest] public IEnumerator PortraitTitleStaysPutWhenProgressAppears() => Check(1080, 1920, "Elemental Chronicles");
        [UnityTest] public IEnumerator LandscapeCyrillicTitleFits() => Check(1920, 1080, "Хроники стихий");
        [UnityTest] public IEnumerator NarrowPortraitLongTitleFits() => Check(390, 700, "Хроники стихий: путешествие в забытое королевство");

        private static IEnumerator Check(int width, int height, string title)
        {
            TestPixels.RequireGraphics();
            var veil = typeof(NovelApp).Assembly.GetType("Lvn.UI.Screens.BootVeil");
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            void Call(string method, params object[] args) => veil.GetMethod(method, flags).Invoke(null, args);
            void Set(string field, object value) => veil.GetField(field, flags).SetValue(null, value);
            var settings = ScriptableObject.CreateInstance<PanelSettings>();
            var texture = new RenderTexture(width, height, 24);
            var go = new GameObject("boot-veil-layout-test");
            var previousClock = LvnClock.Wall;
            bool previousMotion = LvnPrefs.ReduceMotion;
            float now = 0f;
            try
            {
                texture.Create();
                settings.targetTexture = texture;
                settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                settings.referenceResolution = new Vector2Int(1080, 1920);
                settings.match = width > height ? 1f : 0f;
                settings.clearColor = true;
                settings.colorClearValue = LvnDawn.Ground;
#if UNITY_EDITOR
                settings.themeStyleSheet = UnityEditor.AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(
                    "Assets/UI Toolkit/UnityThemes/UnityDefaultRuntimeTheme.tss");
#endif
                var document = go.AddComponent<UIDocument>();
                document.panelSettings = settings;
                var root = document.rootVisualElement;
                root.style.flexGrow = 1;
                root.style.backgroundColor = LvnDawn.Ground;
                Set("_root", root);
                Set("_target", 0f);
                LvnClock.Wall = () => now;
                LvnPrefs.ReduceMotion = true;
                Call("BuildLayout");
                Call("ResetPresentation");
                Call("Splash", title);
                for (int i = 0; i < 12; i++) yield return null;
                var heading = root.Q<Label>("boot-title");
                if (heading.worldBound.width <= 0 || float.IsNaN(heading.worldBound.width))
                    Assert.Ignore("панель UITK в этой среде не считает раскладку — размеры проверить нечем");
                var before = heading.worldBound;
                Assert.Greater(before.height, 0f);
                Assert.GreaterOrEqual(before.xMin, 0f);
                Assert.LessOrEqual(before.xMax, root.worldBound.xMax);
                Dump(texture, $"boot-title-{width}x{height}");

                Call("Progress", 67, "Загружаем истории…");
                now = 3.1f;
                Call("RevealIfWaiting");
                // Representative data for the visual capture. Smooth progress
                // is tested by LoadingProgressModelTests; here we test layout.
                root.Q<Label>("boot-percent").text = "67%";
                root.Q("boot-fill").style.width = Length.Percent(67);
                for (int i = 0; i < 6; i++) yield return null;
                Assert.AreEqual(before, heading.worldBound, "progress must not shift the product title");
                var progress = root.Q("boot-progress");
                Assert.AreEqual(Visibility.Visible, progress.resolvedStyle.visibility);
                Assert.Greater(progress.worldBound.yMin, heading.worldBound.yMax);
                Assert.Less(progress.worldBound.yMax, root.Q("boot-signature").worldBound.yMin);
                Assert.Less(root.Q("boot-status").worldBound.xMax, root.Q("boot-percent").worldBound.xMin);
                Dump(texture, $"boot-progress-{width}x{height}");
            }
            finally
            {
                LvnClock.Wall = previousClock;
                LvnPrefs.ReduceMotion = previousMotion;
                Call("ResetPresentation");
                foreach (string field in new[] { "_root", "_identity", "_progress", "_brandTitle", "_pct", "_status", "_fill" })
                    Set(field, null);
                Set("_target", 0f);
                Object.Destroy(go);
                Object.Destroy(settings);
                texture.Release();
                Object.Destroy(texture);
            }
        }

        private static void Dump(RenderTexture texture, string name)
        {
            string shots = Environment.GetEnvironmentVariable("LVN_TEST_SHOTS");
            if (string.IsNullOrEmpty(shots)) return;
            Directory.CreateDirectory(shots);
            var screenshot = TestPixels.Read(texture);
            try { File.WriteAllBytes(Path.Combine(shots, name + ".png"), screenshot.EncodeToPNG()); }
            finally { Object.Destroy(screenshot); }
        }
    }
}
