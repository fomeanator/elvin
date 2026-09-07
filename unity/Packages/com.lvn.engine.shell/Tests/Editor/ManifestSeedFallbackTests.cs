using Lvn.UI.Screens;
using NUnit.Framework;

namespace Lvn.Tests
{
    public sealed class ManifestSeedFallbackTests
    {
        [TestCase(false, false, false, TestName = "PendingFetchMustFinishBeforeSeed")]
        [TestCase(false, true, false, TestName = "ServerManifestWinsOverSeed")]
        [TestCase(true, true, false, TestName = "CachedManifestWinsAfterFetchFailure")]
        [TestCase(true, false, true, TestName = "FailedFetchWithoutCacheUsesSeed")]
        public void SeedIsTheLastResort(bool fetchFailed, bool hasManifest, bool expected)
        {
            Assert.AreEqual(expected, ManifestSeedFallback.ShouldTry(fetchFailed, hasManifest));
        }
    }
}
