using NUnit.Framework;
using Lvn.Services;

namespace Lvn.Tests
{
    /// <summary>
    /// СКОЛЬКО У ИГРОКА ВАЛЮТЫ — <see cref="LvnWallet.Balance"/>.
    ///
    /// <para>Ценность вопроса не в поиске по карте, а в ОТВЕТЕ на «валюты нет
    /// в карте»: ноль — это решение, и его можно было принять иначе. Написанное
    /// хвостом <c>? v : 0</c> по месту, оно повторялось у витрины гардероба и у
    /// функции сценария, и третий автор мог ответить по-другому.</para>
    /// </summary>
    public class WalletBalanceTests
    {
        [Test]
        public void Незнакомая_валюта_это_ноль_а_не_ошибка()
        {
            Assert.AreEqual(0L, LvnWallet.Balance("совершенно-неизвестная-валюта"));
        }

        [Test]
        public void Пустое_имя_валюты_это_тоже_ноль()
        {
            Assert.AreEqual(0L, LvnWallet.Balance(null));
            Assert.AreEqual(0L, LvnWallet.Balance(""));
        }

        [Test]
        public void Ответ_совпадает_с_картой_балансов()
        {
            foreach (var kv in LvnWallet.Balances)
                Assert.AreEqual(kv.Value, LvnWallet.Balance(kv.Key),
                    "дом обязан отвечать то же, что лежит в карте: иначе два способа спросить дают разное");
        }
    }
}
