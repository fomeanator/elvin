using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Lvn.Tests.Runtime
{
    /// <summary>Only the current theme may publish delayed images or UI sounds.</summary>
    public class ThemeLoadConcurrencyTests
    {
        private GameObject _go;
        private PanelSettings _panel;
        private VnStage _stage;
        private DelayedAssets _assets;
        private Texture2D _texture;
        private readonly List<UnityEngine.Object> _owned = new List<UnityEngine.Object>();
        private static readonly string[] SoundFields = { "_sndClick", "_sndChoice", "_sndType" };

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _stage = TestStage.Panel("theme-load-concurrency", out _go, out _panel);
            yield return null;
            _assets = new DelayedAssets();
            _stage.Assets = _assets;
            _texture = new Texture2D(2, 2);
            _owned.Add(_texture);
            Assert.IsNotNull(Root.Q<DialogueBox>(), "The stage must have built its chrome.");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            // Retire the theme and drain pending continuations before destroying
            // the real panel and native assets, including after a failed assertion.
            _stage?.ApplyTheme(new VnTheme());
            _assets?.ReleaseAll();
            yield return null;
            yield return null;
            _stage?.ClearStage();
            if (_go != null) UnityEngine.Object.Destroy(_go);
            if (_panel != null) UnityEngine.Object.Destroy(_panel);
            yield return null;
            for (int i = _owned.Count - 1; i >= 0; i--) UnityEngine.Object.Destroy(_owned[i]);
            _owned.Clear();
            _stage = null;
            yield return null;
        }

        private VisualElement Root => _go.GetComponent<UIDocument>().rootVisualElement;

        private AudioClip Sound(int index)
            => (AudioClip)typeof(VnStage).GetField(SoundFields[index],
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_stage);

        private static string[] ImageUrls(VnTheme theme) => new[]
            { theme.PanelImageUrl, theme.PlateImageUrl, theme.ChoiceImageUrl, theme.ChoiceHoverImageUrl };

        private static Sprite[] Images(VnTheme theme) => new[]
            { theme.PanelSprite, theme.PlateSprite, theme.ChoiceSprite, theme.ChoiceHoverSprite };

        private static string[] SoundUrls(VnTheme theme) => new[]
            { theme.ClickSoundUrl, theme.ChoiceSoundUrl, theme.TypeSoundUrl };

        private VnTheme NewTheme(string id, bool images = true, int firstSound = 0)
        {
            var theme = new VnTheme
            {
                PanelImageUrl = images ? id + "/panel" : null,
                PlateImageUrl = images ? id + "/plate" : null,
                ChoiceImageUrl = images ? id + "/choice" : null,
                ChoiceHoverImageUrl = images ? id + "/hover" : null,
                ClickSoundUrl = firstSound <= 0 ? id + "/click" : null,
                ChoiceSoundUrl = firstSound <= 1 ? id + "/select" : null,
                TypeSoundUrl = id + "/typing"
            };
            foreach (string url in ImageUrls(theme))
            {
                if (url == null) continue;
                var sprite = Sprite.Create(_texture, new Rect(0, 0, 2, 2), Vector2.one * 0.5f);
                sprite.name = url;
                _owned.Add(sprite);
                _assets.Sprites.Add(url, new Pending<Sprite>(sprite));
            }
            foreach (string url in SoundUrls(theme))
            {
                if (url == null) continue;
                var clip = AudioClip.Create(url, 16, 1, 8000, false);
                _owned.Add(clip);
                _assets.Clips.Add(url, new Pending<AudioClip>(clip));
            }
            return theme;
        }

        private void Release(VnTheme theme)
        {
            foreach (string url in ImageUrls(theme))
                if (url != null) _assets.Sprites[url].Release();
            foreach (string url in SoundUrls(theme))
                if (url != null) _assets.Clips[url].Release();
        }

        private static IEnumerator WaitFor(Func<bool> ready)
        {
            float deadline = Time.realtimeSinceStartup + 3f;
            while (!ready() && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(ready(), "Timed out waiting for a controlled theme load.");
        }

        private void AssertAssets(VnTheme theme)
        {
            Assert.AreSame(theme, _stage.Theme);
            var urls = ImageUrls(theme);
            var sprites = Images(theme);
            for (int i = 0; i < urls.Length; i++)
                Assert.AreSame(_assets.Sprites[urls[i]].Value, sprites[i], urls[i]);
            urls = SoundUrls(theme);
            for (int i = 0; i < urls.Length; i++)
                Assert.AreSame(_assets.Clips[urls[i]].Value, Sound(i), urls[i]);
        }

        private void AssertChrome(VnTheme theme)
        {
            var dialogue = Root.Q<DialogueBox>();
            Assert.IsNotNull(dialogue);
            Assert.AreSame(theme.PanelSprite, dialogue.Q("vn-panel").style.backgroundImage.value.sprite);
            Assert.AreSame(theme.PlateSprite, dialogue.Q("vn-plate").style.backgroundImage.value.sprite);
            var choices = Root.Q<ChoiceList>();
            Assert.IsNotNull(choices);
            choices.Present(new[] { new LvnOption(0, "Option", null) });
            var button = choices.Q<Button>();
            Assert.IsNotNull(button);
            Assert.AreSame(theme.ChoiceSprite, button.style.backgroundImage.value.sprite);
            using (var enter = MouseEnterEvent.GetPooled())
            {
                enter.target = button;
                button.SendEvent(enter);
            }
            Assert.AreSame(theme.ChoiceHoverSprite, button.style.backgroundImage.value.sprite);
            using (var leave = MouseLeaveEvent.GetPooled())
            {
                leave.target = button;
                button.SendEvent(leave);
            }
            Assert.AreSame(theme.ChoiceSprite, button.style.backgroundImage.value.sprite);
        }

        [UnityTest] public IEnumerator LatePanel_DoesNotChangeNewTheme() => LateImage(0);
        [UnityTest] public IEnumerator LatePlate_DoesNotChangeNewTheme() => LateImage(1);
        [UnityTest] public IEnumerator LateChoice_DoesNotChangeNewTheme() => LateImage(2);
        [UnityTest] public IEnumerator LateHover_DoesNotChangeNewTheme() => LateImage(3);

        private IEnumerator LateImage(int delayed)
        {
            var a = NewTheme("a");
            var b = NewTheme("b");
            var urls = ImageUrls(a);
            _stage.ApplyTheme(a);
            for (int i = 0; i <= delayed; i++)
            {
                string url = urls[i];
                yield return WaitFor(() => _assets.Requests.Contains(url));
                if (i < delayed) _assets.Sprites[url].Release();
            }
            Assert.IsNull(Images(a)[delayed], "The old load must still be pending.");

            _stage.ApplyTheme(b);
            Release(b);
            yield return WaitFor(() => Sound(2) == _assets.Clips[b.TypeSoundUrl].Value);
            AssertAssets(b);
            AssertChrome(b);
            var dialogue = Root.Q<DialogueBox>();
            var choices = Root.Q<ChoiceList>();

            // A arrives last, after B has installed all of its own assets.
            Release(a);
            // Allow Unity's synchronization context to drain the released awaits.
            yield return null;
            yield return null;

            AssertAssets(b);
            Assert.AreSame(dialogue, Root.Q<DialogueBox>(), "A must not rebuild B's dialogue.");
            Assert.AreSame(choices, Root.Q<ChoiceList>(), "A must not rebuild B's choices.");
            AssertChrome(b);
            for (int i = delayed; i < urls.Length; i++)
            {
                Assert.IsNull(Images(a)[i], "An obsolete theme must not receive late sprites either.");
                if (i > delayed) CollectionAssert.DoesNotContain(_assets.Requests, urls[i]);
            }
            foreach (string url in SoundUrls(a)) CollectionAssert.DoesNotContain(_assets.Requests, url);
        }

        [UnityTest] public IEnumerator LateClick_DoesNotChangeNewTheme() => LateSound(0);
        [UnityTest] public IEnumerator LateSelection_DoesNotChangeNewTheme() => LateSound(1);
        [UnityTest] public IEnumerator LateTyping_DoesNotChangeNewTheme() => LateSound(2);

        private IEnumerator LateSound(int delayed)
        {
            // Earlier sound slots are empty so A is suspended at the chosen await.
            var a = NewTheme("a", images: false, firstSound: delayed);
            var b = NewTheme("b");
            var urls = SoundUrls(a);
            _stage.ApplyTheme(a);
            yield return WaitFor(() => _assets.Requests.Contains(urls[delayed]));
            Assert.IsNull(Sound(delayed), "The old sound must still be pending.");

            _stage.ApplyTheme(b);
            Release(b);
            yield return WaitFor(() => Sound(2) == _assets.Clips[b.TypeSoundUrl].Value);
            AssertAssets(b);
            var dialogue = Root.Q<DialogueBox>();
            var choices = Root.Q<ChoiceList>();
            Release(a);
            yield return null;
            yield return null;

            AssertAssets(b);
            Assert.AreSame(dialogue, Root.Q<DialogueBox>());
            Assert.AreSame(choices, Root.Q<ChoiceList>());
            for (int i = delayed + 1; i < urls.Length; i++)
                CollectionAssert.DoesNotContain(_assets.Requests, urls[i]);
        }

        [UnityTest]
        public IEnumerator CurrentTheme_LoadsEveryAssetAndRebuildsChrome()
        {
            var theme = NewTheme("current");
            _stage.ApplyTheme(theme);
            var before = Root.Q<DialogueBox>();
            var urls = ImageUrls(theme);
            for (int i = 0; i < urls.Length; i++)
            {
                int slot = i;
                string url = urls[i];
                yield return WaitFor(() => _assets.Requests.Contains(url));
                Assert.IsNull(Images(theme)[i], "No sprite may appear before its load completes.");
                _assets.Sprites[url].Release();
                yield return WaitFor(() => Images(theme)[slot] == _assets.Sprites[url].Value);
            }
            yield return WaitFor(() => !ReferenceEquals(before, Root.Q<DialogueBox>()));
            AssertChrome(theme);

            urls = SoundUrls(theme);
            for (int i = 0; i < urls.Length; i++)
            {
                int slot = i;
                string url = urls[i];
                yield return WaitFor(() => _assets.Requests.Contains(url));
                Assert.IsNull(Sound(i), "No sound may appear before its load completes.");
                _assets.Clips[url].Release();
                yield return WaitFor(() => Sound(slot) == _assets.Clips[url].Value);
            }
            AssertAssets(theme);
        }

        private sealed class Pending<T>
        {
            public readonly T Value;
            public readonly TaskCompletionSource<T> Reply = new TaskCompletionSource<T>();
            public Pending(T value) { Value = value; }
            public void Release() => Reply.TrySetResult(Value);
        }

        private sealed class DelayedAssets : ILvnAssets
        {
            public readonly Dictionary<string, Pending<Sprite>> Sprites = new Dictionary<string, Pending<Sprite>>();
            public readonly Dictionary<string, Pending<AudioClip>> Clips = new Dictionary<string, Pending<AudioClip>>();
            public readonly List<string> Requests = new List<string>();

            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct)
            {
                Requests.Add(url);
                return Sprites.TryGetValue(url, out var pending) ? pending.Reply.Task : Task.FromResult<Sprite>(null);
            }

            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct)
            {
                Requests.Add(url);
                return Clips.TryGetValue(url, out var pending) ? pending.Reply.Task : Task.FromResult<AudioClip>(null);
            }

            public void ReleaseAll()
            {
                foreach (var pending in Sprites.Values) pending.Release();
                foreach (var pending in Clips.Values) pending.Release();
            }

            public void Unload(string url) { }
            public void UnloadAll() { }
        }
    }
}
