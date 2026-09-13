using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Lvn.Tests.Runtime
{
    public sealed class PackShopLayoutTests
    {
        GameObject _go;
        PanelSettings _panel;
        RenderTexture _target;
        LvnTheme _theme;
        sealed class NoAssets : ILvnAssets
        {
            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct) => Task.FromResult<Sprite>(null);
            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct) => Task.FromResult<AudioClip>(null);
            public void Unload(string url) { }
            public void UnloadAll() { }
        }
        [SetUp] public void SetUp() { _theme = LvnTheme.Current; LvnTheme.Use(LvnTheme.Chrono()); }
        [TearDown] public void TearDown() { Clear(); LvnTheme.Use(_theme); }
        void Clear()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            if (_panel != null) { _panel.targetTexture = null; Object.DestroyImmediate(_panel); }
            if (_target != null) { _target.Release(); Object.DestroyImmediate(_target); }
        }
        VisualElement Panel(int width, int height)
        {
            Clear();
            // Реальная UITK-панель; stencil нужен обрезке карточек и ScrollView.
            _target = new RenderTexture(width, height, 24); _target.Create();
            _panel = ScriptableObject.CreateInstance<PanelSettings>();
            _panel.targetTexture = _target;
            _panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            _panel.referenceResolution = new Vector2Int(1080, 1920);
            _panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            _panel.match = 0;
#if UNITY_EDITOR
            _panel.themeStyleSheet = UnityEditor.AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(
                "Assets/UI Toolkit/UnityThemes/UnityDefaultRuntimeTheme.tss");
#endif
            _go = new GameObject("shop-layout-test");
            var doc = _go.AddComponent<UIDocument>(); doc.panelSettings = _panel;
            var grid = new VisualElement(); grid.style.width = Length.Percent(88f);
            LvnFlow.Wrap(grid, Justify.SpaceBetween);
            doc.rootVisualElement.Add(grid);
            return grid;
        }
        [UnityTest]
        public IEnumerator PricesStayInsideCardsAndStageButtonsStayAtTheDrawnFooter()
        {
            foreach (var size in new[] { new Vector2Int(750, 1334), new Vector2Int(1170, 2532), new Vector2Int(1536, 2048) })
            foreach (bool stage in new[] { false, true })
            {
                var grid = Panel(size.x, size.y);
                var shop = new PackShopScreen(new NoAssets(), modal: true);
                if (stage) shop.SetContent(new LvnManifest { ui = new LvnUiConfig { browse = new BrowseConfig { skin = "/test/" } } });
                var method = typeof(PackShopScreen).GetMethod("Card", BindingFlags.Instance | BindingFlags.NonPublic);
                for (int i = 0; i < 3; i++)
                {
                    var pack = new PackShopScreen.Pack { Sku = "layout-" + i, Currency = "gold", Amount = 100,
                        Price = "$199.99", Best = i == 2, Bonus = i == 1 ? 50 : 0 };
                    if (i == 2)
                    {
                        pack.Headline = "Большой набор для захватывающего приключения";
                        pack.Grants = new Dictionary<string, long> { ["gold"] = 3500, ["energy"] = 20, ["crystals"] = 100 };
                    }
                    grid.Add((VisualElement)method.Invoke(shop, new object[] { pack }));
                }
                yield return new WaitForSecondsRealtime(.4f);
                Assert.Greater(grid.resolvedStyle.width, 0, "UITK did not produce geometry");
                foreach (var card in grid.Children())
                {
                    var price = card.Q<Button>("shop-price");
                    var measured = price.MeasureTextSize(price.text, price.contentRect.width,
                        VisualElement.MeasureMode.AtMost, 0, VisualElement.MeasureMode.Undefined);
                    Assert.Greater(measured.y, 0, "Font did not load");
                    Assert.LessOrEqual(measured.y, price.contentRect.height + 2f, size + " clipped price");
                    Assert.GreaterOrEqual(price.worldBound.xMin, card.worldBound.xMin - 1f);
                    Assert.LessOrEqual(price.worldBound.xMax, card.worldBound.xMax + 1f);
                    Assert.LessOrEqual(price.worldBound.yMax, card.worldBound.yMax + 1f);
                    if (stage)
                        Assert.LessOrEqual(card.worldBound.yMax - price.worldBound.yMax, LvnStageKit.D(4f),
                            "The price floated above the button painted at the card footer: " + card.userData);
                    var chips = card.Q("shop-grants");
                    if (chips != null)
                        foreach (var chip in chips.Children())
                        {
                            Assert.GreaterOrEqual(chip.worldBound.xMin, card.worldBound.xMin - 1f);
                            Assert.LessOrEqual(chip.worldBound.xMax, card.worldBound.xMax + 1f);
                        }
                }
            }
        }
    }
}
