using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.Services;
using Lvn.UI;
using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    // The auth screen, the store screen and the IAP catalog parser — the pure
    // parts: construction from configs, pack-card rendering, JSON handling.
    // Network paths (register/verify) are covered by the server's Go tests.
    public class ServiceScreensTests
    {
        private sealed class NoAssets : ILvnAssets
        {
            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct) => Task.FromResult<Sprite>(null);
            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct) => Task.FromResult<AudioClip>(null);
            public Task PreloadAsync(IReadOnlyList<string> urls, string kind, CancellationToken ct) => Task.CompletedTask;
            public void Unload(string url) { }
            public void UnloadAll() { }
        }

        // ── IAP catalog parsing ──
        [Test]
        public void ParseCatalog_ReadsFullAndMinimalPacks()
        {
            var packs = LvnWallet.ParseCatalog(@"{""packs"":[
                {""sku"":""gold_550"",""currency"":""gold"",""amount"":550,
                 ""title"":""Pouch"",""price"":""$4.99"",""bonus"":50},
                {""sku"":""plain"",""currency"":""crystals"",""amount"":10}
            ]}");
            Assert.AreEqual(2, packs.Count);
            Assert.AreEqual("gold_550", packs[0].Sku);
            Assert.AreEqual(550, packs[0].Amount);
            Assert.AreEqual("Pouch", packs[0].Title);
            Assert.AreEqual("$4.99", packs[0].Price);
            Assert.AreEqual(50, packs[0].Bonus);
            Assert.AreEqual("", packs[1].Title);   // minimal pack: presentation empty, not null
            Assert.AreEqual(0, packs[1].Bonus);
        }

        [Test]
        public void ParseCatalog_SkipsSkulessEntriesAndSurvivesGarbage()
        {
            var packs = LvnWallet.ParseCatalog(@"{""packs"":[{""currency"":""gold"",""amount"":5},
                {""sku"":""ok"",""currency"":""gold"",""amount"":1}]}");
            Assert.AreEqual(1, packs.Count);
            Assert.AreEqual("ok", packs[0].Sku);

            Assert.IsNull(LvnWallet.ParseCatalog("not json"));
            Assert.IsNull(LvnWallet.ParseCatalog(null));
            Assert.AreEqual(0, LvnWallet.ParseCatalog("{}").Count);
        }

        // ── StoreScreen ──
        [Test]
        public void StoreScreen_RendersACardPerPack_TitledAndComposed()
        {
            var screen = new StoreScreen(new StoreConfig
            {
                bonus_text = "+{0} extra",
                currency_names = new Dictionary<string, string> { ["gold"] = "Gold" },
            }, new NoAssets());

            screen.SetPacks(new List<LvnWallet.IapPack>
            {
                new LvnWallet.IapPack { Sku = "a", Currency = "gold", Amount = 550, Title = "Pouch", Price = "$4.99", Bonus = 50 },
                new LvnWallet.IapPack { Sku = "b", Currency = "gold", Amount = 100 }, // no title → composed
            });

            var labels = screen.Query<Label>().ToList();
            Assert.IsTrue(labels.Exists(l => l.text == "Pouch"), "titled pack keeps its title");
            Assert.IsTrue(labels.Exists(l => l.text.Contains("550") && l.text.Contains("+50 extra")),
                "sub-line carries amount and themed bonus");
            Assert.IsTrue(labels.Exists(l => l.text == "100 Gold"), "untitled pack composes amount + currency name");

            var buys = screen.Query<Button>().ToList();
            Assert.IsTrue(buys.Exists(b => b.text == "$4.99"), "priced pack's button shows the price");
            Assert.IsTrue(buys.Exists(b => b.text == "Get"), "unpriced pack falls back to the buy label");
        }

        [Test]
        public void StoreScreen_EmptyCatalogShowsTheNote()
        {
            var screen = new StoreScreen(new StoreConfig { empty_text = "closed!" }, new NoAssets());
            screen.SetPacks(null);
            var labels = screen.Query<Label>().ToList();
            Assert.IsTrue(labels.Exists(l => l.text == "closed!"));
        }

        [Test]
        public void ParseCatalog_ReadsSection()
        {
            var packs = LvnWallet.ParseCatalog(
                @"{""packs"":[{""sku"":""x"",""currency"":""energy"",""amount"":3,""section"":""currency2""}]}");
            Assert.AreEqual("currency2", packs[0].Section);
        }

        [Test]
        public void StoreScreen_GroupsBySectionWithHeadersAndPinnedBanner()
        {
            var screen = new StoreScreen(new StoreConfig
            {
                section_titles = new Dictionary<string, string> { ["currency1"] = "Coins", ["currency2"] = "Energy" },
                pay_banner_text = "Pay from RU →",
                pay_banner_url = "https://help.example/ru",
                pay_banner_always = true, // deterministic regardless of the test machine's locale
            }, new NoAssets());

            screen.SetPacks(new List<LvnWallet.IapPack>
            {
                new LvnWallet.IapPack { Sku = "g1", Currency = "gold", Amount = 100, Section = "currency1" },
                new LvnWallet.IapPack { Sku = "g2", Currency = "gold", Amount = 500, Section = "currency1" },
                new LvnWallet.IapPack { Sku = "e1", Currency = "energy", Amount = 3, Section = "currency2" },
            });

            var labels = screen.Query<Label>().ToList();
            Assert.IsTrue(labels.Exists(l => l.text == "Coins"), "section 1 heading");
            Assert.IsTrue(labels.Exists(l => l.text == "Energy"), "section 2 heading");
            Assert.AreEqual(2, labels.FindAll(l => l.text == "Pay from RU →").Count,
                "the pay banner is pinned at the top of EACH section");
        }

        [Test]
        public void StoreScreen_PayBannerHiddenForNonRuUnlessForced()
        {
            var prev = StoreScreen.RegionIsRussiaHook;
            try
            {
                var cfg = new StoreConfig { pay_banner_text = "Pay from RU →", pay_banner_url = "https://help.example/ru" };
                var packs = new List<LvnWallet.IapPack>
                {
                    new LvnWallet.IapPack { Sku = "g1", Currency = "gold", Amount = 100, Section = "currency1" },
                };

                StoreScreen.RegionIsRussiaHook = () => false; // a non-RU viewer
                var screen = new StoreScreen(cfg, new NoAssets());
                screen.SetPacks(packs);
                Assert.IsFalse(screen.Query<Label>().ToList().Exists(l => l.text == "Pay from RU →"),
                    "non-RU viewer sees no banner");

                StoreScreen.RegionIsRussiaHook = () => true; // an RU viewer
                screen.SetPacks(packs);
                Assert.IsTrue(screen.Query<Label>().ToList().Exists(l => l.text == "Pay from RU →"),
                    "RU viewer sees the banner");
            }
            finally { StoreScreen.RegionIsRussiaHook = prev; }
        }

        // ── SettingsScreen ──
        [Test]
        public void SettingsScreen_RendersRows_TogglesSound_AndShowsLinks()
        {
            bool prevSound = LvnPrefs.SoundOn;
            try
            {
                LvnPrefs.SoundOn = true;
                var screen = new SettingsScreen(new SettingsConfig
                {
                    sound_label = "Звук", on_text = "Вкл", off_text = "Выкл",
                    uid_label = "ID", version_label = "Версия",
                    terms_text = "Условия", terms_url = "https://x/terms",
                    privacy_text = "Политика", privacy_url = "https://x/privacy",
                    social = new List<SocialLink> { new SocialLink { name = "Discord", url = "https://x/dc" } },
                }, new NoAssets());
                screen.Rebuild();

                var labels = screen.Query<Label>().ToList();
                Assert.IsTrue(labels.Exists(l => l.text == "Звук"), "sound row label");
                Assert.IsTrue(labels.Exists(l => l.text == "Версия"), "version row label");
                Assert.IsTrue(labels.Exists(l => l.text == "Условия"), "terms link");
                Assert.IsTrue(labels.Exists(l => l.text == "Политика"), "privacy link");
                Assert.IsTrue(labels.Exists(l => l.text == "Discord"), "social link (no icon → name)");

                // The sound toggle reflects LvnPrefs.SoundOn (On → "Вкл").
                Assert.IsTrue(screen.Query<Button>().ToList().Exists(b => b.text == "Вкл"), "sound toggle shows On");
                LvnPrefs.SoundOn = false;
                screen.Rebuild();
                Assert.IsTrue(screen.Query<Button>().ToList().Exists(b => b.text == "Выкл"), "sound toggle now Off");
            }
            finally { LvnPrefs.SoundOn = prevSound; }
        }

        // ── AuthScreen ──
        [Test]
        public void AuthScreen_BuildsFromConfig_WithNicknameField()
        {
            var screen = new AuthScreen(new AuthConfig
            {
                title = "Добро пожаловать",
                subtitle = "tagline",
                start_text = "Начать",
            }, new NoAssets());

            var labels = screen.Query<Label>().ToList();
            Assert.IsTrue(labels.Exists(l => l.text == "Добро пожаловать"));
            Assert.IsTrue(labels.Exists(l => l.text == "tagline"));
            Assert.IsNotNull(screen.Q<TextField>(), "nickname field is on by default");
            var buttons = screen.Query<Button>().ToList();
            Assert.IsTrue(buttons.Exists(b => b.text == "Начать"));
        }

        [Test]
        public void AuthScreen_NicknameFieldCanBeDisabled_AndNullConfigIsSafe()
        {
            var noNick = new AuthScreen(new AuthConfig { ask_nickname = false }, new NoAssets());
            Assert.IsNull(noNick.Q<TextField>());

            var defaults = new AuthScreen(null, new NoAssets());
            Assert.IsNotNull(defaults.Q<TextField>());
            var buttons = defaults.Query<Button>().ToList();
            Assert.IsTrue(buttons.Exists(b => b.text == "Start"));
        }

        // ── NovelShell wiring ──
        [Test]
        public void Shell_AuthScreenFollowsTheManifestSwitch()
        {
            var assets = new NoAssets();

            var on = NovelShell.Create();
            on.Build(new LvnManifest { ui = new LvnUiConfig { auth = new AuthConfig() } }, assets);
            Assert.IsNotNull(on.Auth, "auth section present → screen built");
            Assert.IsNotNull(on.Store, "store overlay always exists");
            Object.DestroyImmediate(on.gameObject);

            var off = NovelShell.Create();
            off.Build(new LvnManifest { ui = new LvnUiConfig { auth = new AuthConfig { enabled = false } } }, assets);
            Assert.IsNull(off.Auth, "enabled:false → silent sign-in, no screen");
            Object.DestroyImmediate(off.gameObject);

            var absent = NovelShell.Create();
            absent.Build(new LvnManifest(), assets);
            Assert.IsNull(absent.Auth, "no section → no screen (the old behaviour)");
            Object.DestroyImmediate(absent.gameObject);
        }
    }
}
