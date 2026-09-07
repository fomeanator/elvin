using NUnit.Framework;
using Lvn.Content;

namespace Lvn.Tests
{
    /// <summary>
    /// ПОЛОСА РАСПАКОВКИ ОБЯЗАНА СТРОИТЬСЯ НА ЛЮБОМ УСТРОЙСТВЕ.
    ///
    /// <para>Ширина полосы стала зависеть от числа ядер, а бронь для живого
    /// осталась записанной единицей. На двухъядерном устройстве ширина равна
    /// одному месту — и бронь съедала его целиком. Конструктор полосы такое
    /// запрещает намеренно (полоса, целиком отданная живому, останавливает фон
    /// навсегда), поэтому он бросал исключение — из СТАТИЧЕСКОГО конструктора
    /// <see cref="LvnLanes"/>. А через <see cref="LvnLanes.Wire"/> ходит вся
    /// сеть: после первого падения любое обращение к ней падало
    /// TypeInitializationException до конца жизни приложения.</para>
    ///
    /// <para>Замер 07.09 на устройстве с двумя ядрами: манифест не приехал ни
    /// разу, игрок бесконечно видел «нет связи с сервером» — про сеть, которая
    /// была исправна. Проверяется здесь не число мест (его можно менять), а
    /// то, что пара «ширина и бронь» ВСЕГДА принимается конструктором.</para>
    /// </summary>
    public class DecoderLaneTests
    {
        // Ноль и отрицательное — «ядер спросить не удалось»: не главный поток.
        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(8)]
        [TestCase(16)]
        public void ПолосаСтроитсяПриЛюбомЧислеЯдер(int cores)
        {
            var lane = LvnLanes.DecoderLane(cores);
            Assert.DoesNotThrow(
                () => new LvnLane("проба", lane.Width, lane.KeptForLive),
                $"при {cores} ядрах полоса {lane.Width}/{lane.KeptForLive} не строится — "
                + "статический конструктор LvnLanes упадёт и унесёт с собой всю сеть");
        }

        [Test]
        public void УЕдинственногоМестаБрониНет()
        {
            var lane = LvnLanes.DecoderLane(2);
            Assert.AreEqual(1, lane.Width, "двухъядерное устройство оставляет ядро кадру");
            Assert.AreEqual(0, lane.KeptForLive,
                "единственное место делят живое и фоновое: бронь на нём останавливает фон навсегда");
        }

        [Test]
        public void ГдеМестБольшеОдного_ЖивомуБронируетсяМесто()
        {
            foreach (var cores in new[] { 0, 4, 8 })
            {
                var lane = LvnLanes.DecoderLane(cores);
                Assert.Greater(lane.Width, 1, $"при {cores} ядрах ширина должна быть больше одного");
                Assert.AreEqual(1, lane.KeptForLive,
                    $"при {cores} ядрах живому положено место: иначе пустая иконка ждёт фоновых");
            }
        }

        /// <summary>Сам дом полос обязан подниматься — это и есть тот
        /// статический конструктор, который падал.</summary>
        [Test]
        public void ДомПолосПоднимается()
        {
            Assert.DoesNotThrow(() =>
            {
                Assert.NotNull(LvnLanes.Wire);
                Assert.NotNull(LvnLanes.Decoder);
            }, "LvnLanes не инициализируется — вся сеть приложения мертва");
        }
    }
}
