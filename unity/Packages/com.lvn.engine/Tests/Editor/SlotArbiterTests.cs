using System.Collections.Generic;
using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// Smart-slot arbitration: a visible actor OWNS its spot; a claimant
    /// resolved into an occupied slot slides to the nearest free slot (ties
    /// break outward so crowds spread), an explicit numeric x is authorial
    /// and never touched, hidden actors hold nothing. Born from the partner
    /// screenshot of two characters standing inside each other — branch-merged
    /// chapters lose hides on the way into shared tails (673 flow-order
    /// collisions across the cold chapters), and the stage must never DRAW that.
    /// </summary>
    public class SlotArbiterTests
    {
        private static KeyValuePair<string, Placement> Actor(string id, float x, bool show = true)
            => new KeyValuePair<string, Placement>(id, new Placement { X = x, Show = show });

        [Test]
        public void FreeSlotKeepsTheDesiredX()
        {
            var others = new[] { Actor("matvey", 0.25f) };
            var x = VnStage.ArbitrateSlotX(0.75f, "miron", false, others, null, out var owner);
            Assert.IsNull(owner);
            Assert.AreEqual(0.75f, x, 0.001f);
        }

        [Test]
        public void OccupiedSlotSlidesToTheNearestFreeSlot_TiesBreakOutward()
        {
            // right (0.75) is owned; 0.62 and 0.88 are equally near — outward wins
            var others = new[] { Actor("matvey", 0.75f) };
            var x = VnStage.ArbitrateSlotX(0.75f, "miron", false, others, null, out var owner);
            Assert.AreEqual("matvey", owner);
            Assert.AreEqual(0.88f, x, 0.001f);
        }

        [Test]
        public void ExplicitAuthorXIsNeverArbitrated()
        {
            var others = new[] { Actor("matvey", 0.75f) };
            var x = VnStage.ArbitrateSlotX(0.75f, "miron", true, others, null, out var owner);
            Assert.IsNull(owner, "явный x — авторская композиция, не коллизия");
            Assert.AreEqual(0.75f, x, 0.001f);
        }

        [Test]
        public void HiddenActorsHoldNoSlot()
        {
            var others = new[] { Actor("matvey", 0.75f, show: false) };
            var x = VnStage.ArbitrateSlotX(0.75f, "miron", false, others, null, out var owner);
            Assert.IsNull(owner);
            Assert.AreEqual(0.75f, x, 0.001f);
        }

        [Test]
        public void ReissuingYourselfIsNotACollision()
        {
            var others = new[] { Actor("miron", 0.75f) };
            var x = VnStage.ArbitrateSlotX(0.75f, "miron", false, others, null, out var owner);
            Assert.IsNull(owner);
            Assert.AreEqual(0.75f, x, 0.001f);
        }

        [Test]
        public void CrowdedSceneSlidesJustClearAndClamps()
        {
            var others = new[]
            {
                Actor("a", 0.12f), Actor("b", 0.25f), Actor("c", 0.38f), Actor("d", 0.50f),
                Actor("e", 0.62f), Actor("f", 0.75f), Actor("g", 0.88f),
            };
            var x = VnStage.ArbitrateSlotX(0.88f, "miron", false, others, null, out var owner);
            Assert.IsNotNull(owner);
            Assert.Less(x, 0.88f - VnStage.SlotClaimRadius, "должен съехать с занятой точки");
            Assert.GreaterOrEqual(x, 0.05f);
            Assert.LessOrEqual(x, 0.95f);
        }

        [Test]
        public void EntitySlotOverridesJoinTheCandidatePool()
        {
            // the entity's own tuned slot (e.g. hill wardrobe stand) is free — usable
            var others = new[] { Actor("matvey", 0.75f), Actor("roman", 0.88f), Actor("lena", 0.62f) };
            var custom = new Dictionary<string, float> { ["porch"] = 0.97f };
            var x = VnStage.ArbitrateSlotX(0.75f, "miron", false, others, custom, out var owner);
            Assert.AreEqual("matvey", owner);
            Assert.AreEqual(0.97f, x, 0.001f, "кастомный слот сущности — легальный кандидат");
        }
    }
}
