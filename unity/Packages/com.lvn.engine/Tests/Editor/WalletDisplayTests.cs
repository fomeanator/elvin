using System.Threading.Tasks;
using Lvn.Services;
using Lvn.UI.Screens;
using NUnit.Framework;

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
