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
                texture.Create();
                settings.targetTexture = texture;
                settings.scaleMode = PanelScaleMode.ConstantPixelSize;
                // Sizes in the runtime are authored against 1080x1920. A
                // 390px panel at scale 1 made fixed header/chart rows consume
                // the entire sheet; its zero-height scroll was misreported as
                // a missing layout instead of a mismatched test environment.
                float scale = width < height ? width / (float)LvnPanel.ReferenceWidth
                    : height / (float)LvnPanel.ReferenceHeight;
                settings.scale = scale;
                float panelWidth = width / scale, panelHeight = height / scale;
#if UNITY_EDITOR
                settings.themeStyleSheet = UnityEditor.AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(
                    "Assets/UI Toolkit/UnityThemes/UnityDefaultRuntimeTheme.tss");
#endif
                settings.clearColor = true;
                settings.colorClearValue = LvnTokens.Bg;
                var document = go.AddComponent<UIDocument>();
                document.panelSettings = settings;
                document.rootVisualElement.style.width = panelWidth;
                document.rootVisualElement.style.height = panelHeight;
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
                Assert.IsNotNull(close);
                Assert.Greater(close.worldBound.width, 0f, "close button must have a layout");
                Assert.LessOrEqual(close.worldBound.xMax, panelWidth);
                Assert.GreaterOrEqual(close.worldBound.xMin, 0f);
                var scroll = hud.Q<ScrollView>();
                Assert.IsNotNull(scroll);
                Assert.Greater(scroll.worldBound.height, 0f, "the sheet must leave room for its scrollable sections");
                Assert.LessOrEqual(scroll.worldBound.yMax, panelHeight);

                // Кадр — и на диск для глаз, и к эталону для сборки: лист в
                // облике темы (без арта) — то, что ломают правки токенов и
                // раскладки, а числа выше не замечают.
                string shots = Environment.GetEnvironmentVariable("LVN_TEST_SHOTS");
                var screenshot = TestPixels.Read(texture);
                try
                {
                    if (!string.IsNullOrEmpty(shots))
                    {
                        Directory.CreateDirectory(shots);
                        File.WriteAllBytes(Path.Combine(shots, $"download-hud-{width}x{height}.png"), screenshot.EncodeToPNG());
                    }
                    TestPixels.AssertGolden(screenshot, $"download-hud-{width}x{height}");
                }
                finally { Object.Destroy(screenshot); }

                var metrics = hud.Q("download-metrics");
                float metricsHeight = metrics.worldBound.height;
                Assert.Greater(metricsHeight, 0f);
                // The panel intentionally keeps this row in idle so that
                // the sheet does not jump between download batches. Advance
                // past the work-hold interval before checking the idle state.
                now += 5f;
                hud.Tick(default);
                for (int i = 0; i < 3; i++) yield return null;
                Assert.AreEqual(DisplayStyle.Flex, metrics.resolvedStyle.display);
                Assert.That(metrics.worldBound.height, Is.EqualTo(metricsHeight).Within(0.5f));
                Assert.AreEqual(DisplayStyle.None, hud.Q("download-percent").resolvedStyle.display);
                Assert.AreEqual(LvnWords.Of("dl.idle", "No active downloads"), hud.Q<Label>("download-title").text);
                Assert.AreEqual("—", hud.Q<Label>("download-speed").text);
                Assert.AreEqual("—", hud.Q<Label>("download-left").text);
                Assert.AreEqual("—", hud.Q<Label>("download-queue").text);
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
