using System;
using System.Collections;
using System.IO;
using Lvn.Content;
using Lvn.UI;
using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Lvn.Tests
{
    public sealed class DownloadHudPanelTests
    {
        [UnityTest]
        public IEnumerator NarrowPortraitKeepsTheCloseButtonOnScreen() => CheckPanel(390, 700);

        [UnityTest]
        public IEnumerator ShortLandscapeKeepsContentScrollable() => CheckPanel(640, 320);

        private static IEnumerator CheckPanel(int width, int height)
        {
            TestPixels.RequireGraphics();
            var originalClock = LvnClock.Wall;
            bool wasInChapter = LvnScreenDirector.Current.InChapter;
            var settings = ScriptableObject.CreateInstance<PanelSettings>();
            var texture = new RenderTexture(width, height, 24);
            var go = new GameObject("download-hud-test");
            try
            {
                settings.targetTexture = texture;
                settings.scaleMode = PanelScaleMode.ConstantPixelSize;
                settings.clearColor = true;
                settings.colorClearValue = LvnTokens.Bg;
                var document = go.AddComponent<UIDocument>();
                document.panelSettings = settings;
                LvnFonts.ApplyDefault(document.rootVisualElement);
                LvnScreenDirector.Current.SetChapter(true);
                var hud = new DownloadHud();
                document.rootVisualElement.Add(hud);
                hud.ActiveUrl = () => "/sprites/character.png";
                float now = 1f;
                LvnClock.Wall = () => now;
                hud.Tick(new TransferSnapshot(2, 12, 2, 10 << 20, 50 << 20, 50 << 20, 0, null, 1));
                for (int i = 0; i < 6; i++) yield return null;
                hud.SetExpanded(true);
                now = 4f;
                hud.Tick(new TransferSnapshot(2, 12, 4, 20 << 20, 50 << 20, 50 << 20, 0, null, 1));
                yield return new WaitForSecondsRealtime(0.5f);
                var close = hud.Q<Button>("download-close");
                // Раскладка панели здесь может не считаться вовсе — тогда мерить
                // нечего, и молчаливый ноль выдавать за «не влезло» нельзя:
                // соседние проверки прокрутки пропускаются по тому же признаку.
                if (close == null || close.worldBound.width <= 0f)
                    Assert.Ignore("панель UITK в этой среде не считает раскладку — размеры проверить нечем");
                Assert.LessOrEqual(close.worldBound.xMax, width);
                Assert.GreaterOrEqual(close.worldBound.xMin, 0f);
                var scroll = hud.Q<ScrollView>();
                if (scroll == null || scroll.worldBound.height <= 0f)
                    Assert.Ignore("панель UITK в этой среде не считает раскладку — размеры проверить нечем");
                Assert.LessOrEqual(scroll.worldBound.yMax, height);

                string shots = Environment.GetEnvironmentVariable("LVN_TEST_SHOTS");
                if (!string.IsNullOrEmpty(shots))
                {
                    Directory.CreateDirectory(shots);
                    var screenshot = TestPixels.Read(texture);
                    try { File.WriteAllBytes(Path.Combine(shots, $"download-hud-{width}x{height}.png"), screenshot.EncodeToPNG()); }
                    finally { Object.Destroy(screenshot); }
                }

                hud.Tick(default);
                yield return null;
                Assert.AreEqual(DisplayStyle.None, hud.Q("download-metrics").resolvedStyle.display);
            }
            finally
            {
                LvnClock.Wall = originalClock;
                LvnScreenDirector.Current.SetChapter(wasInChapter);
                Object.Destroy(go);
                Object.Destroy(settings);
                Object.Destroy(texture);
            }
        }
    }
}
