using System;
using System.Threading.Tasks;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>Кнопка, которая ждёт, отпускает себя сама — в том числе когда
    /// ожидание сорвалось.</summary>
    public class BusyButtonTests
    {
        [Test]
        public async Task ReleasesAfterFailureBecauseADeadButtonIsForever()
        {
            var b = new Button { text = "Играть" };

            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning,
                new System.Text.RegularExpressions.Regex("lvn-busy"));
            bool ok = await LvnBusy.RunAsync(b, () => throw new InvalidOperationException("сеть"));

            Assert.IsFalse(ok);
            Assert.IsTrue(b.enabledSelf, "после сорванного ожидания кнопка мертва навсегда");
            Assert.AreEqual("Играть", b.text, "подпись осталась «занята»");
        }

        [Test]
        public async Task SecondTapWhileBusyIsIgnored()
        {
            var b = new Button { text = "Купить" };
            var gate = new TaskCompletionSource<bool>();
            int runs = 0;

            var first = LvnBusy.RunAsync(b, async () => { runs++; await gate.Task; });
            bool second = await LvnBusy.RunAsync(b, () => { runs++; return Task.CompletedTask; });

            Assert.IsFalse(second, "второй тап прошёл сквозь занятую кнопку");
            gate.SetResult(true);
            await first;
            Assert.AreEqual(1, runs);
        }

        [Test]
        public async Task WorkKeepsTheLabelItSetItself()
        {
            var b = new Button { text = "Купить" };

            await LvnBusy.RunAsync(b, () => { b.text = "Готово"; return Task.CompletedTask; });

            Assert.AreEqual("Готово", b.text, "«Готово» после покупки не превращается обратно в «Купить»");
            Assert.IsTrue(b.enabledSelf);
        }

        [Test]
        public async Task SuccessLeavesStateToTheWorkWhenAsked()
        {
            var b = new Button { text = "Стереть" };

            await LvnBusy.RunAsync(b, () => { b.SetEnabled(false); return Task.CompletedTask; },
                                   busyText: null, releaseOnSuccess: false);

            Assert.IsFalse(b.enabledSelf, "работа сама расставила состояние — дом не спорит");
        }

        [Test]
        public async Task FailureReleasesEvenWhenTheWorkOwnsTheState()
        {
            var b = new Button { text = "Стереть" };

            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning,
                new System.Text.RegularExpressions.Regex("lvn-busy"));
            await LvnBusy.RunAsync(b, () => throw new Exception("диск"),
                                   busyText: null, releaseOnSuccess: false);

            Assert.IsTrue(b.enabledSelf, "при провале кнопка отпускается ВСЕГДА — это и есть смысл дома");
        }

        [Test]
        public async Task BusyLabelIsShownWhileWaitingAndTakenBackAfter()
        {
            var b = new Button { text = "Играть" };
            string seen = null;

            await LvnBusy.RunAsync(b, () => { seen = b.text; return Task.CompletedTask; }, busyText: "Ждём…");

            Assert.AreEqual("Ждём…", seen, "игрок должен видеть, что нажатие услышано");
            Assert.AreEqual("Играть", b.text, "подпись возвращается, раз работа её не меняла");
        }

        [Test]
        public async Task ADisabledButtonIsAlreadyBusy()
        {
            // Второй тап приходит и по кнопке, которую выключил кто-то другой:
            // это по-прежнему «занята», а не «жми ещё раз».
            var b = new Button { text = "Купить" };
            b.SetEnabled(false);
            int runs = 0;

            bool ok = await LvnBusy.RunAsync(b, () => { runs++; return Task.CompletedTask; });

            Assert.IsFalse(ok);
            Assert.AreEqual(0, runs);
        }

        [Test]
        public async Task WorkWithoutAButtonStillRuns()
        {
            // Кнопки может не быть (вызов из кода) — работа от этого не отменяется.
            int runs = 0;
            bool ok = await LvnBusy.RunAsync(null, () => { runs++; return Task.CompletedTask; });

            Assert.IsTrue(ok);
            Assert.AreEqual(1, runs);
        }

        [Test]
        public async Task NoWorkIsNotSuccess()
        {
            var b = new Button { text = "Купить" };
            Assert.IsFalse(await LvnBusy.RunAsync(b, null));
            Assert.IsTrue(b.enabledSelf, "нечего ждать — нечего и выключать");
        }

        [Test]
        public void SubscribingNothingIsHarmless()
        {
            Assert.DoesNotThrow(() => LvnBusy.OnClick(null, () => Task.CompletedTask));
            Assert.DoesNotThrow(() => LvnBusy.OnClick(new Button(), null));
        }
    }
}
