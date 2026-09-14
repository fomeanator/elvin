using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lvn.Content;
using Lvn.UI;
using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Lvn.Tests.Runtime
{
    public sealed class ProfileLayoutTests
    {
        GameObject _go;
        PanelSettings _panel;
        RenderTexture _target;
        DirectoryAssets _art;
        LvnTheme _theme;
        Rect? _safe;
        string _name;
        // Размеры матрицы из onboarding-shell-ui; вырезы задаются отдельно.
        static readonly (int w, int h, int top, int bottom)[] Matrix = {
            (750, 1334, 0, 0), (1179, 2556, 177, 102), (1320, 2868, 186, 102),
            (1080, 2340, 80, 72), (1080, 2400, 80, 72), (1536, 2048, 0, 0)
        };

        [SetUp] public void SetUp()
        {
            _theme = LvnTheme.Current; _safe = LvnEdges.Simulated; _name = LvnPlayerName.Current;
            LvnTheme.Use(LvnTheme.Romance());
            LvnPlayerName.Set("Александра Виктория Долгорукова");
            // Партнёрский арт можно дать только локально; тесты без него
            // проверяют ту же раскладку с обычным запасным значком.
            _art = new DirectoryAssets(Environment.GetEnvironmentVariable("LVN_PROFILE_ART") ?? Path.GetTempPath());
        }
        [TearDown] public void TearDown()
        {
            Clear(); _art?.UnloadAll(); LvnEdges.Simulated = _safe;
            LvnTheme.Use(_theme); LvnPlayerName.Set(_name);
        }
        void Clear()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            if (_panel != null) { _panel.targetTexture = null; Object.DestroyImmediate(_panel); }
            if (_target != null) { _target.Release(); Object.DestroyImmediate(_target); }
        }
        ProfileScreen Mount((int w, int h, int top, int bottom) size, bool stage, int relations, bool minimal = false)
        {
            Clear();
            _target = new RenderTexture(size.w, size.h, 24); _target.Create();
            _panel = ScriptableObject.CreateInstance<PanelSettings>();
            _panel.targetTexture = _target; _panel.scaleMode = PanelScaleMode.ConstantPixelSize;
            _panel.scale = size.w / 1080f;
            _panel.clearColor = true; _panel.colorClearValue = LvnTokens.Bg;
#if UNITY_EDITOR
            _panel.themeStyleSheet = UnityEditor.AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(
                "Assets/UI Toolkit/UnityThemes/UnityDefaultRuntimeTheme.tss");
#endif
            LvnEdges.Simulated = new Rect(0, size.bottom, Screen.width, Screen.height - size.top - size.bottom);
            _go = new GameObject("profile-layout-test");
            var doc = _go.AddComponent<UIDocument>(); doc.panelSettings = _panel;
            var root = doc.rootVisualElement;
            root.style.width = 1080; root.style.height = size.h * 1080f / size.w;
            LvnFonts.ApplyDefault(root);
            var p = new ProfileScreen(_art) { Minimal = minimal, Uid = "qa-profile-1234567890",
                AvatarUrl = "/content/ui/stage/avatar.png" };
            if (stage) p.SetContent(new LvnManifest { ui = new LvnUiConfig
                { browse = new BrowseConfig { skin = "/content/ui/stage/" } } });
            if (relations > 0)
            {
                p.ChaptersDone = 24;
                p.Stats.AddRange(new[] { new ProfileScreen.Stat("12 345", "Принятые решения"),
                    new ProfileScreen.Stat("24", "Прочитанные главы"), new ProfileScreen.Stat("7", "Завершённые истории"),
                    new ProfileScreen.Stat("999", "Особенно длинная подпись характеристики"), new ProfileScreen.Stat("42", "Встречи") });
                p.Achievements.Add(new ProfileScreen.Achievement(LvnIcon.Book, "Первая завершённая история", true));
                p.Achievements.Add(new ProfileScreen.Achievement(LvnIcon.Heart, "Новая встреча", false));
                for (int i = 0; i < relations; i++)
                    p.Relations.Add(new ProfileScreen.Relation("Александр Константинович " + (i + 1), .5f, "hero-" + i));
            }
            p.OnOpenSettings = p.OnOpenCutscenes = () => { };
            p.OnSignOut = p.OnDeleteAccount = () => System.Threading.Tasks.Task.FromResult(false);
            root.Add(p); p.ShowAsTab();
            return p;
        }

        [UnityTest]
        public IEnumerator FullProfileFitsDeviceMatrixWithZeroOneAndTwelveRelations()
        {
            foreach (var size in Matrix)
            foreach (bool stage in new[] { false, true })
            foreach (int count in new[] { 0, 1, 12 })
            {
                var p = Mount(size, stage, count);
                yield return new WaitForSecondsRealtime(.35f);
                string scenario = $"{(stage ? "stage" : "engine")}-{size.w}x{size.h}-{count}";
                var body = p.Q<ScrollView>("profile-body");
                float pixel = 1f / _panel.scale + .01f;
                Shot(scenario + "-top");
                Assert.Greater(body.contentViewport.worldBound.height, 0, "No UITK layout: " + scenario);
                Assert.Greater(p.resolvedStyle.opacity, .9f, "Profile is still invisible");
                foreach (var label in body.Query<Label>().ToList()) AssertTextFits(label, body, scenario);
                foreach (string name in new[] { "profile-stats", "profile-achievements" })
                {
                    var grid = p.Q(name); if (grid == null) continue;
                    var cells = grid.Children().ToList();
                    foreach (var cell in cells)
                    {
                        Assert.That(cell.worldBound.width, Is.EqualTo(cells[0].worldBound.width).Within(pixel), scenario);
                        Assert.LessOrEqual(cell.worldBound.xMax, grid.worldBound.xMax + pixel, scenario);
                    }
                    for (int i = 1; i < cells.Count; i++)
                        if (Mathf.Abs(cells[i].worldBound.yMin - cells[i - 1].worldBound.yMin) < 1)
                            Assert.That(cells[i].worldBound.xMin - cells[i - 1].worldBound.xMax,
                                Is.EqualTo(LvnTokens.Space2).Within(pixel), "Uneven tile gap: " + scenario
                                + $" {name} {i}: {cells[i - 1].worldBound} -> {cells[i].worldBound}; margin={cells[i - 1].resolvedStyle.marginRight}");
                }
                Assert.That(p.Query<Label>("profile-relation-name").ToList().Count, Is.EqualTo(count));
                body.ScrollTo(p.Q("profile-footer"));
                yield return new WaitForSecondsRealtime(.15f);
                var footer = p.Q("profile-footer");
                Assert.LessOrEqual(footer.worldBound.yMax, body.contentViewport.worldBound.yMax + 2, "Footer cannot be reached: " + scenario);
                Shot(scenario + "-bottom");
            }
        }

        [UnityTest]
        public IEnumerator MinimalFitsEveryDeviceAndHasNoHiddenExtraSections()
        {
            foreach (var size in Matrix)
            foreach (bool stage in new[] { false, true })
            {
                var p = Mount(size, stage, 12, minimal: true);
                yield return new WaitForSecondsRealtime(.3f);
                var body = p.Q<ScrollView>("profile-body");
                Assert.That(body.Query<Label>().ToList().Count, Is.EqualTo(2));
                Assert.That(p.Q("profile-avatar"), Is.Null);
                foreach (var label in body.Query<Label>().ToList()) AssertTextFits(label, body, "Minimal");
                Assert.LessOrEqual(p.Q("profile-footer").worldBound.yMax, body.contentViewport.worldBound.yMax + 2);
                Shot($"{(stage ? "stage" : "engine")}-{size.w}x{size.h}-minimal");
            }
        }

        [UnityTest]
        public IEnumerator TileGridReflowsOnResizeWithoutStretchingTheLastTile()
        {
            var p = Mount(Matrix[0], false, 1);
            p.style.right = StyleKeyword.Auto;
            foreach (int width in new[] { 1080, 700, 540, 360, 1080 })
            {
                p.style.width = width;
                yield return new WaitForSecondsRealtime(.25f);
                var grid = p.Q("profile-stats");
                var cells = grid.Children().ToList();
                foreach (var cell in cells)
                {
                    Assert.That(cell.worldBound.width, Is.EqualTo(cells[0].worldBound.width).Within(1f / _panel.scale + .01f));
                    Assert.LessOrEqual(cell.worldBound.xMax, grid.worldBound.xMax + 1f / _panel.scale);
                }
                Assert.GreaterOrEqual(cells.Last().worldBound.yMin, cells.First().worldBound.yMin);
            }
        }

        static void AssertTextFits(Label label, ScrollView body, string scenario)
        {
            Assert.Greater(label.contentRect.width, 0, "No text layout: " + label.text);
            Assert.GreaterOrEqual(label.worldBound.xMin, body.contentViewport.worldBound.xMin - 2, scenario + ": " + label.text);
            Assert.LessOrEqual(label.worldBound.xMax, body.contentViewport.worldBound.xMax + 2, scenario + ": " + label.text);
            var measured = label.MeasureTextSize(label.text, label.contentRect.width, VisualElement.MeasureMode.AtMost,
                0, VisualElement.MeasureMode.Undefined);
            Assert.Greater(measured.y, 0, "Font did not load");
            Assert.LessOrEqual(measured.y, label.contentRect.height + 3, "Clipped text: " + scenario + ": " + label.text);
        }

        void Shot(string name)
        {
            string dir = Environment.GetEnvironmentVariable("LVN_TEST_SHOTS");
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);
            var image = TestPixels.Read(_target);
            try { File.WriteAllBytes(Path.Combine(dir, "profile-" + name + ".png"), image.EncodeToPNG()); }
            finally { Object.DestroyImmediate(image); }
        }
    }
}
