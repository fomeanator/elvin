using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.Services;
using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// <summary>
    /// ВХОД В НОВЕЛЛУ УЖЕ ОПЛАЧЕН — правило, которое стоит игроку денег
    /// каждый раз, когда оно ошибается.
    ///
    /// <para>За вход в новеллу берут плату ОДИН раз: при первом её открытии.
    /// Дальше игрок волен выходить в меню и возвращаться сколько угодно —
    /// «Продолжить» не касса. Отличить первый вход от возврата можно только по
    /// одному признаку: НАЧИНАЛИ ли эту новеллу вообще.</para>
    ///
    /// <para>Признак этот жил тем же числом, что и «докуда дошёл», и проверялся
    /// как «потолок больше нуля». У новеллы, чьи главы нумерованы с нуля (а так
    /// нумерует импортёр вводную), потолок записан нулём — и плата бралась
    /// заново при КАЖДОМ возврате. Игрок платит энергию за то, что уже купил,
    /// и жалуется не на баг, а на воровство.</para>
    ///
    /// <para>Кассир — приватный метод <see cref="NovelApp"/>, поэтому зовётся
    /// отражением. Оболочки у голого компонента нет, магазин предлагать нечем —
    /// значит списание идёт молча и целиком через кошелёк, и результат виден
    /// прямо в балансе. Кошелёк держим офлайн (пустой <c>BaseUrl</c>), как в
    /// <see cref="WalletOfflineTests"/> и <see cref="CashierTests"/>.</para>
    /// </summary>
    public sealed class TitleEntryChargeTests
    {
        private const string Id = "t_charge_novel";
        private const string Ноль = "t_charge_pilot";

        private string _адресБыл;
        private GameObject _го;
        private NovelApp _хост;

        private static LvnTitle Title(string id, int chapterNumber, long price)
            => new LvnTitle
            {
                id = id,
                cost = price > 0 ? new LvnCost { currency = "energy", amount = (int)price } : null,
                seasons = new List<LvnSeason>
                {
                    new LvnSeason { chapters = new List<LvnChapter> { new LvnChapter { id = id + "_ch", number = chapterNumber } } }
                },
            };

        [SetUp]
        public void Приготовить()
        {
            _адресБыл = LvnBackend.BaseUrl;
            LvnBackend.BaseUrl = "";      // жёсткий офлайн: судит локальное зеркало
            LvnWallet.ResetLocal();
            LvnProgress.ResetTitle(Id);
            LvnProgress.ResetTitle(Ноль);
            // Компонент без Start(): в EditMode игровой цикл не крутится, а
            // кассиру нужен только сам объект — оболочка и манифест у него null.
            _го = new GameObject("t_charge_host");
            _хост = _го.AddComponent<NovelApp>();
        }

        [TearDown]
        public void Убрать()
        {
            if (_го != null) UnityEngine.Object.DestroyImmediate(_го);
            _го = null; _хост = null;
            LvnProgress.ResetTitle(Id);
            LvnProgress.ResetTitle(Ноль);
            LvnWallet.ResetLocal();
            LvnBackend.BaseUrl = _адресБыл;
        }

        /// <summary>Позвать кассира за вход в новеллу. Метод приватный
        /// намеренно (касса — не публичное API оболочки), поэтому отражением;
        /// шов, который сделал бы это лишним, описан в отчёте.</summary>
        private Task<bool> Оплатить(LvnTitle title)
        {
            var m = typeof(NovelApp).GetMethod("ChargeTitleEntryAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(m, "кассира переименовали — проверка входа больше ничего не проверяет");
            return (Task<bool>)m.Invoke(_хост, new object[] { title });
        }

        private static long Энергия => LvnWallet.Balances.TryGetValue("energy", out var v) ? v : 0;

        // Первый вход — платный: это и есть та цена, которую видит игрок на
        // кнопке. Без этой половины правило «уже оплачен» доказывало бы себя
        // само (не берём никогда — значит никогда и не берём дважды).
        [Test]
        public async Task ПервыйВходВНовеллуОплачивается()
        {
            await LvnWallet.EarnAsync("energy", 3, "test");

            Assert.IsTrue(await Оплатить(Title(Id, 1, 1)), "хватало энергии, а войти не дали");
            Assert.AreEqual(2, Энергия, "за первый вход не списали цену новеллы");
        }

        // Новелла без своей цены не касается кошелька вовсе: выдумывать
        // умолчание нельзя, это деньги игрока.
        [Test]
        public async Task БесплатнаяНовеллаКошелькаНеКасается()
        {
            await LvnWallet.EarnAsync("energy", 3, "test");

            Assert.IsTrue(await Оплатить(Title(Id, 1, 0)));
            Assert.AreEqual(3, Энергия, "с бесплатной новеллы взяли плату");
        }

        // ВОЗВРАТ В ОПЛАЧЕННУЮ НОВЕЛЛУ. Игрок вышел в меню и нажал
        // «Продолжить» — касса обязана промолчать.
        [Test]
        public async Task ВозвратВОплаченнуюНовеллуНеБерётПлатуВторойРаз()
        {
            await LvnWallet.EarnAsync("energy", 3, "test");
            var t = Title(Id, 1, 1);
            LvnProgress.StartChapter(t, t.ChaptersOf()[0]);   // новеллу уже начинали

            Assert.IsTrue(await Оплатить(t));
            Assert.AreEqual(3, Энергия, "за возврат в уже оплаченную новеллу списали ещё раз");
        }

        // ТО ЖЕ, но главы новеллы нумерованы С НУЛЯ — так устроена вводная, в
        // которую попадает КАЖДЫЙ новый игрок. Потолок у неё записан нулём,
        // «больше нуля» ложно — и плата бралась при каждом возврате. Самая
        // дорогая половина правила: ошибка бьёт по всем игрокам сразу и
        // выглядит как списание денег ни за что.
        [Test]
        public async Task ВозвратВНовеллуСНулевойГлавойТожеНеБерётПлату()
        {
            await LvnWallet.EarnAsync("energy", 3, "test");
            var t = Title(Ноль, 0, 1);
            LvnProgress.StartChapter(t, t.ChaptersOf()[0]);

            Assert.AreEqual(0, LvnProgress.Reached(t), "потолок нулевой главы — ноль, в этом вся ловушка");

            Assert.IsTrue(await Оплатить(t));
            Assert.AreEqual(3, Энергия,
                "с новеллы, чьи главы нумерованы с нуля, плату взяли повторно — игрок платит за уже купленное");
        }

        // Дочитанная новелла: точку снял финал, потолок остался. Возврат в неё
        // — всё ещё возврат: вход был оплачен, и повторное чтение бесплатно.
        // Ровно здесь «есть точка продолжения» в одиночку дало бы ложь.
        [Test]
        public async Task ВозвратВДочитаннуюНовеллуТожеБесплатен()
        {
            await LvnWallet.EarnAsync("energy", 3, "test");
            var t = Title(Ноль, 0, 1);
            LvnProgress.StartChapter(t, t.ChaptersOf()[0]);
            LvnProgress.FinishChapter(t, null);

            Assert.IsNull(LvnProgress.Current(t), "у дочитанной новеллы точки нет — иначе проверка ни о чём");

            Assert.IsTrue(await Оплатить(t));
            Assert.AreEqual(3, Энергия, "за перечитывание оплаченной новеллы взяли плату заново");
        }
    }
}
