using Lvn.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests.Editor
{
    /// <summary>
    /// ПРИМА — постоянная фигура сцены.
    ///
    /// <para>Проверяется здесь одно: настройки превращаются в команду В ОДНОМ
    /// МЕСТЕ. Пока эти же поля собирали четверо — витрина, две катсцены и
    /// гардероб, — расхождение в одном поле у одного из них давало на экране
    /// другого человека: «героинь опять две», «встаёт в главе по-менюшному»,
    /// «рост скачет».</para>
    /// </summary>
    public class PrimaTests
    {
        private static JObject Pose(int z = 0)
            => LvnPrima.Pose("hero", "center", 1f, 0.91f, z);

        [Test]
        public void TheSettingsBecomeAShowCommand()
        {
            var p = Pose();
            Assert.AreEqual("actor", (string)p["op"]);
            Assert.AreEqual("hero", (string)p["id"]);
            // Проверяем СМЫСЛ, а не тип: «показана» — вопрос к словарю (Lvn.LvnBool),
            // и приведение сломалось бы молча, начни производитель писать слово.
            Assert.IsTrue(Lvn.LvnBool.Of(p["show"], false));
            Assert.AreEqual("center", (string)p["position"]);
            Assert.AreEqual(0.91f, (float)p["height"], 0.0001f);
        }

        // Явный z живёт у сцены до следующего явного значения: «сотка» катсцены
        // тащилась за куклой в меню и в следующую главу — там она стояла бы
        // поверх любого собеседника.
        [Test]
        public void TheLayerOrderIsAlwaysStated()
        {
            Assert.IsNotNull(Pose()["z"], "порядок слоя не задан — прежний останется жить");
            Assert.AreEqual(0, (int)Pose()["z"]);
            Assert.AreEqual(100, (int)Pose(100)["z"], "катсцена не смогла поставить её перед всеми");
        }

        // У фигуры якорь ног: число по вертикали уводило её за нижнюю кромку
        // кадра (живой дефект y=0.02).
        [Test]
        public void TheFigureIsNeverPinnedByItsFeetToANumber()
        {
            Assert.IsNull(Pose()["y"], "вертикаль задана числом — фигура уедет за кадр");
        }

        // Пустое место — не повод собрать команду без позиции: «center» и есть
        // положение по умолчанию витрины.
        [Test]
        public void AnEmptyPlaceStillMeansTheCentre()
        {
            Assert.AreEqual("center", (string)LvnPrima.Pose("hero", null, 1f, 0.91f, 0)["position"]);
        }

        // Рост — настройка витрины (ui.browse.doll_height), а не число на месте
        // вызова. Разные числа в разных местах и были «рост скачет».
        [Test]
        public void TheHeightComesFromTheShowcaseSettings()
        {
            var before = LvnMenuStage.DollHeight;
            try
            {
                LvnMenuStage.Apply(0.77f, null, null, null);
                var p = LvnPrima.Pose("hero", "center", LvnMenuStage.DollWidth, LvnMenuStage.DollHeight, 0);
                Assert.AreEqual(0.77f, (float)p["height"], 0.0001f);
            }
            finally { LvnMenuStage.DollHeight = before; }
        }
    }
}
