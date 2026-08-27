using System.Collections.Generic;
using Lvn.Content;
using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// Дома настроек: движковый дефолт работает без манифеста, манифест
    /// перекрывает его точечно, а общее правило существует в одном экземпляре.
    /// Тесты держат именно это — не конкретные числа, а контракт.
    public class StagingHomesTests
    {
        [TearDown]
        public void Restore()
        {
            LvnMotion.Tempo = 1f;
            LvnMenuStage.Apply(0.91f, 1f, 0.35f, 0.14f);
            LvnWardrobeStage.Apply(null);
        }

        [Test]
        public void Tempo_ScalesEveryDuration_AndClampsToSanity()
        {
            LvnMotion.Tempo = 1f;
            Assert.AreEqual(LvnMotion.Normal, LvnMotion.Ms(LvnMotion.Normal), "темп 1 ничего не меняет");

            LvnMotion.Tempo = 2f;
            Assert.AreEqual(LvnMotion.Normal * 2, LvnMotion.Ms(LvnMotion.Normal), "вдвое вальяжнее");
            Assert.AreEqual(0.5f, LvnMotion.Sec(0.25f), 1e-4f, "секунды идут тем же множителем");

            LvnMotion.Tempo = 99f;
            Assert.LessOrEqual(LvnMotion.Tempo, 4f, "нелепый темп обрезается, а не ломает экран");
            LvnMotion.Tempo = 0f;
            Assert.Greater(LvnMotion.Tempo, 0f, "ноль превратил бы движение в деление на ноль");
        }

        [Test]
        public void StageMotion_CombinesSceneTempoAndGlobalTempo()
        {
            float scale = VnTheme.MotionDurationScale;
            LvnMotion.Tempo = 1f;
            Assert.AreEqual(0.4f * scale, VnTheme.Motion(0.4f), 1e-4f, "темп сцены применён");

            LvnMotion.Tempo = 0.5f;
            Assert.AreEqual(0.4f * scale * 0.5f, VnTheme.Motion(0.4f), 1e-4f,
                "общая ручка действует поверх сценической");
            Assert.AreEqual(VnTheme.MotionMs(0.4f), (int)System.Math.Round(VnTheme.Motion(0.4f) * 1000f),
                "миллисекунды — та же величина");
        }

        [Test]
        public void MenuPan_WalksTheCanvasInOrder_AndStaysOnIt()
        {
            LvnMenuStage.Apply(null, null, 0.35f, 0.14f);
            Assert.AreEqual(0.35f, LvnMenuStage.PanFor(0), 1e-4f, "первая вкладка — стартовая точка");
            Assert.AreEqual(0.49f, LvnMenuStage.PanFor(1), 1e-4f, "шаг за вкладку");
            Assert.Greater(LvnMenuStage.PanFor(3), LvnMenuStage.PanFor(2), "камера едет в одну сторону");
            Assert.AreEqual(LvnMenuStage.PanFor(3), LvnMenuStage.PanFor(9), 1e-4f,
                "за последнюю вкладку полотно не уезжает");
            Assert.LessOrEqual(LvnMenuStage.PanFor(3), 1f, "и не съезжает с картины");
        }

        [Test]
        public void MenuStaging_ManifestOverridesOnlyWhatItNames()
        {
            LvnMenuStage.Apply(0.91f, 1f, 0.35f, 0.14f);
            LvnMenuStage.Apply(0.8f, null, null, null);
            Assert.AreEqual(0.8f, LvnMenuStage.DollHeight, 1e-4f, "названное — перекрыто");
            Assert.AreEqual(1f, LvnMenuStage.DollWidth, 1e-4f, "остальное осталось движковым");
            Assert.AreEqual(0.35f, LvnMenuStage.PanStart, 1e-4f);
        }

        [Test]
        public void WardrobeAxis_IsRecognisedByMeaning_InOnePlace()
        {
            Assert.AreEqual(LvnWardrobeAxisKind.Hair, LvnWardrobeStage.KindOf("hairstyle"));
            Assert.AreEqual(LvnWardrobeAxisKind.Hair, LvnWardrobeStage.KindOf("Причёска"));
            Assert.AreEqual(LvnWardrobeAxisKind.Decor, LvnWardrobeStage.KindOf("decor"));
            Assert.AreEqual(LvnWardrobeAxisKind.Decor, LvnWardrobeStage.KindOf("украшения"));
            Assert.AreEqual(LvnWardrobeAxisKind.Outfit, LvnWardrobeStage.KindOf("armor"),
                "незнакомая ось — вещь на корпусе: самый безобидный кадр");
            Assert.IsTrue(LvnWardrobeStage.IsHair("hair_color"));
            Assert.AreEqual(LvnIcon.Crown, LvnWardrobeStage.IconFor("hairstyle"));
            Assert.AreEqual(LvnIcon.Wardrobe, LvnWardrobeStage.IconFor("outfit"));
        }

        [Test]
        public void WardrobeFraming_HasSaneDefaults_AndBendsToTheManifest()
        {
            LvnWardrobeStage.Apply(null);
            var hair = LvnWardrobeStage.Framing("hairstyle");
            var outfit = LvnWardrobeStage.Framing("outfit");
            var all = LvnWardrobeStage.Framing(LvnWardrobeStage.AllAxis);
            Assert.Greater(hair.zoom, all.zoom, "причёску показываем крупнее общего плана");
            Assert.Less(hair.anchorY, outfit.anchorY, "голова выше корпуса");
            Assert.AreEqual(1f, LvnWardrobeStage.Framing("decor").zoom, 1e-4f,
                "украшения приходят кроп-иконками — приближать нечего");

            LvnWardrobeStage.Apply(new WardrobeConfig
            {
                framing = new Dictionary<string, FramingConfig>
                {
                    ["hairstyle"] = new FramingConfig { zoom = 2.2f },
                },
            });
            var tuned = LvnWardrobeStage.Framing("hairstyle");
            Assert.AreEqual(2.2f, tuned.zoom, 1e-4f, "новелла подвела кадр под свой арт");
            Assert.AreEqual(hair.anchorY, tuned.anchorY, 1e-4f,
                "не названное манифестом осталось движковым");
            Assert.AreEqual(outfit.zoom, LvnWardrobeStage.Framing("outfit").zoom, 1e-4f,
                "и соседняя ось не тронута");
        }
    }
}
