using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.Services;
using Lvn.UI;
using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Lvn.Tests
{
    public class GachaPanelTests
    {
        [UnityTest] public IEnumerator NarrowPortraitRewardFits() => Check(320, 640, true);
        [UnityTest] public IEnumerator LandscapeRewardCanScroll() => Check(740, 360, true);
        [UnityTest] public IEnumerator PhonePortraitUsesRuntimeScale() => Check(390, 844, true);
        [UnityTest] public IEnumerator PhoneLandscapeUsesRuntimeScale() => Check(844, 390, true);

        private static IEnumerator Check(int width, int height, bool runtimeScale = false)
        {
            TestPixels.RequireGraphics();
            var settings = ScriptableObject.CreateInstance<PanelSettings>();
            var texture = new RenderTexture(width, height, 24);
            var go = new GameObject("gacha-panel-test");
            using var assets = new PrizeAssets();
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            var savedBase = (Dictionary<string, string>)typeof(LvnWords).GetField("_base", flags).GetValue(null);
            var savedTranslation = (Dictionary<string, string>)typeof(LvnWords).GetField("_translated", flags).GetValue(null);
            try
            {
                LvnWallet.Apply("{\"balances\":{\"crystals\":1000},\"inventory\":{}}");
                LvnWords.Learn(new Dictionary<string, string>
                {
                    ["gacha.title"] = "Крутка", ["gacha.spin"] = "Крутить", ["gacha.super"] = "Редкое",
                    ["gacha.price"] = "Следующая крутка: {0}", ["gacha.won_prize"] = "Вам выпало: {0}",
                    ["gacha.take"] = "Забрать приз",
                });
                LvnWords.Translate(null);
                texture.Create();
                settings.targetTexture = texture;
                settings.scaleMode = PanelScaleMode.ConstantPixelSize;
                float scale = runtimeScale ? (width < height ? width / (float)LvnPanel.ReferenceWidth
                    : height / (float)LvnPanel.ReferenceHeight) : 1f;
                settings.scale = scale;
                float panelWidth = width / scale, panelHeight = height / scale;
                settings.clearColor = true;
                settings.colorClearValue = LvnTokens.Bg;
#if UNITY_EDITOR
                settings.themeStyleSheet = UnityEditor.AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(
                    "Assets/UI Toolkit/UnityThemes/UnityDefaultRuntimeTheme.tss");
#endif
                var doc = go.AddComponent<UIDocument>();
                doc.panelSettings = settings;
                var root = doc.rootVisualElement;
                root.style.width = panelWidth;
                root.style.height = panelHeight;
                root.style.flexGrow = 1;
                LvnFonts.ApplyDefault(root);
                var screen = new GachaScreen(assets);
                screen.SetContent(Manifest());
                root.Add(screen);
                screen.ShowAsTab();
                screen.Present(new LvnGacha.Status
                {
                    SpinCurrency = "crystals", SpinPrice = 50,
                    Sectors = new List<LvnGacha.Sector>
                    {
                        new LvnGacha.Sector { Id = "rare", Kind = "super" },
                        new LvnGacha.Sector { Id = "coins", Kind = "currency", Currency = "crystals", Amount = 100 },
                    },
                });
                yield return new WaitForSecondsRealtime(0.3f);
                var spin = screen.Q<Button>("gacha-spin");
                Assert.IsNotNull(spin, "funded wallet exposes the paid spin action");
                Assert.Greater(spin.worldBound.height, 40f);
                Assert.LessOrEqual(spin.worldBound.xMax, panelWidth);
                Assert.LessOrEqual(spin.worldBound.yMax, panelHeight + 0.01f, "allow float rounding at the panel edge");
                Assert.IsFalse(float.IsNaN(screen.Q("gacha-strip").resolvedStyle.left));
                Save(texture, $"gacha-idle-{width}x{height}");
                var prize = new LvnGacha.Prize { Sku = "wardrobe:Hero:outfit:53", Label = "Редкий наряд" };
                var described = screen.DescribePrize(prize);
                Assert.AreEqual("Огненный тигр", described.Label);
                Assert.AreEqual("/outfit.png", described.Art, "art resolves from the wardrobe catalog when spin response omits it");
                // SpinAsync removes the payment button before awaiting the server.
                screen.Q("gacha-actions").Clear();
                var reveal = screen.RevealPrizeAsync(new LvnGacha.Spin { Super = true, Prize = prize, WalletSynced = true });
                yield return null;
                Assert.Less(screen.Q("gacha-reward-art").resolvedStyle.opacity, 1f, "reward actually animates");
                while (!reveal.IsCompleted) yield return null;
                Assert.IsFalse(reveal.IsFaulted, reveal.Exception?.ToString());
                yield return null;
                var take = screen.Q<Button>("gacha-take");
                Save(texture, $"gacha-prize-{width}x{height}");
                // Exercise a long localized caption on the real layout.
                take.text = "Забрать полученный наряд";
                yield return null;
                Assert.GreaterOrEqual(take.worldBound.xMin, 0f);
                Assert.LessOrEqual(take.worldBound.xMax, panelWidth);
                Assert.LessOrEqual(take.worldBound.yMax, panelHeight);
                var measured = take.MeasureTextSize(take.text, take.contentRect.width, VisualElement.MeasureMode.Exactly,
                    0, VisualElement.MeasureMode.Undefined);
                Assert.LessOrEqual(measured.y, take.contentRect.height + 1f, "localized action text is not clipped vertically");
                Assert.AreEqual("Огненный тигр", screen.Q<Label>("gacha-reward-name").text);
                Assert.IsNotNull(screen.Q("gacha-picture"), "ceremony displays the shared prize card");
                Assert.IsNotNull(screen.Q("gacha-picture").Q("card-art").resolvedStyle.backgroundImage.sprite,
                    "the shared card has loaded the actual prize artwork");
                Save(texture, $"gacha-long-caption-{width}x{height}");
                var point = take.worldBound.center;
                var picked = root.panel.Pick(point);
                Assert.IsTrue(picked == take || take.Contains(picked), "the reward button must receive the tap, not an overlay");
                using (var down = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, button = 0, mousePosition = point }))
                { down.target = picked; picked.SendEvent(down); }
                yield return null;
                using (var up = PointerUpEvent.GetPooled(new Event { type = EventType.MouseUp, button = 0, mousePosition = point }))
                { up.target = picked; picked.SendEvent(up); }
                yield return null;
                Assert.AreEqual(DisplayStyle.None, screen.Q("gacha-reward").resolvedStyle.display);
                Assert.IsNull(screen.Q("gacha-blackout"), "Take removes the input-blocking ceremony");

                screen.HideAsTab();
                // Home is authored on a 1080-unit panel. Check the button in that coordinate system.
                var launch = GachaScreen.LaunchButton(() => { });
                root.Add(launch);
                launch.Q<Label>("gacha-launch-label").text = "Крутка";
                yield return null;
                var caption = launch.Q<Label>("gacha-launch-label");
                Assert.Greater(launch.layout.width, caption.layout.width);
                Assert.GreaterOrEqual(caption.worldBound.xMin, launch.worldBound.xMin);
                Assert.LessOrEqual(caption.worldBound.xMax, launch.worldBound.xMax);
                Assert.LessOrEqual(caption.worldBound.yMax, launch.worldBound.yMax);
            }
            finally
            {
                LvnWallet.ResetLocal();
                LvnWords.Learn(savedBase);
                LvnWords.Translate(savedTranslation);
                Object.Destroy(go);
                Object.Destroy(settings);
                Object.Destroy(texture);
            }
        }

        private static void Save(RenderTexture texture, string name)
        {
            var dir = Environment.GetEnvironmentVariable("LVN_TEST_SHOTS");
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);
            var image = TestPixels.Read(texture);
            try { File.WriteAllBytes(Path.Combine(dir, name + ".png"), image.EncodeToPNG()); }
            finally { Object.Destroy(image); }
        }

        private static LvnManifest Manifest() => new LvnManifest
        {
            sprites = new Dictionary<string, LvnSpriteEntity>
            {
                ["Hero"] = new LvnSpriteEntity
                {
                    wardrobe = new Dictionary<string, LvnWardrobeSlot>
                    {
                        ["outfit"] = new LvnWardrobeSlot { items = new List<LvnWardrobeItem>
                        { new LvnWardrobeItem { value = "53", name = "Огненный тигр", icon = "/outfit.png", gacha = true } } },
                    },
                },
            },
        };

        private sealed class PrizeAssets : ILvnAssets, IDisposable
        {
            private readonly Texture2D _texture = new Texture2D(8, 8);
            private readonly Sprite _sprite;
            public PrizeAssets()
            {
                var path = Environment.GetEnvironmentVariable("LVN_TEST_PRIZE_ART");
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) _texture.LoadImage(File.ReadAllBytes(path));
                else { for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) _texture.SetPixel(x, y, LvnTokens.Gold); _texture.Apply(); }
                _sprite = Sprite.Create(_texture, new Rect(0, 0, _texture.width, _texture.height), Vector2.one * 0.5f);
            }
            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct) => Task.FromResult(_sprite);
            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct) => Task.FromResult<AudioClip>(null);
            public Task PreloadAsync(IReadOnlyList<string> urls, string kind, CancellationToken ct) => Task.CompletedTask;
            public void Unload(string url) { }
            public void UnloadAll() { }
            public void Dispose() { Object.Destroy(_sprite); Object.Destroy(_texture); }
        }
    }
}
