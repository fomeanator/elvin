using System.Collections.Generic;
using System.Threading.Tasks;
using Lvn.Services;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// КАССИР — обряд оплаты целиком, шестью исходами.
    ///
    /// <para>Обряд гоняется на офлайн-половине кошелька (см.
    /// <c>WalletOfflineTests</c>): <c>BaseUrl</c> пуст, значит списание судит
    /// локальное зеркало, а не сервер. Окна подменяются заглушками — Кассир и
    /// не должен уметь их рисовать, он только спрашивает.</para>
    ///
    /// <para>Отдельно закреплено то, ради чего роль и выделялась: исход
    /// покупки после захода в магазин — <see cref="LvnCashier.Outcome.PaidAfterStore"/>,
    /// а не провал. Копия обряда в гардеробе считала иначе, и покупки самых
    /// платящих игроков уходили в отчёт как неудачи.</para>
    /// </summary>
    public class CashierTests
    {
        private string _prevUrl;
        private readonly List<string> _asked = new List<string>();
        private bool _storeOpened;

        [SetUp]
        public void Setup()
        {
            _prevUrl = LvnBackend.BaseUrl;
            LvnBackend.BaseUrl = ""; // жёсткий офлайн: судит локальное зеркало
            LvnWallet.ResetLocal();
            _asked.Clear();
            _storeOpened = false;
        }

        [TearDown]
        public void Teardown()
        {
            LvnWallet.ResetLocal();
            LvnBackend.BaseUrl = _prevUrl;
        }

        private static LvnCashier.Charge Ticket(long amount = 10) => new LvnCashier.Charge
        {
            Currency = "gold",
            Amount = amount,
            Reason = "chapter:1",
            Title = "Не хватает",
            Message = "Нужно больше золота",
            BuyText = "В магазин",
            CancelText = "Не сейчас",
        };

        // Заглушка витрины: соглашается или отказывается, и запоминает, о чём
        // спросили — формулировки принадлежат вызывающему, Кассир их не сочиняет.
        private Task<bool> Offer(bool answer, string title, string msg)
        {
            _asked.Add(title + "|" + msg);
            return Task.FromResult(answer);
        }

        [Test]
        public async Task БезЦены_ПлатитьНеЗаЧто()
        {
            var free = new LvnCashier.Charge { Currency = "", Amount = 0 };
            var outcome = await LvnCashier.ChargeAsync(free, null, null);

            Assert.AreEqual(LvnCashier.Outcome.Free, outcome);
            Assert.IsTrue(outcome.Ok(), "бесплатное пропускает");
            CollectionAssert.IsEmpty(_asked, "бесплатное не спрашивает про магазин");
        }

        [Test]
        public async Task НулеваяЦена_ТожеБесплатно()
        {
            var outcome = await LvnCashier.ChargeAsync(Ticket(0), null, null);
            Assert.AreEqual(LvnCashier.Outcome.Free, outcome);
        }

        [Test]
        public async Task Хватило_СписалиИПустили()
        {
            await LvnWallet.EarnAsync("gold", 100, "test");

            var outcome = await LvnCashier.ChargeAsync(Ticket(), null, null);

            Assert.AreEqual(LvnCashier.Outcome.Paid, outcome);
            Assert.IsTrue(outcome.Ok());
            Assert.AreEqual(90, LvnWallet.Balances["gold"], "деньги сняты ровно раз");
            CollectionAssert.IsEmpty(_asked, "хватило — магазин не предлагают");
        }

        [Test]
        public async Task НеХватило_ИгрокОтказалсяОтМагазина()
        {
            await LvnWallet.EarnAsync("gold", 5, "test");

            var outcome = await LvnCashier.ChargeAsync(Ticket(),
                (t, m) => Offer(false, t, m),
                () => { _storeOpened = true; return Task.CompletedTask; });

            Assert.AreEqual(LvnCashier.Outcome.Declined, outcome);
            Assert.IsFalse(outcome.Ok());
            Assert.IsFalse(_storeOpened, "отказался — магазин не открывают");
            Assert.AreEqual(5, LvnWallet.Balances["gold"], "отказ ничего не трогает");
            Assert.AreEqual(1, _asked.Count);
            StringAssert.Contains("Нужно больше золота", _asked[0],
                "спрашивают словами вызывающего, а не движка");
        }

        [Test]
        public async Task НеХватило_НоМагазинПомог_ЭтоПОКУПКА()
        {
            await LvnWallet.EarnAsync("gold", 5, "test");

            var outcome = await LvnCashier.ChargeAsync(Ticket(),
                (t, m) => Offer(true, t, m),
                async () =>
                {
                    _storeOpened = true;
                    await LvnWallet.EarnAsync("gold", 100, "purchase"); // в магазине купили
                });

            Assert.AreEqual(LvnCashier.Outcome.PaidAfterStore, outcome);
            Assert.IsTrue(outcome.Ok(), "покупка после магазина — покупка, а не провал");
            Assert.IsTrue(_storeOpened);
            Assert.AreEqual(95, LvnWallet.Balances["gold"], "списали один раз, не дважды");
        }

        [Test]
        public async Task НеХватило_ИПослеМагазина_ОтказСОбъяснением()
        {
            await LvnWallet.EarnAsync("gold", 5, "test");
            string explained = null;

            var outcome = await LvnCashier.ChargeAsync(Ticket(),
                (t, m) => Offer(true, t, m),
                () => Task.CompletedTask,          // из магазина вышли ни с чем
                (t, m) => { explained = t; return Task.CompletedTask; });

            Assert.AreEqual(LvnCashier.Outcome.Denied, outcome);
            Assert.IsFalse(outcome.Ok());
            Assert.AreEqual("Не хватает", explained, "отказ объясняют вслух");
            Assert.AreEqual(5, LvnWallet.Balances["gold"], "неудачная попытка не снимает денег");
        }

        [Test]
        public async Task СвоийЗаголовокОкончательногоОтказа_Уважается()
        {
            await LvnWallet.EarnAsync("gold", 1, "test");
            var charge = Ticket();
            charge.DeniedTitle = "Всё равно не хватает";
            string explained = null;

            await LvnCashier.ChargeAsync(charge,
                (t, m) => Offer(true, t, m),
                () => Task.CompletedTask,
                (t, m) => { explained = t; return Task.CompletedTask; });

            Assert.AreEqual("Всё равно не хватает", explained);
        }

        [Test]
        public async Task НечемСпросить_ЭтоНеОтказИгрока()
        {
            await LvnWallet.EarnAsync("gold", 1, "test");

            var outcome = await LvnCashier.ChargeAsync(Ticket(), null, null);

            Assert.AreEqual(LvnCashier.Outcome.NoOffer, outcome,
                "не дали, чем спрашивать — обряд честно называет это своей недостачей");
            Assert.IsFalse(outcome.Ok());
        }

        [Test]
        public async Task ПоловинаХуков_ТожеНечемСпросить()
        {
            await LvnWallet.EarnAsync("gold", 1, "test");

            var outcome = await LvnCashier.ChargeAsync(Ticket(),
                (t, m) => Offer(true, t, m), null); // спросить есть чем, открыть нечем

            Assert.AreEqual(LvnCashier.Outcome.NoOffer, outcome);
            CollectionAssert.IsEmpty(_asked, "не спрашивают того, чего не сможем сделать");
        }

        [Test]
        public async Task ПокупкаВещи_ЛожитсяВИнвентарь()
        {
            await LvnWallet.EarnAsync("gold", 100, "test");
            var charge = Ticket();
            charge.Reason = "wardrobe";
            charge.Sku = "victoria.hair.long";

            var outcome = await LvnCashier.ChargeAsync(charge, null, null);

            Assert.AreEqual(LvnCashier.Outcome.Paid, outcome);
            CollectionAssert.Contains(LvnWallet.Inventory.Keys, "victoria.hair.long",
                "без sku покупка не стала бы владением");
        }

        [Test]
        public void ИсходыДелятсяНаДваЛагеря()
        {
            Assert.IsTrue(LvnCashier.Outcome.Free.Ok());
            Assert.IsTrue(LvnCashier.Outcome.Paid.Ok());
            Assert.IsTrue(LvnCashier.Outcome.PaidAfterStore.Ok());
            Assert.IsFalse(LvnCashier.Outcome.Declined.Ok());
            Assert.IsFalse(LvnCashier.Outcome.Denied.Ok());
            Assert.IsFalse(LvnCashier.Outcome.NoOffer.Ok());
        }

        [Test]
        public async Task ПустойОрдер_НеПадает()
        {
            Assert.AreEqual(LvnCashier.Outcome.Free, await LvnCashier.ChargeAsync(null, null, null));
        }
    }
}
