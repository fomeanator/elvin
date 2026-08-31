using System.Threading.Tasks;
using Lvn.UI.Screens;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ШВЕЙЦАР — порядок входа в экран, один на всех.
    ///
    /// <para>Порядок неочевиден и потому раньше расходился: сначала УВЕСТИ
    /// поверхности за кромку, потом дождаться готовности, и только затем
    /// показать экран и сыграть вход. Сделай наоборот — и игрок увидит готовое
    /// меню, которое дёрнется и поедет заново.</para>
    ///
    /// <para>И предохранитель: чем бы движение ни кончилось, поверхность встаёт
    /// на место. Оборванная анимация (пересборка документа, смена темы посреди
    /// хода) однажды оставила верхнюю полосу за кромкой навсегда — приём был
    /// записан трижды, а страховка стояла не у всех.</para>
    /// </summary>
    public sealed class UsherTests
    {
        private sealed class Поверхность : ILvnEntrance
        {
            public string След = "";
            public void ArmEntrance() => След += "з";      // заряжена
            public void PlayEntrance() => След += "в";     // вход
            public void RestoreEntrance() => След += "м";  // встала на место
        }

        [Test]
        public async Task ЗарядИдётДоПоказаАВходПосле()
        {
            var п = new Поверхность();
            string наМоментПоказа = null;

            await LvnUsher.OpenAsync(hold: null, show: () => наМоментПоказа = п.След, п);

            Assert.AreEqual("з", наМоментПоказа,
                "экран показали раньше, чем поверхность ушла за кромку: игрок увидит "
                + "готовое меню, которое дёрнется и поедет заново");
            Assert.IsTrue(п.След.StartsWith("зв"), "вход сыгран не после показа: " + п.След);
        }

        [Test]
        public async Task ДверьДержитсяПокаНеГотово()
        {
            var п = new Поверхность();
            bool держим = true;
            bool показали = false;

            var ждём = LvnUsher.OpenAsync(hold: () => держим, show: () => показали = true, п);
            await Task.Yield();
            await Task.Yield();

            Assert.IsFalse(показали, "швейцар впустил, не дождавшись готовности");
            Assert.AreEqual("з", п.След, "поверхность обязана ждать ЗА кромкой, а не на месте");

            держим = false;
            await ждём;

            Assert.IsTrue(показали, "готово, а швейцар так и не впустил");
            Assert.IsTrue(п.След.Contains("в"), "вход не сыгран");
        }

        [Test]
        public void ПустойУчастникНеРоняетВход()
        {
            // Витрина-карусель входа не играет: у неё нет церемонии. Список
            // участников с дыркой — норма, а не повод уронить показ.
            Assert.DoesNotThrow(() => LvnUsher.Arm(null, new Поверхность()));
            Assert.DoesNotThrow(() => LvnUsher.Play(null, new Поверхность()));
        }
    }
}
