using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    public sealed class DownloadHudTests
    {
        private Func<float> _clock;
        private float _now;
        private DownloadHud _hud;

        [SetUp]
        public void SetUp()
        {
            _clock = LvnClock.Wall;
            _now = 1f;
            LvnClock.Wall = () => _now;
            _hud = new DownloadHud();
            _hud.SetExpanded(true);
        }

        [TearDown]
        public void TearDown() => LvnClock.Wall = _clock;

        private static TransferSnapshot Snapshot(long received, int epoch = 1, long plan = 100_000_000)
            => new TransferSnapshot(1, 2, 0, received, plan, plan, 0, null, epoch);

        [Test]
        public void SmallerBatchDoesNotInheritPreviousStall()
        {
            _hud.Tick(Snapshot(50_000_000));
            _now = 20;
            _hud.Tick(Snapshot(100_000, epoch: 2));
            _now = 21;
            _hud.Tick(Snapshot(200_000, epoch: 2));
            Assert.AreNotEqual("—", _hud.Q<Label>("download-speed").text);
            Assert.AreNotEqual(LvnWords.Of("dl.waiting_data", "Waiting for data…"),
                _hud.Q<Label>("download-detail").text);
        }

        [Test]
        public void IdleThenNewWorkResetsTheSamplingWindow()
        {
            _hud.Tick(Snapshot(50_000_000));
            _now = 20;
            _hud.Tick(default);
            _now = 30;
            _hud.Tick(Snapshot(100_000));
            Assert.AreEqual("—", _hud.Q<Label>("download-speed").text);
            Assert.AreEqual(DisplayStyle.None, _hud.Q<Label>("download-eta").style.display.value);
        }

        [Test]
        public void StalledTransferHidesSpeedAndEta()
        {
            _hud.Tick(Snapshot(100_000));
            _now = 2;
            _hud.Tick(Snapshot(1_000_000));
            _now = 8;
            _hud.Tick(Snapshot(1_000_000));
            Assert.AreEqual("—", _hud.Q<Label>("download-speed").text);
            Assert.AreEqual(DisplayStyle.None, _hud.Q<Label>("download-eta").style.display.value);
        }

        [Test]
        public void ExpandedCardClearsMetricsWhenWorkEnds()
        {
            _hud.Tick(Snapshot(100_000));
            // ВЫДЕРЖКА ОКНА. Лестница качает обозами и между ними отпускает
            // сеть на доли секунды; окно держит состояние ещё несколько секунд,
            // иначе панель прыгала бы высотой на каждой границе пачки («туда-
            // сюда дёргает» — Илья 10.09). Проверяя КОНЕЦ работы, часы двигаем
            // за эту выдержку — иначе тест читает ровно то, что она и держит.
            _now = 10;
            _hud.Tick(default);
            Assert.AreEqual(LvnWords.Of("dl.idle", "No active downloads"),
                _hud.Q<Label>("download-title").text);
            // СТРОКА ПОКАЗАТЕЛЕЙ ОСТАЁТСЯ НА МЕСТЕ (10.09). Прежде она пряталась
            // в простое, и лист подпрыгивал на её высоту каждый раз, когда
            // кончался обоз лестницы. Теперь место остаётся за ней, а пустое
            // значение говорится прочерком: панель стоит смирно, а «нечего
            // показывать» видно словом.
            Assert.AreEqual(DisplayStyle.Flex, _hud.Q("download-metrics").style.display.value,
                "строка показателей снова исчезает — лист будет прыгать между пачками");
        }

        [Test]
        public void OfflinePendingSyncDoesNotPretendToUpload()
        {
            _hud.Offline = () => true;
            _hud.PendingOps = () => 3;
            int flushed = 0;
            _hud.FlushPending = () => { flushed++; return Task.CompletedTask; };
            _now = 20;
            _hud.Tick(default);
            Assert.AreEqual(LvnOfflineText.Title, _hud.Q<Label>("download-title").text);
            Assert.AreEqual(0, flushed);
        }

        [Test]
        public void UnknownPlanDoesNotInventRemainingBytes()
        {
            _hud.Tick(Snapshot(100_000, plan: 0));
            Assert.AreEqual("—", _hud.Q<Label>("download-left").text);
        }

        [Test]
        public void UnderestimatedPlanDoesNotPromiseZeroBytesLeft()
        {
            _hud.Tick(Snapshot(200_000, plan: 100_000));
            Assert.AreEqual("—", _hud.Q<Label>("download-left").text);
            Assert.AreEqual(DisplayStyle.None, _hud.Q<Label>("download-eta").style.display.value);
        }

        [Test]
        public void OldQueueSuccessDoesNotDescribeALaterUnmanagedTransfer()
        {
            var center = new DownloadCenter((_, ct) => Task.CompletedTask, _ => true);
            center.Enqueue("Chapter", 100, Items());
            _hud.Center = center;
            _hud.SetExpanded(false);
            _hud.Tick(default);
            _now = 2;
            _hud.Tick(Snapshot(10));
            _now = 12;   // за выдержкой окна: прежняя очередь давно кончилась
            _hud.Tick(default);
            Assert.AreEqual(LvnWords.Of("dl.idle", "No active downloads"),
                _hud.Q<Label>("download-title").text);
        }

        [Test]
        public void RetryButtonReflectsReconnectWithoutReopening()
        {
            bool offline = true;
            var center = new DownloadCenter((_, ct) => Task.CompletedTask, _ => false);
            _hud.Center = center;
            _hud.Offline = () => offline;
            center.Enqueue("Chapter", 100, Items());
            _hud.Tick(default);
            Assert.IsFalse(_hud.Q<Button>("download-retry").enabledSelf);
            offline = false;
            _hud.Tick(default);
            Assert.IsTrue(_hud.Q<Button>("download-retry").enabledSelf);
        }

        [Test]
        public async Task SuccessfulQueueUpdatesAnAlreadyOpenCard()
        {
            var done = new TaskCompletionSource<bool>();
            bool cached = false;
            var center = new DownloadCenter(async (_, ct) => { await done.Task; cached = true; }, _ => cached);
            _hud.Center = center;
            center.Enqueue("Story — chapter 3", 100, Items());
            _hud.Tick(Snapshot(10, plan: 100));
            done.SetResult(true);
            await center.WhenDrainedAsync();
            _now = 10;   // за выдержкой окна: работа кончилась совсем
            _hud.Tick(default);
            Assert.AreEqual(LvnWords.Of("dl.finished", "Download complete"),
                _hud.Q<Label>("download-title").text);
        }

        [Test]
        public void FailedQueueStaysVisibleAndOffersRetry()
        {
            var center = new DownloadCenter((_, ct) => Task.CompletedTask, _ => false);
            _hud.Center = center;
            center.Enqueue("Story — chapter 3", 100, Items());
            _hud.Tick(default);
            Assert.IsTrue(_hud.HasWork);
            Assert.IsNotNull(_hud.Q<Button>("download-retry"));
            Assert.AreEqual(LvnWords.Of("dl.failed", "Download incomplete"),
                _hud.Q<Label>("download-title").text);
        }

        private static List<PreloadItem> Items()
            => new List<PreloadItem> { new PreloadItem { Url = "/a.bin", Size = 100 } };
    }
}
