using System.Threading.Tasks;
using Lvn.Services;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// <summary>Очередь на отправку: правило ответа сервера одно на всех.</summary>
    public class OutboxTests
    {
        private const string Key = "test.outbox.queue";

        private static LvnOutbox Box(System.Func<JArray, Task<long>> send, int cap = 5)
            => new LvnOutbox("test", Key, cap: cap, flushAt: 1000, everySec: 9999f,
                             durable: true, batchMax: 100, send: send);

        [SetUp]
        [TearDown]
        public void Clean() => PlayerPrefs.DeleteKey(Key);

        [Test]
        public async Task DeliveredBatchLeavesTheQueue()
        {
            var box = Box(_ => Task.FromResult(200L));
            box.Add(new JObject { ["a"] = 1 });

            await box.FlushAsync();

            Assert.AreEqual(0, box.Count);
        }

        [Test]
        public async Task ServerTroubleKeepsTheQueue()
        {
            var box = Box(_ => Task.FromResult(503L));
            box.Add(new JObject { ["a"] = 1 });

            await box.FlushAsync();

            Assert.AreEqual(1, box.Count, "5xx — это «позже», а не «выбрось»");
        }

        [Test]
        public async Task RejectedBatchIsDroppedSoTheRestCanShip()
        {
            var box = Box(_ => Task.FromResult(400L));
            box.Add(new JObject { ["a"] = 1 });

            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning,
                new System.Text.RegularExpressions.Regex("lvn-outbox"));
            await box.FlushAsync();

            Assert.AreEqual(0, box.Count,
                "неисправимая пачка держала собой всю очередь — и не ехало НИЧЕГО");
        }

        [Test]
        public async Task TooManyRequestsIsLaterNotHopeless()
        {
            var box = Box(_ => Task.FromResult(429L));
            box.Add(new JObject { ["a"] = 1 });

            await box.FlushAsync();

            Assert.AreEqual(1, box.Count, "«слишком часто» значит «позже»");
        }

        [Test]
        public async Task NetworkFailureKeepsTheQueue()
        {
            var box = Box(_ => Task.FromResult(0L));
            box.Add(new JObject { ["a"] = 1 });

            await box.FlushAsync();

            Assert.AreEqual(1, box.Count);
        }

        [Test]
        public void OldestGoesFirstWhenTheCapIsReached()
        {
            var box = Box(_ => Task.FromResult(200L), cap: 3);
            for (int i = 0; i < 5; i++) box.Add(new JObject { ["i"] = i });

            Assert.AreEqual(3, box.Count);
            box.Modify(q => Assert.AreEqual(2, (int)q[0]["i"],
                "свежее ближе к тому, что игрок делает прямо сейчас"));
        }

        [Test]
        public void QueueSurvivesARestart()
        {
            var box = Box(_ => Task.FromResult(200L));
            box.Add(new JObject { ["a"] = 1 });

            var reborn = Box(_ => Task.FromResult(200L));
            Assert.AreEqual(1, reborn.Count, "очередь не пережила перезапуск");
        }

        [Test]
        public void CorruptStoredQueueDoesNotCrashTheGame()
        {
            PlayerPrefs.SetString(Key, "{это не массив");

            var box = Box(_ => Task.FromResult(200L));

            Assert.AreEqual(0, box.Count);
        }
    }
}
