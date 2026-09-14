using System.Threading.Tasks;
using Lvn.Services;
using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// Как кошелёк выглядит игроку — одно правило на все экраны.
    public class WalletDisplayTests
    {
        private string _prevUrl;

        [SetUp]
        public void Reset()
        {
            _prevUrl = LvnBackend.BaseUrl;
            LvnBackend.BaseUrl = ""; // офлайн: чистое локальное зеркало
            LvnWallet.ResetLocal();
        }

        [TearDown]
        public void Clean()
        {
            LvnWallet.ResetLocal();
            LvnBackend.BaseUrl = _prevUrl;
        }

        [Test]
        public async Task PlainCurrency_ShowsGroupedNumber()
        {
            await LvnWallet.EarnAsync("crystals", 13060, "test");
            Assert.AreEqual(13060L.ToString("N0"), LvnWallet.Display("crystals"),
                "обычная валюта — просто число с разрядами");
        }

        [Test]
        public void UnknownCurrency_ReadsAsZero_NotAsCrash()
        {
            Assert.AreEqual("0", LvnWallet.Display("nope"));
            Assert.AreEqual("0", LvnWallet.Display(null));
        }

        [Test] public void CompactPillRequestsDueEnergyWithoutShowingATimer()
        {
            var clock = LvnClock.Wall;
            bool offline = LvnNetworkStatus.ForceOffline;
            int reads = 0;
            try
            {
                LvnNetworkStatus.ForceOffline = false;
                LvnClock.Wall = () => 50000f;
                LvnWallet.Apply(@"{""now"":1000,""balances"":{""energy"":2},""inventory"":{},
                    ""regen"":{""energy"":{""balance"":2,""cap"":5,""next_refill_unix"":1001}}}");
                LvnClock.Wall = () => 50002f;
                LvnWallet.SyncGet = _ => { reads++; return Task.FromResult((200L,
                    @"{""balances"":{""energy"":3},""inventory"":{},""regen"":{""energy"":{""balance"":3,""cap"":5,""next_refill_unix"":2000}}}")); };
                var pill = new LvnWalletPill("energy", new LvnWalletPill.Look { ShowTimer = false });
                Assert.AreEqual(1, reads);
                pill.Refresh();
                Assert.AreEqual("3/5", pill.Q<Label>().text);
                Assert.AreEqual(1, pill.Query<Label>().ToList().Count, "the hidden timer stays hidden");
            }
            finally { LvnWallet.SyncGet = null; LvnClock.Wall = clock; LvnNetworkStatus.ForceOffline = offline; }
        }

        // Отсчёт до восполнения — часть той же плашки, и формат у него один:
        // минуты с секундами, а за час — часы. Раньше он существовал только в
        // игровом HUD, поэтому в меню энергия молча стояла без обещания.
        [Test]
        public void RefillCountdown_ReadsAsTime()
        {
            Assert.AreEqual("0:45", LvnWalletPill.FormatDuration(45));
            Assert.AreEqual("2:05", LvnWalletPill.FormatDuration(125));
            Assert.AreEqual("1:02:05", LvnWalletPill.FormatDuration(3725));
        }
    }
}
