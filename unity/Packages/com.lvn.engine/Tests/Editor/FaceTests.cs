using System.Collections.Generic;
using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ВЫРАЖЕНИЕ ЛИЦА ВИТРИНЫ: реакция ложится поверх облика, но уступает
    /// игроку. Правило старшинства — единственное, где такая система ломается
    /// молча: 11.09.2026 фишки эмоций «переставали работать» после первой
    /// реакции, а комната надевала своё лицо поверх выбранного.
    /// </summary>
    public class FaceTests
    {
        private const string Entity = "test_face_hero";
        private const string Axis = "emotion";

        [SetUp]
        [TearDown]
        public void Cleanup()
        {
            LvnFace.Clear();
            LvnFace.Choose(Entity, Axis, null);
            LvnWardrobe.ClearPreview(Entity);
            LvnWardrobe.Clear(Entity);
        }

        private static Dictionary<string, string> Axes(string emotion = null)
        {
            var a = new Dictionary<string, string> { ["outfit"] = "plain" };
            if (emotion != null) a[Axis] = emotion;
            return a;
        }

        /// <summary>Нет выбора игрока — реакция надевает своё лицо, даже
        /// поверх сценарного значения.</summary>
        [Test]
        public void ReactionDressesTheFaceWhenPlayerHasNoChoice()
        {
            LvnFace.Hold(Entity, Axis, "delight");
            var axes = Axes("idle");
            LvnFace.ApplyTo(axes, Entity);
            Assert.AreEqual("delight", axes[Axis]);
            Assert.AreEqual("plain", axes["outfit"], "реакция трогает только лицо");
        }

        /// <summary>Игрок примерил эмоцию фишкой — реакция ему не указ.</summary>
        [Test]
        public void PlayersPreviewBeatsReaction()
        {
            LvnWardrobe.Preview(Entity, Axis, "angry");
            LvnFace.Hold(Entity, Axis, "delight");
            var axes = Axes();
            LvnFace.ApplyTo(axes, Entity);
            Assert.IsFalse(axes.ContainsKey(Axis), "наложение отступило: лицо решает костюмер по примерке");
            Assert.IsTrue(LvnFace.PlayerHoldsFace(Entity, Axis));
        }

        /// <summary>Надетая через гардероб эмоция — тоже выбор игрока.</summary>
        [Test]
        public void PlayersEquippedFaceBeatsReaction()
        {
            LvnWardrobe.Equip(Entity, Axis, "smirk");
            LvnFace.Hold(Entity, Axis, "boredom");
            var axes = Axes();
            LvnFace.ApplyTo(axes, Entity);
            Assert.IsFalse(axes.ContainsKey(Axis));
        }

        /// <summary>Выбранное в гардеробе меню лицо стоит на героине в витрине,
        /// запоминается и сильнее реакции (TR-82).</summary>
        [Test]
        public void ChosenFaceDressesTheMenuAndBeatsReaction()
        {
            LvnFace.Choose(Entity, Axis, "smirk");
            Assert.AreEqual("smirk", LvnFace.ChosenEmotion(Entity), "выбор запомнен");
            LvnFace.Hold(Entity, Axis, "delight");
            var axes = Axes("idle");
            LvnFace.ApplyTo(axes, Entity);
            Assert.AreEqual("smirk", axes[Axis]);
            Assert.IsTrue(LvnFace.PlayerHoldsFace(Entity, Axis));
        }

        /// <summary>В ГЛАВЕ лицом командует сценарий: ни выбранное лицо, ни
        /// реакция на актёров не ложатся (14.09 надетая эмоция утекла в главу).</summary>
        [Test]
        public void StoryIsNeverTouchedByChoiceOrReaction()
        {
            LvnFace.Choose(Entity, Axis, "smirk");
            LvnFace.Hold(Entity, Axis, "delight");
            LvnFace.InStory = true;
            var axes = Axes("idle");
            LvnFace.ApplyTo(axes, Entity);
            Assert.AreEqual("idle", axes[Axis], "сценарная эмоция нетронута");
            var bare = Axes();
            LvnFace.ApplyTo(bare, Entity);
            Assert.IsFalse(bare.ContainsKey(Axis), "без эмоции в команде ось остаётся пустой");
        }

        /// <summary>Лицо по умолчанию — забыть выбор: героиня снова живёт реакциями.</summary>
        [Test]
        public void DefaultChoiceForgetsAndReactionsReturn()
        {
            LvnFace.Choose(Entity, Axis, "smirk");
            LvnFace.Choose(Entity, Axis, null);
            Assert.IsNull(LvnFace.ChosenEmotion(Entity));
            LvnFace.Hold(Entity, Axis, "delight");
            var axes = Axes("idle");
            LvnFace.ApplyTo(axes, Entity);
            Assert.AreEqual("delight", axes[Axis]);
        }

        /// <summary>Снятое выражение ничего не оставляет — лицо возвращается к облику.</summary>
        [Test]
        public void ReleaseLeavesNoTrace()
        {
            LvnFace.Hold(Entity, Axis, "happy");
            LvnFace.Release(Entity);
            var axes = Axes("idle");
            LvnFace.ApplyTo(axes, Entity);
            Assert.AreEqual("idle", axes[Axis]);
            Assert.IsNull(LvnFace.Holding(Entity));
        }

        /// <summary>Пустая эмоция — то же, что снять; чужого героя наложение не касается.</summary>
        [Test]
        public void EmptyEmotionReleasesAndOtherEntityIsUntouched()
        {
            LvnFace.Hold(Entity, Axis, "happy");
            LvnFace.Hold(Entity, Axis, "");
            Assert.IsNull(LvnFace.Holding(Entity));

            LvnFace.Hold(Entity, Axis, "happy");
            var other = Axes("idle");
            LvnFace.ApplyTo(other, "someone_else");
            Assert.AreEqual("idle", other[Axis]);
        }
    }
}
