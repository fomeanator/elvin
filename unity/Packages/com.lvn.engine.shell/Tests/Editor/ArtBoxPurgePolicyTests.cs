using Lvn.UI.Screens;
using NUnit.Framework;

namespace Lvn.Tests
{
    public sealed class ArtBoxPurgePolicyTests
    {
        private static readonly string[] QualityVariants = { "@2k", "@1440", "@1k" };

        [TestCase("@1k", "@2k", "@1440")]
        [TestCase("@1440", "@2k", "@1k")]
        [TestCase("@2k", "@1440", "@1k")]
        public void CurrentCleanupDeletesOtherQualitiesAndKeepsItsOwn(
            string current, string otherA, string otherB)
        {
            var decision = ArtBoxPurgePolicy.Decide("/art/image" + current + ".png",
                current, current, QualityVariants);

            CollectionAssert.AreEquivalent(new[]
            {
                "/art/image" + otherA + ".png", "/art/image" + otherB + ".png"
            }, decision.DeleteUrls);
            CollectionAssert.DoesNotContain(decision.DeleteUrls, "/art/image" + current + ".png");
            Assert.AreEqual("/art/image" + current + ".png", decision.ReloadUrl);
        }

        [TestCase("@1k", "@2k")]
        [TestCase("@1k", "@1440")]
        [TestCase("@1440", "@1k")]
        [TestCase("@1440", "@2k")]
        [TestCase("@2k", "@1k")]
        [TestCase("@2k", "@1440")]
        public void ChangedSelectionStopsDeletingAndRedownloading(string cleanup, string selected)
        {
            var url = "/art/image" + cleanup + ".png";
            var before = ArtBoxPurgePolicy.Decide(url, cleanup, cleanup, QualityVariants);
            Assert.IsNotEmpty(before.DeleteUrls);
            Assert.IsNotNull(before.ReloadUrl);

            // Следующий ассет того же обхода уже обязан учитывать новый выбор:
            // иначе уборка удалит выбранную ступень и закажет оставленную.
            var after = ArtBoxPurgePolicy.Decide(url, cleanup, selected, QualityVariants);
            Assert.IsEmpty(after.DeleteUrls);
            Assert.IsNull(after.ReloadUrl);
        }

        [Test]
        public void VariantComputedAfterSelectionChangedCannotReviveOldCleanup()
        {
            // Уменьшитель тоже читает настройку: его адрес может уже относиться
            // к новому выбору, пока сам обход ещё держит прежнюю ступень.
            var decision = ArtBoxPurgePolicy.Decide("/art/image@2k.png", "@1k", "@2k", QualityVariants);

            Assert.IsEmpty(decision.DeleteUrls);
            Assert.IsNull(decision.ReloadUrl);
        }

        [TestCase(null)]
        [TestCase("/art/image.png")]
        [TestCase("/art/image@2k.png")]
        public void AddressWithoutCleanupVariantHasNoWork(string url)
        {
            var decision = ArtBoxPurgePolicy.Decide(url, "@1k", "@1k", QualityVariants);

            Assert.IsEmpty(decision.DeleteUrls);
            Assert.IsNull(decision.ReloadUrl);
        }
    }
}
