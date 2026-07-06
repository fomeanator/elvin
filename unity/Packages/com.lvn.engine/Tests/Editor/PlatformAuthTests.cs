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
    // The platform sign-in seam and its auth-screen buttons: no SDK hook →
    // no button; a plugged provider surfaces exactly one labelled button.
    public class PlatformAuthTests
    {
        private sealed class NoAssets : ILvnAssets
        {
            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct) => Task.FromResult<Sprite>(null);
            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct) => Task.FromResult<AudioClip>(null);
            public Task PreloadAsync(IReadOnlyList<string> urls, string kind, CancellationToken ct) => Task.CompletedTask;
            public void Unload(string url) { }
            public void UnloadAll() { }
        }

        [TearDown]
        public void Cleanup()
        {
            LvnPlatformAuth.Google = null;
            LvnPlatformAuth.Apple = null;
            LvnPlatformAuth.Dev = null;
        }

        [Test]
        public void Seam_ReportsOnlyPluggedProviders()
        {
            Assert.IsFalse(LvnPlatformAuth.Has("google"));
            LvnPlatformAuth.Google = () => Task.FromResult("tok");
            Assert.IsTrue(LvnPlatformAuth.Has("google"));
            Assert.IsFalse(LvnPlatformAuth.Has("apple"));
            Assert.IsFalse(LvnPlatformAuth.Has("martian"));
        }

        [Test]
        public async Task SignIn_WithoutAHookIsAGracefulNo()
        {
            Assert.IsFalse(await LvnPlatformAuth.SignInAsync("google"));
            LvnPlatformAuth.Google = () => Task.FromResult<string>(null); // user cancelled
            Assert.IsFalse(await LvnPlatformAuth.SignInAsync("google"));
        }

        [Test]
        public void AuthScreen_ShowsAButtonPerPluggedProvider()
        {
            LvnPlatformAuth.Google = () => Task.FromResult("tok");
            var screen = new AuthScreen(new AuthConfig { google_text = "Через Google" }, new NoAssets());
            var buttons = CollectButtons(screen);
            Assert.IsTrue(buttons.Contains("Через Google"), "plugged Google → its button");
            Assert.IsFalse(buttons.Contains("Sign in with Apple"), "no Apple hook → no Apple button");

            var off = new AuthScreen(new AuthConfig { show_google = false }, new NoAssets());
            Assert.IsFalse(CollectButtons(off).Contains("Sign in with Google"),
                "config can hide a plugged provider");
        }

        private static List<string> CollectButtons(VisualElement root)
        {
            var list = new List<string>();
            void Walk(VisualElement el)
            {
                if (el is Button b) list.Add(b.text);
                foreach (var c in el.Children()) Walk(c);
            }
            Walk(root);
            return list;
        }
    }
}
