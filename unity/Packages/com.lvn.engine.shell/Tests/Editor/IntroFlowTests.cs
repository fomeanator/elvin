using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    // Exercises RunAsync and the real popup. The browse only supplies player
    // choices; no stage, content server or wallet request is needed here.
    public sealed class IntroFlowTests
    {
        private sealed class Browse : ILvnBrowse
        {
            public VisualElement View { get; } = new VisualElement();
            public int Picks;
            private TaskCompletionSource<LvnTitle> _choice;

            public async Task<LvnTitle> PickTitleAsync(CancellationToken ct = default)
            {
                Picks++;
                _choice = new TaskCompletionSource<LvnTitle>();
                using var reg = ct.Register(() => _choice.TrySetResult(null));
                return await _choice.Task;
            }

            public void Choose(LvnTitle title) => _choice.SetResult(title);
            public bool RequestTitle(string id) => false;
            public void SetContent(LvnManifest manifest) { }
        }

        private GameObject _host;
        private NovelShell _shell;
        private LvnTitle _intro, _ordinary;
        private Browse _browse;
        private CancellationTokenSource _stop;
        private Task _run;
        private Func<float> _oldClock;
        private bool _oldIntroDone;
        private float _oldWalletAsk;
        private int _starts, _ends;
        private static readonly FieldInfo WalletAsk = typeof(Lvn.Services.LvnWallet)
            .GetField("_lastAsk", BindingFlags.Static | BindingFlags.NonPublic);

        [SetUp]
        public void SetUp()
        {
            _oldIntroDone = LvnPrefs.IntroDone;
            LvnPrefs.IntroDone = false;
            _oldClock = LvnClock.Now;
            float tick = 0;
            LvnClock.Now = () => tick += 10f; // finish UI fades without waiting for frames
            _oldWalletAsk = (float)WalletAsk.GetValue(null);
            WalletAsk.SetValue(null, float.PositiveInfinity); // suppress unrelated background refresh
            LvnWords.Translate(null);
            LvnWords.Learn(null, null);
            LvnScreenDirector.Current.Reset();
            _intro = MakeTitle("intro");
            _ordinary = MakeTitle("novel");
            _stop = new CancellationTokenSource();
            _browse = new Browse();
            _starts = _ends = 0;
            _run = null;

            // Build just the flow's surfaces, without a UIDocument or boot host.
            _host = new GameObject("IntroFlowTests");
            _host.SetActive(false);
            _shell = _host.AddComponent<NovelShell>();
            var root = new VisualElement();
            SetField("_root", root);
            SetField("_manifest", new LvnManifest { titles = new List<LvnTitle> { _intro, _ordinary } });
            SetProperty("Boot", new BootScreen(null, null));
            SetProperty("Loading", new LoadingScreen(null, null));
            SetProperty("Title", new TitleCard(null, null));
            SetProperty("Popup", new PopupScreen(null));
            SetProperty("Portal", new PortalConfig()); // no loading animation before the callback
            SetProperty("Browse", _browse);
            root.Add(_shell.Boot);
            root.Add(_shell.Loading);
            root.Add(_shell.Title);
            root.Add(_browse.View);
            root.Add(_shell.Popup);
            _browse.View.style.display = DisplayStyle.None;
            _shell.OnChapterSessionStart = () => _starts++;
            _shell.OnChapterSessionEnd = () => _ends++;
        }

        [TearDown]
        public async Task TearDown()
        {
            _stop.Cancel();
            _shell.Popup.Hide();
            try
            {
                if (_run != null) await _run;
            }
            finally
            {
                _stop.Dispose();
                _shell.ReleaseSubscriptions();
                UnityEngine.Object.DestroyImmediate(_host);
                LvnProgress.ResetTitle(_intro.id);
                LvnProgress.ResetTitle(_ordinary.id);
                LvnPrefs.IntroDone = _oldIntroDone;
                LvnClock.Now = _oldClock;
                WalletAsk.SetValue(null, _oldWalletAsk);
                LvnWords.Translate(null);
                LvnWords.Learn(null, null);
                LvnScreenDirector.Current.Reset();
            }
        }

        private static LvnTitle MakeTitle(string type) => new LvnTitle
        {
            id = "test-intro-flow-" + Guid.NewGuid().ToString("N"), type = type,
            seasons = new List<LvnSeason> { new LvnSeason
            {
                chapters = new List<LvnChapter> { new LvnChapter
                {
                    id = "chapter", number = 1, script_url = "/test/chapter.lvn",
                } },
            } },
        };

        private void SetField(string name, object value) => typeof(NovelShell)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_shell, value);

        private void SetProperty(string name, object value) => typeof(NovelShell)
            .GetProperty(name).SetValue(_shell, value);

        private void Run(Func<LvnTitle, LvnChapter, string, Task> play) =>
            _run = _shell.RunAsync(playChapter: play, ct: _stop.Token, bootSplash: false);

        private async Task Until(Func<bool> ready)
        {
            for (int i = 0; i < 120 && !ready() && !_run.IsCompleted; i++) await Task.Yield();
            if (_run.IsFaulted) await _run;
            Assert.IsTrue(ready(), "shell did not reach the expected wait point");
        }

        [TestCase(0, true)] // exception before any player explanation
        [TestCase(0, false)]
        [TestCase(1, true)] // host returned without completing the introduction
        [TestCase(1, false)]
        [TestCase(2, true)] // host explained a checkpoint error, then returned normally
        [TestCase(2, false)]
        public async Task IncompleteIntroExplainsAndStopsAutomaticRetries(int failure, bool hasBrowse)
        {
            if (!hasBrowse) SetProperty("Browse", null);
            int introCalls = 0, ordinaryCalls = 0;
            Run(async (title, chapter, name) =>
            {
                if (title == _ordinary) { ordinaryCalls++; return; }
                // The old hot loop must fail an assertion, not hang the runner.
                if (++introCalls > 1) { _stop.Cancel(); throw new OperationCanceledException(); }
                Assert.AreSame(_intro, title);
                Assert.AreSame(NovelShell.FirstChapter(_intro), chapter);
                _shell.Loading.style.display = DisplayStyle.Flex;
                _shell.Title.style.display = DisplayStyle.Flex;
                if (failure == 0) throw new InvalidDataException("test entry failure");
                if (failure == 2)
                    await _shell.AlertAsync("Saved progress needs attention", "Entry was cancelled.", ct: _stop.Token);
            });

            if (failure == 2)
            {
                await Until(() => _shell.Popup.IsOpen);
                Assert.AreEqual("Saved progress needs attention", _shell.Popup.Q<Label>("popup-title").text);
                _shell.Popup.Hide(); // acknowledge the host's explanation
            }
            await Until(() => _shell.Popup.IsOpen);
            Assert.AreEqual("Introduction stopped", _shell.Popup.Q<Label>("popup-title").text);
            StringAssert.Contains(hasBrowse ? "choose a story" : "close and reopen",
                _shell.Popup.Q<Label>("popup-message").text);
            Assert.AreEqual(DisplayStyle.Flex, _shell.Popup.style.display.value);
            Assert.AreEqual(DisplayStyle.None, _shell.Loading.style.display.value);
            Assert.AreEqual(DisplayStyle.None, _shell.Title.style.display.value);
            Assert.AreEqual(0, _browse.Picks, "wait for acknowledgement before entering the library");
            Assert.IsFalse(_run.IsCompleted);
            Assert.AreEqual(1, _ends, "chapter session must end before the recovery notice");
            _shell.Popup.Hide();

            if (hasBrowse)
            {
                await Until(() => _browse.Picks == 1);
                Assert.AreEqual(DisplayStyle.Flex, _browse.View.style.display.value);
                _browse.Choose(_ordinary);
                await Until(() => _browse.Picks == 2);
                Assert.AreEqual(1, ordinaryCalls);
                Assert.AreEqual(2, _ends);
                Assert.IsFalse(_shell.Popup.IsOpen, "ordinary chapter return must not repeat the notice");
            }
            else
            {
                await Until(() => _run.IsCompleted);
                await _run;
            }
            Assert.AreEqual(1, introCalls, "acknowledgement must not retry the failed introduction");
            Assert.IsTrue(_shell.HasPendingIntro, "a failed introduction must remain unfinished");
            Assert.IsFalse(LvnPrefs.IntroDone);
        }

        [Test]
        public async Task CompletedIntroGoesStraightToBrowseWithoutAnAlert()
        {
            int calls = 0;
            Run((title, chapter, name) =>
            {
                if (++calls > 1) { _stop.Cancel(); throw new OperationCanceledException(); }
                Assert.AreSame(_intro, title);
                LvnIntro.NoteFinished(title);
                return Task.CompletedTask;
            });
            await Until(() => _browse.Picks == 1);
            Assert.AreEqual(1, calls);
            Assert.AreEqual(1, _starts);
            Assert.AreEqual(1, _ends);
            Assert.IsFalse(_shell.Popup.IsOpen);
            Assert.IsFalse(_shell.HasPendingIntro);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task OrdinaryChapterReturnsToBrowseAsBefore(bool fails)
        {
            SetField("_manifest", new LvnManifest { titles = new List<LvnTitle> { _ordinary } });
            int calls = 0;
            Run((title, chapter, name) =>
            {
                calls++;
                Assert.AreSame(_ordinary, title);
                if (fails) throw new InvalidDataException("test ordinary failure");
                return Task.CompletedTask;
            });
            await Until(() => _browse.Picks == 1);
            Assert.AreEqual(0, calls, "ordinary chapters still require a choice");
            _browse.Choose(_ordinary);
            await Until(() => _browse.Picks == 2);
            Assert.AreEqual(1, calls);
            Assert.AreEqual(1, _starts);
            Assert.AreEqual(1, _ends);
            Assert.IsFalse(_shell.Popup.IsOpen);
        }
    }
}
