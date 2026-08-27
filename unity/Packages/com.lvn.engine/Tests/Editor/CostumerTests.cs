using System.Collections.Generic;
using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// Костюмер — «во что герой одет прямо сейчас». Два разных вопроса
    /// (видно на герое / зафиксировано) и правило сборки облика для сцены,
    /// проверяемые без сцены, плеера и каталога.
    public class CostumerTests
    {
        private const string Entity = "test_costumer_hero";

        [SetUp]
        [TearDown]
        public void Cleanup()
        {
            LvnWardrobe.ClearPreview(Entity);
            LvnWardrobe.Clear(Entity);
        }

        private static Dictionary<string, string> Defaults(params string[] pairs)
        {
            var d = new Dictionary<string, string>();
            for (int i = 0; i + 1 < pairs.Length; i += 2) d[pairs[i]] = pairs[i + 1];
            return d;
        }

        // ── что видно на герое ────────────────────────────────────────────────

        [Test]
        public void Chosen_PreviewBeatsEquipped_EquippedBeatsDefault()
        {
            var dflt = Defaults("dress", "plain");

            Assert.AreEqual("plain", LvnCostumer.Chosen(Entity, "dress", dflt),
                "ничего не надето — на герое дефолт набора");

            LvnWardrobe.Equip(Entity, "dress", "gala");
            Assert.AreEqual("gala", LvnCostumer.Chosen(Entity, "dress", dflt));

            LvnWardrobe.Preview(Entity, "dress", "beach");
            Assert.AreEqual("beach", LvnCostumer.Chosen(Entity, "dress", dflt),
                "игрок крутит карусель — сцена показывает примерку, а не надетое");
        }

        // «Снял» — это ОТВЕТ. Съёмное украшение, которое игрок сейчас
        // рассматривает как пункт «Нет», не имеет права добраться надетым:
        // именно так украшение и исчезает с героини живьём.
        [Test]
        public void Chosen_TakingOff_IsAnAnswer_NotAGap()
        {
            LvnWardrobe.Equip(Entity, "decor", "ribbon");
            LvnWardrobe.Preview(Entity, "decor", LvnWardrobe.NoneValue);

            Assert.AreEqual(LvnWardrobe.NoneValue, LvnCostumer.Chosen(Entity, "decor"));
            Assert.IsTrue(LvnCostumer.Bare(LvnCostumer.Chosen(Entity, "decor")));
            Assert.AreEqual("ribbon", LvnCostumer.Committed(Entity, "decor"),
                "снятое в примерке не отменяет надетого — «Выбрать» ещё не нажимали");
        }

        [Test]
        public void Committed_IgnoresPreview_SoTheListDoesNotJump()
        {
            LvnWardrobe.Equip(Entity, "hair", "long");
            LvnWardrobe.Preview(Entity, "hair", "short");

            Assert.AreEqual("long", LvnCostumer.Committed(Entity, "hair"));
            Assert.AreEqual("short", LvnCostumer.Chosen(Entity, "hair"));
        }

        [Test]
        public void EmptyAnswer_WhenThereIsNothingToWear()
        {
            Assert.AreEqual("", LvnCostumer.Chosen(Entity, "dress"));
            Assert.AreEqual("", LvnCostumer.Committed(Entity, "dress"));
            Assert.AreEqual("", LvnCostumer.Chosen(Entity, null));
            Assert.AreEqual("", LvnCostumer.Chosen(null, "dress"));
        }

        [Test]
        public void Wearing_CountsThePreview()
        {
            LvnWardrobe.Equip(Entity, "dress", "gala");
            Assert.IsTrue(LvnCostumer.Wearing(Entity, "dress", "gala"));

            LvnWardrobe.Preview(Entity, "dress", "beach");
            Assert.IsFalse(LvnCostumer.Wearing(Entity, "dress", "gala"),
                "подсвечена та карточка, что на героине сейчас");
            Assert.IsTrue(LvnCostumer.Wearing(Entity, "dress", "beach"));
        }

        // ── облик для сцены ───────────────────────────────────────────────────

        [Test]
        public void Look_LiteralIsStoryForced_TemplateIsVariableDriven()
        {
            LvnWardrobe.Preview(Entity, "armor", "silk");
            LvnWardrobe.Preview(Entity, "outfit", "beach");

            var axes = LvnCostumer.Look(
                Defaults("armor", "chain", "outfit", "{wardrobe}"),
                Entity, _ => "gala");

            Assert.AreEqual("chain", axes["armor"],
                "костюм, вписанный автором буквально, примеркой не сбивается");
            Assert.AreEqual("beach", axes["outfit"],
                "ось, которую ведёт переменная, примерка перебивает живьём");
        }

        [Test]
        public void Look_UnresolvedAxisIsDropped_SoTheLayerIsSkipped()
        {
            var axes = LvnCostumer.Look(
                Defaults("weapon", "{wpn}", "cape", ""),
                Entity, _ => "");   // переменная пуста — надевать нечего

            Assert.IsFalse(axes.ContainsKey("weapon"),
                "без значения слой не рисуется — это «ничего не надето», а не ошибка");
            Assert.IsFalse(axes.ContainsKey("cape"));
        }

        [Test]
        public void Look_WithoutVariables_DropsTemplatesInsteadOfDrawingThem()
        {
            var axes = LvnCostumer.Look(Defaults("outfit", "{wardrobe}"), Entity, null);

            Assert.IsFalse(axes.ContainsKey("outfit"),
                "нераскрытый {токен} не имеет права уехать в имя файла");
        }

        [Test]
        public void Look_WardrobeFillsWhatTheScriptLeftUnset()
        {
            LvnWardrobe.Equip(Entity, "hair", "long");

            var axes = LvnCostumer.Look(Defaults("emotion", "smile"), Entity, null);

            Assert.AreEqual("smile", axes["emotion"]);
            Assert.AreEqual("long", axes["hair"],
                "сценарий про причёску молчит — её знает гардероб");
        }

        [Test]
        public void Look_TakingOffKeepsTheAxisEmpty()
        {
            LvnWardrobe.Equip(Entity, "decor", "ribbon");
            LvnWardrobe.Preview(Entity, "decor", LvnWardrobe.NoneValue);

            var axes = LvnCostumer.Look(new Dictionary<string, string>(), Entity, null);

            Assert.IsFalse(axes.ContainsKey("decor"),
                "надетое не добирает ось, которую игрок сейчас снял");
        }

        [Test]
        public void Look_SurvivesNothingAtAll()
        {
            var axes = LvnCostumer.Look(null, null, null);
            Assert.IsNotNull(axes);
            Assert.AreEqual(0, axes.Count);
        }
    }
}
