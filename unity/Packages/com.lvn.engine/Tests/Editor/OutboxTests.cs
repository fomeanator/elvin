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

        [Test]
        public async Task AnyTwoHundredCountsAsDelivered()
        {
            // Правило записано как «2xx», а не «ровно 200»: сервер вправе
            // ответить 202 или 204 — обе очереди раньше считали это отказом и
            // слали ту же пачку заново, вечно.
            foreach (var code in new[] { 201L, 202L, 204L })
            {
                var box = Box(_ => Task.FromResult(code));
                box.Add(new JObject { ["a"] = 1 });
                await box.FlushAsync();
                Assert.AreEqual(0, box.Count, "код " + code);
                box.Clear();
            }
        }

        [Test]
        public async Task WaitAWhileIsLaterNotHopeless()
        {
            var box = Box(_ => Task.FromResult(408L));
            box.Add(new JObject { ["a"] = 1 });

            await box.FlushAsync();

            Assert.AreEqual(1, box.Count, "«подожди» значит «позже», как и 429");
        }

        [Test]
        public async Task EveryOtherFourHundredIsHopeless()
        {
            foreach (var code in new[] { 401L, 403L, 404L, 413L, 422L })
            {
                var box = Box(_ => Task.FromResult(code));
                box.Add(new JObject { ["a"] = 1 });
                UnityEngine.TestTools.LogAssert.Expect(LogType.Warning,
                    new System.Text.RegularExpressions.Regex("lvn-outbox"));
                await box.FlushAsync();
                Assert.AreEqual(0, box.Count, "код " + code + " повтором не чинится");
                box.Clear();
            }
        }

        [Test]
        public async Task ThrownSendKeepsTheQueue()
        {
            // Обрыв соединения прилетает исключением, а не кодом: пачку держим.
            var box = Box(_ => throw new System.Net.WebException("обрыв"));
            box.Add(new JObject { ["a"] = 1 });

            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning,
                new System.Text.RegularExpressions.Regex("lvn-outbox"));
            await box.FlushAsync();

            Assert.AreEqual(1, box.Count);
        }

        [Test]
        public async Task OnlyOneBatchGoesPerFlush()
        {
            int sent = 0;
            var box = new LvnOutbox("test", Key, cap: 10, flushAt: 1000, everySec: 9999f,
                                    durable: true, batchMax: 2,
                                    send: b => { sent = b.Count; return Task.FromResult(200L); });
            for (int i = 0; i < 5; i++) box.Add(new JObject { ["i"] = i });

            await box.FlushAsync();

            Assert.AreEqual(2, sent, "за раз уходит не больше batchMax");
            Assert.AreEqual(3, box.Count, "выброшено ровно доставленное, не вся очередь");
            box.Modify(q => Assert.AreEqual(2, (int)q[0]["i"], "уехали САМЫЕ СТАРЫЕ"));
        }

        [Test]
        public async Task EmptyQueueDoesNotBotherTheServer()
        {
            int calls = 0;
            var box = Box(_ => { calls++; return Task.FromResult(200L); });

            await box.FlushAsync();

            Assert.AreEqual(0, calls, "пустая пачка — это запрос ни о чём");
        }

        [Test]
        public void NothingIsNotAnEvent()
        {
            var box = Box(_ => Task.FromResult(200L));
            box.Add(null);
            Assert.AreEqual(0, box.Count);
        }

        [Test]
        public void ClearForgetsTheDeviceCopyToo()
        {
            var box = Box(_ => Task.FromResult(200L));
            box.Add(new JObject { ["a"] = 1 });
            box.Clear();

            Assert.AreEqual(0, box.Count);
            Assert.AreEqual(0, Box(_ => Task.FromResult(200L)).Count,
                "иначе забытая очередь воскресает следующим запуском");
        }

        [Test]
        public async Task KeptQueueSurvivesARestartAfterAFailedSend()
        {
            var box = Box(_ => Task.FromResult(503L));
            box.Add(new JObject { ["a"] = 1 });
            await box.FlushAsync();

            Assert.AreEqual(1, Box(_ => Task.FromResult(200L)).Count,
                "пережить перезапуск обязана именно та очередь, которую не приняли");
        }
    }
}
