using NUnit.Framework;
using Lvn.UI.Screens;

namespace Lvn.Tests
{
    /// <summary>
    /// НАВБАР ДЕРЖИТСЯ, ПОКА ОТКРЫТО МЕНЮ ГЛАВЫ.
    ///
    /// <para>У бара свой отсчёт тишины, у меню — своя жизнь. Первая привязка
    /// ставила отсчёт на паузу только бару, который УЖЕ был показан; бар,
    /// открытый самим меню, взводил отсчёт при показе и через пять секунд
    /// уезжал из-под открытого меню. Правило теперь одно —
    /// <see cref="LvnTopBar.BarRests"/> — и им пользуются оба места.</para>
    ///
    /// <para>Сам отсчёт (schedule) без панели не тикает — живой сценарий
    /// проверяется глазами в главе: бургер в бабликах → меню → бар стоит.</para>
    /// </summary>
    public class TopBarMenuHoldTests
    {
        [Test]
        public void TheBarRestsOnlyWhenShownAndNoMenuIsOpen()
        {
            Assert.IsTrue(LvnTopBar.BarRests(barShown: true, menuOpen: false), "тишина без меню — бар уходит");
            Assert.IsFalse(LvnTopBar.BarRests(barShown: true, menuOpen: true), "меню открыто — бар стоит");
            Assert.IsFalse(LvnTopBar.BarRests(barShown: false, menuOpen: false), "уже ушёл — уходить некуда");
            Assert.IsFalse(LvnTopBar.BarRests(barShown: false, menuOpen: true), "и с меню тоже");
        }
    }
}
