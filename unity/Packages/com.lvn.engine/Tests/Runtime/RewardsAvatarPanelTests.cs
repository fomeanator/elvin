using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.Services;
using Lvn.UI;
using Lvn.UI.Screens;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Lvn.Tests
{
    public class RewardsAvatarPanelTests
    {
        private GameObject _go;
        private PanelSettings _settings;
        private RenderTexture _texture;
        private VisualElement _root;
        private Art _art;
        private string _url, _picked, _auto;
        private bool _offline;

        [SetUp] public void SetUp()
        {
            TestPixels.RequireGraphics();
            _url = LvnBackend.BaseUrl; _offline = LvnNetworkStatus.ForceOffline; _picked = LvnAvatars.Picked;
            _auto = LvnPrefs.GachaAuto; LvnPrefs.GachaAuto = "off";
            LvnNetworkStatus.ForceOffline = false; LvnWallet.ResetLocal();
            _settings = ScriptableObject.CreateInstance<PanelSettings>();
            _texture = new RenderTexture(390, 844, 24); _texture.Create();
            _settings.targetTexture = _texture;
            _settings.scaleMode = PanelScaleMode.ConstantPixelSize; _settings.scale = 390f / 1080f;
            _settings.clearColor = true; _settings.colorClearValue = LvnTokens.Bg;
#if UNITY_EDITOR
            _settings.themeStyleSheet = UnityEditor.AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(
                "Assets/UI Toolkit/UnityThemes/UnityDefaultRuntimeTheme.tss");
#endif
            _go = new GameObject("rewards-avatar-panel");
            var document = _go.AddComponent<UIDocument>(); document.panelSettings = _settings;
            _root = document.rootVisualElement; _root.style.width = 1080; _root.style.height = 844f * 1080f / 390f;
            LvnFonts.ApplyDefault(_root); LvnMotion.EnableTapFeedback(_root);
            _art = new Art();
        }
        [TearDown] public void TearDown()
        {
            _root.Clear(); Object.Destroy(_go); Object.Destroy(_settings); Object.Destroy(_texture); _art.Dispose();
            LvnAvatars.Picked = _picked; LvnWallet.ResetLocal();
            LvnBackend.BaseUrl = _url; LvnNetworkStatus.ForceOffline = _offline;
            LvnPrefs.GachaAuto = _auto;
        }

        [UnityTest] public IEnumerator TouchCurrencySpinUpdatesEnergyAndReturnsToIdle()
        {
            using var server = new SpinServer();
            LvnBackend.BaseUrl = server.Root;
            var refresh = LvnWallet.RefreshAsync();
            yield return Until(() => refresh.IsCompleted);
            Assert.IsTrue(refresh.Result);
            var bar = new LvnTopBar { Currencies = new List<string> { "energy", "crystals" } };
            _root.Add(bar);
            var screen = new GachaScreen(_art);
            screen.SetContent(Manifest()); _root.Add(screen);
            var showing = screen.RunAsync();
            yield return Until(() => screen.Q<Button>("gacha-spin") != null);
            yield return new WaitForSecondsRealtime(0.4f);
            yield return Tap(screen.Q<Button>("gacha-spin"));
            yield return Until(() => server.Spins == 1 && screen.Q<Button>("gacha-spin") != null, 12f);
            Assert.AreEqual(152, LvnWallet.Balance("energy"));
            Assert.AreEqual(50, LvnWallet.Balance("crystals"));
            var energy = bar.Query<LvnWalletPill>().ToList().Find(p => p.Currency == "energy");
            Assert.IsNotNull(energy);
            Assert.AreEqual("152", energy.Q<Label>().text, "HUD changes without reopening the screen");
            Shot("energy-prize-touch");
            Assert.IsNull(screen.Q<Button>("gacha-take"), "currency lands on the reel; only rare prizes have a ceremony");
            Assert.AreEqual(DisplayStyle.None, screen.Q("gacha-reward").resolvedStyle.display);
            Assert.AreEqual(1, server.Spins, "taking a prize must not start or charge another spin");
            screen.RequestCancel(); yield return Until(() => showing.IsCompleted);
            Assert.IsFalse(showing.IsFaulted, showing.Exception?.ToString());
        }

        [UnityTest] public IEnumerator AvatarPreviewPurchaseAndSetSurviveReopening()
        {
            LvnNetworkStatus.ForceOffline = true;
            LvnWallet.Apply(@"{""balances"":{""crystals"":200},""inventory"":{}}");
            LvnAvatars.Picked = "free";
            var screen = new AvatarPickScreen(_art); screen.SetContent(Manifest()); _root.Add(screen);
            var showing = screen.ShowAsync();
            yield return new WaitForSecondsRealtime(0.5f);
            yield return Tap(screen.Q<Button>("avatar-paid"));
            Assert.AreEqual("free", LvnAvatars.Picked, "preview must not buy or install the face");
            Assert.AreEqual(200, LvnWallet.Balance("crystals"));
            Shot("avatar-preview");
            yield return Tap(screen.Q<Button>("avatar-apply"));
            yield return Until(() => LvnAvatars.Picked == "paid");
            Assert.AreEqual(50, LvnWallet.Balance("crystals"));
            Assert.IsTrue(LvnWallet.Has("avatar.paid"));
            Assert.IsFalse(screen.Q<Button>("avatar-apply").enabledSelf);
            var art = screen.Q<Button>("avatar-paid").Q("avatar-art");
            Assert.AreEqual(1f, art.resolvedStyle.backgroundColor.a);
            Shot("avatar-selected");
            screen.RequestCancel(); yield return Until(() => showing.IsCompleted);
            screen.RemoveFromHierarchy();
            LvnWallet.ReloadLocal();
            Assert.AreEqual("/heroine.png", LvnAvatars.Url(Manifest()));
            var circle = new VisualElement(); circle.style.width = 220; circle.style.height = 220; _root.Add(circle);
            LvnPortraitFace.Show(circle, "/friend.png", Manifest(), _art, forceSelf: true);
            Assert.IsNotNull(circle.Q("lvn-hero-face"));
            yield return null; Shot("avatar-live-portrait");
            LvnPortraitFace.Show(circle, LvnAvatars.Url(Manifest()), Manifest(), _art);
            Assert.IsNull(circle.Q("lvn-hero-face"), "switching from the hero to a static avatar removes the old layers");
            Assert.AreEqual(1, circle.childCount);
            Assert.AreEqual(Overflow.Hidden, circle.style.overflow.value);
        }

        [UnityTest] public IEnumerator RestorePurchasesWaitsForTheServerAndCanRetryFailure()
        {
            var previous = LvnWallet.SyncGet;
            var reply = new TaskCompletionSource<(long, string)>();
            int reads = 0;
            LvnWallet.SyncGet = _ => { reads++; return reply.Task; };
            try
            {
                var settings = new SettingsScreen(null, null);
                var make = typeof(SettingsScreen).GetMethod("RestoreRow",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                _root.Add((VisualElement)make.Invoke(settings, null));
                yield return null;
                var button = _root.Q<Button>("restore-purchases");
                yield return Tap(button);
                Assert.AreEqual(1, reads);
                Assert.IsFalse(button.enabledSelf);
                yield return new WaitForSecondsRealtime(1f);
                Assert.AreNotEqual(LvnWords.Of("common.done", "Done"), button.text,
                    "Success must not be driven by a presentation timer");
                reply.SetResult((0, ""));
                yield return Until(() => button.enabledSelf);
                Assert.AreEqual(LvnWords.Of("network.title", "No connection"), button.text);

                reply = new TaskCompletionSource<(long, string)>();
                yield return Tap(button);
                reply.SetResult((200, "{\"balances\":{\"crystals\":123},\"inventory\":{\"avatar.paid\":1}}"));
                yield return Until(() => button.enabledSelf);
                Assert.AreEqual(LvnWords.Of("common.done", "Done"), button.text);
                Assert.AreEqual(123, LvnWallet.Balance("crystals"));
                Assert.IsTrue(LvnWallet.Has("avatar.paid"));
            }
            finally { LvnWallet.SyncGet = previous; }
        }

        [UnityTest] public IEnumerator SettingsSoundRestoresLevelsAndAllQualityAndSocialButtonsWork()
        {
            bool sound = LvnPrefs.SoundOn;
            float music = LvnPrefs.VolMusic, sfx = LvnPrefs.VolSfx;
            string quality = LvnPrefs.ArtQuality;
            var opener = LvnWebView.Opener;
            var opened = new List<string>();
            LvnWebView.Opener = url => { opened.Add(url); return true; };
            LvnNetworkStatus.ForceOffline = true;
            LvnPrefs.SoundOn = true; LvnPrefs.VolMusic = 0.7f; LvnPrefs.VolSfx = 0.4f;
            try
            {
                var config = JsonConvert.DeserializeObject<SettingsConfig>(@"{
                    ""simple_audio"":true,""social"":[
                    {""name"":""VK"",""url"":""https://vk.com/test""},
                    {""name"":""Telegram"",""url"":""https://t.me/test""},
                    {""name"":""TikTok"",""url"":""https://www.tiktok.com/@test""}]}");
                var settings = new SettingsScreen(config, _art); _root.Add(settings);
                var showing = settings.ShowAsync();
                yield return new WaitForSecondsRealtime(0.4f);
                yield return OpenSection(settings, "sound");
                var sliders = settings.Q<ScrollView>().contentContainer.Query<Slider>().ToList();
                Assert.GreaterOrEqual(sliders.Count, 2);
                var viewport = settings.Q<ScrollView>().contentViewport;
                Shot("settings-layout");
                foreach (var slider in sliders)
                {
                    Assert.GreaterOrEqual(slider.worldBound.xMin, viewport.worldBound.xMin);
                    Assert.LessOrEqual(slider.worldBound.xMax, viewport.worldBound.xMax,
                        $"slider={slider.worldBound}, row={slider.parent.worldBound}, viewport={viewport.worldBound}, content={settings.Q<ScrollView>().contentContainer.worldBound}");
                }
                yield return Tap(settings.Q<Button>("settings-sound"));
                for (int i = 0; i < 2; i++)
                {
                    Assert.AreEqual(0, sliders[i].value);
                    Assert.IsFalse(sliders[i].enabledSelf);
                }
                Assert.AreEqual(0.7f, LvnPrefs.VolMusic);
                Assert.AreEqual(0.4f, LvnPrefs.VolSfx);
                settings.RequestCancel(); yield return Until(() => showing.IsCompleted);
                settings.RemoveFromHierarchy();
                settings = new SettingsScreen(config, _art); _root.Add(settings);
                showing = settings.ShowAsync();
                yield return new WaitForSecondsRealtime(0.4f);
                yield return OpenSection(settings, "sound");
                yield return Tap(settings.Q<Button>("settings-sound"));
                sliders = settings.Q<ScrollView>().contentContainer.Query<Slider>().ToList();
                Assert.AreEqual(0.7f, sliders[0].value);
                Assert.AreEqual(0.4f, sliders[1].value);
                Assert.IsTrue(sliders[0].enabledSelf && sliders[1].enabledSelf);
                Shot("settings-sound");
                var scroll = settings.Q<ScrollView>();
                yield return OpenSection(settings, "graphics");
                foreach (string label in new[] { "2K", "1440p", "1K" })
                {
                    var button = settings.Query<Button>().ToList().Find(b => b.text == label);
                    Assert.IsNotNull(button);
                    scroll.ScrollTo(button); yield return null;
                    Assert.GreaterOrEqual(button.worldBound.xMin, scroll.worldBound.xMin);
                    Assert.LessOrEqual(button.worldBound.xMax, scroll.worldBound.xMax);
                    yield return Tap(button);
                }
                Assert.AreEqual("1k", LvnPrefs.ArtQuality);
                Shot("settings-quality");
                var social = settings.Q("settings-social");
                yield return OpenSection(settings, "data");
                scroll.ScrollTo(social); yield return null;
                var buttons = social.Query<Button>().ToList();
                Assert.AreEqual(3, buttons.Count);
                foreach (var button in buttons)
                {
                    Assert.IsTrue(string.IsNullOrEmpty(button.text), "Known networks render icons without a custom asset");
                    yield return Tap(button);
                }
                Assert.AreEqual(3, opened.Count);
                Assert.AreEqual("https://t.me/test", opened[1]);
                Shot("settings-social");
                settings.RequestCancel(); yield return Until(() => showing.IsCompleted);
            }
            finally
            {
                LvnWebView.Opener = opener;
                LvnPrefs.SoundOn = sound; LvnPrefs.VolMusic = music; LvnPrefs.VolSfx = sfx;
                LvnPrefs.ArtQuality = quality;
            }
        }

        [UnityTest] public IEnumerator WardrobeClearsLegacySavedEmotionButKeepsTheOutfit()
        {
            const string who = "legacy-emotion-test";
            LvnNetworkStatus.ForceOffline = true;
            var manifest = JsonConvert.DeserializeObject<LvnManifest>(@"{""sprites"":{""legacy-emotion-test"":{
                ""axes"":{""emotion"":[""idle"",""angry""],""outfit"":[""base""],""face"":[""base""]},
                ""defaults"":{""emotion"":""idle"",""outfit"":""base""},
                ""wardrobe"":{""outfit"":{""items"":[{""value"":""base"",""name"":""Base""}]},
                    ""face"":{""items"":[{""value"":""base"",""name"":""Appearance""}]}},
                ""layers"":[{""url"":""/body_west.png""}]}}}");
            var sheet = new WardrobeSheet(null, _art); sheet.SetContent(manifest); _root.Add(sheet);
            try
            {
                LvnWardrobe.Equip(who, "emotion", "angry"); // a save from the old build
                LvnWardrobe.Equip(who, "outfit", "base");
                LvnWardrobe.Equip(who, "face", "base");
                var showing = sheet.ShowAsync(who);
                yield return null;
                Assert.IsFalse(LvnWardrobe.Equipped(who).ContainsKey("emotion"),
                    "Skipping new emotion commits must also repair an existing saved emotion");
                Assert.AreEqual("base", LvnWardrobe.Equipped(who)["outfit"]);
                Assert.AreEqual("base", LvnWardrobe.Equipped(who)["face"], "A real appearance slot is not a temporary emotion");
                LvnWardrobe.Preview(who, "emotion", "angry");
                sheet.Hide(); yield return Until(() => showing.IsCompleted);
                Assert.AreEqual("idle", LvnCostumer.Chosen(who, "emotion", manifest.sprites[who].defaults));
                Assert.AreEqual("base", LvnWardrobe.Equipped(who)["outfit"]);
            }
            finally
            {
                sheet.Hide();
                LvnWardrobe.ClearPreview(who);
                LvnWardrobe.Equip(who, "emotion", null);
                LvnWardrobe.Equip(who, "outfit", null);
                LvnWardrobe.Equip(who, "face", null);
            }
        }

        [UnityTest] public IEnumerator RussianButtonLeavesFreshAutoAndUpdatesTheVisibleSettings()
        {
            const string key = "lvn_pref_locale";
            bool chosen = LvnPrefs.LocaleChosen;
            string locale = LvnPrefs.Locale, original = LvnPrefs.OriginalLocale;
            var languages = LvnPrefs.AvailableLocales;
            var host = new GameObject("locale-host"); host.SetActive(false);
            var app = host.AddComponent<NovelApp>();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(NovelApp).GetField("_localeApplied", flags).SetValue(app, "en");
            var changed = typeof(NovelApp).GetMethod("OnPrefsMaybeLocale", flags);
            Action apply = () => changed.Invoke(app, null);
            try
            {
                PlayerPrefs.DeleteKey(key); LvnPrefs.Reload();
                LvnPrefs.OriginalLocale = "ru"; LvnPrefs.AvailableLocales = new[] { "en" };
                LvnWords.Learn(new Dictionary<string, string> { ["settings.title"] = "Настройки" });
                LvnWords.Translate(new Dictionary<string, string> { ["settings.title"] = "Settings" });
                LvnPrefs.Changed += apply;
                LvnRedress.Register(_root);
                var screen = new SettingsScreen(new SettingsConfig(), _art); _root.Add(screen);
                var showing = screen.ShowAsync();
                yield return new WaitForSecondsRealtime(0.4f);
                Assert.IsTrue(screen.Query<Label>().ToList().Exists(l => l.text == "Settings"));
                yield return OpenSection(screen, "text");
                LvnPerf.Start(); LvnPerf.Context = "settings";
                var russian = screen.Query<Button>().ToList().Find(b => b.text == "Русский");
                Assert.IsNotNull(russian);
                screen.Q<ScrollView>().ScrollTo(russian); yield return null;
                yield return Tap(russian);
                yield return Until(() => screen.Query<Label>().ToList().Exists(l => l.text == "Настройки"));
                Assert.IsTrue(LvnPrefs.LocaleChosen);
                Assert.AreEqual("", app.CurrentLocale);
                LvnPrefs.Reload();
                Assert.AreEqual("", LvnLocale.Chosen);
                Shot("settings-russian-switched");
                var handles = new List<Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle>();
                Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle.GetAvailable(handles);
                var markerNames = new List<string>();
                foreach (var handle in handles)
                {
                    var d = Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle.GetDescription(handle);
                    if (d.Name.Contains("UI") || d.Name.Contains("Layout") || d.Name.Contains("Text") || d.Name.Contains("Glyph"))
                        markerNames.Add(d.Category.Name + ":" + d.Name);
                }
                Debug.Log("[perf-test] ui-markers=" + string.Join(",", markerNames));
                screen.RequestCancel(); yield return Until(() => showing.IsCompleted);
            }
            finally
            {
                LvnPrefs.Changed -= apply;
                LvnPerf.Stop(); LvnPerf.Context = null;
                Object.Destroy(host);
                LvnPrefs.OriginalLocale = original; LvnPrefs.AvailableLocales = languages;
                if (chosen) PlayerPrefs.SetString(key, locale); else PlayerPrefs.DeleteKey(key);
                LvnPrefs.Reload(); LvnWords.Translate(null); LvnWords.Learn(null, null);
            }
        }

        private IEnumerator OpenSection(SettingsScreen screen, string id)
        {
            if (screen.Q("settings-body-" + id).resolvedStyle.display == DisplayStyle.None)
            {
                var header = screen.Q("settings-sec-" + id);
                screen.Q<ScrollView>().ScrollTo(header);
                yield return null;
                yield return Tap(header);
                yield return null;
            }
            Assert.AreEqual(DisplayStyle.Flex, screen.Q("settings-body-" + id).resolvedStyle.display);
        }

        private IEnumerator Tap(VisualElement button)
        {
            Assert.IsNotNull(button); Assert.IsTrue(button.enabledInHierarchy);
            yield return null;
            var point = button.worldBound.center;
            var target = _root.panel.Pick(point);
            Assert.IsTrue(target == button || button.Contains(target), "touch intercepted by " + target?.name);
            using (var down = PointerDownEvent.GetPooled(new Touch { fingerId = 0, phase = TouchPhase.Began, position = point }, EventModifiers.None))
            { down.target = target; target.SendEvent(down); }
            yield return null;
            using (var up = PointerUpEvent.GetPooled(new Touch { fingerId = 0, phase = TouchPhase.Ended, position = point }, EventModifiers.None))
            { up.target = target; target.SendEvent(up); }
            yield return null;
        }
        private static IEnumerator Until(Func<bool> condition, float timeout = 8f)
        {
            float until = Time.realtimeSinceStartup + timeout;
            while (!condition() && Time.realtimeSinceStartup < until) yield return null;
            Assert.IsTrue(condition(), "timed out waiting for the UI/network result");
        }
        private void Shot(string name)
        {
            var dir = Environment.GetEnvironmentVariable("LVN_TEST_SHOTS");
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir); var image = TestPixels.Read(_texture);
            try { File.WriteAllBytes(Path.Combine(dir, name + ".png"), image.EncodeToPNG()); }
            finally { Object.Destroy(image); }
        }
        private static LvnManifest Manifest() => JsonConvert.DeserializeObject<LvnManifest>(@"{
            ""ui"":{""wardrobe"":{""entity"":""test-avatar""},""browse"":{""skin"":""/skin/"",""avatar"":""/friend.png"",
            ""avatars"":[{""id"":""free"",""url"":""/friend.png""},{""id"":""paid"",""url"":""/heroine.png"",""currency"":""crystals"",""price"":150}]}},
            ""sprites"":{""test-avatar"":{""layers"":[{""url"":""/body_west.png""},{""url"":""/face_west_idle.png""},{""url"":""/clothes_rose.png""},{""url"":""/hair_rose_brunette.png""}]}}}");

        private sealed class Art : ILvnAssets, IDisposable
        {
            private readonly Dictionary<string, Sprite> _sprites = new Dictionary<string, Sprite>();
            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct)
            {
                if (!_sprites.TryGetValue(url, out var sprite))
                {
                    string filename = url.Contains("_west") || url.Contains("_rose") ? Path.GetFileName(url) : url.Contains("card-back") ? "avatar-card-back.png"
                        : url.Contains("heroine") ? "avatar-heroine.png" : "avatar-friend.png";
                    var texture = new Texture2D(8, 8);
                    var dir = Environment.GetEnvironmentVariable("LVN_TEST_ART_DIR");
                    var path = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, filename);
                    if (path != null && File.Exists(path)) texture.LoadImage(File.ReadAllBytes(path));
                    else { for (int x = 0; x < 8; x++) for (int y = 0; y < 8; y++) texture.SetPixel(x, y, LvnTokens.Gold); texture.Apply(); }
                    sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), Vector2.one * 0.5f);
                    _sprites[url] = sprite;
                }
                return Task.FromResult(sprite);
            }
            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct) => Task.FromResult<AudioClip>(null);
            public Task PreloadAsync(IReadOnlyList<string> urls, string kind, CancellationToken ct) => Task.CompletedTask;
            public void Unload(string url) { }
            public void UnloadAll() { }
            public void Dispose() { foreach (var s in _sprites.Values) { Object.Destroy(s.texture); Object.Destroy(s); } }
        }
        private sealed class SpinServer : IDisposable
        {
            private readonly HttpListener _listener = new HttpListener();
            public readonly string Root;
            public int Spins;
            public SpinServer()
            {
                var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
                int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
                Root = "http://127.0.0.1:" + port; _listener.Prefixes.Add(Root + "/"); _listener.Start();
                LvnAsync.Fire(Serve(), "SpinTestServer");
            }
            private async Task Serve()
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); } catch { return; }
                    string path = ctx.Request.Url.AbsolutePath;
                    string body;
                    if (path == "/v1/gacha") body = @"{""spin_currency"":""crystals"",""spin_price"":50,""sectors"":[{""id"":""energy"",""kind"":""currency"",""currency"":""energy"",""amount"":150}]}";
                    else if (path == "/v1/gacha/spin") { Spins++; body = @"{""sector"":""energy"",""kind"":""currency"",""currency"":""energy"",""amount"":150}"; }
                    else body = Spins == 0 ? @"{""balances"":{""crystals"":100,""energy"":2},""inventory"":{}}"
                        : @"{""balances"":{""crystals"":50,""energy"":152},""inventory"":{},""regen"":{""energy"":{""cap"":5,""balance"":152,""next_refill_unix"":0}}}";
                    byte[] bytes = Encoding.UTF8.GetBytes(body); ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length); ctx.Response.Close();
                }
            }
            public void Dispose() { _listener.Stop(); _listener.Close(); }
        }
    }
}
