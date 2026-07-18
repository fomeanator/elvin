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
            Assert.IsNull(screen.Q<TextField>(),
                "the app never asks the name by default — the novel does");
            var buttons = screen.Query<Button>().ToList();
            Assert.IsTrue(buttons.Exists(b => b.text == "Начать"));
        }

        [Test]
        public void AuthScreen_NicknameFieldCanBeDisabled_AndNullConfigIsSafe()
        {
            var withNick = new AuthScreen(new AuthConfig { ask_nickname = true }, new NoAssets());
            Assert.IsNotNull(withNick.Q<TextField>(), "a title can opt the field back in");

            var defaults = new AuthScreen(null, new NoAssets());
            Assert.IsNull(defaults.Q<TextField>(), "null config: no nickname field either");
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
