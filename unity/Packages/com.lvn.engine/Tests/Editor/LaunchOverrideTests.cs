using Lvn.UI.Screens;
using NUnit.Framework;

namespace Lvn.Tests
{
    // The test-lane server override must be BOTH convenient for automation and
    // inert in release builds. Resolve is the pure seam that guards it: the
    // platform pickup (intent extra / env var) feeds raw strings through here.
    public class LaunchOverrideTests
    {
        [Test]
        public void ReleaseBuildIgnoresOverride()
        {
            Assert.IsNull(LvnLaunchOverrides.Resolve("http://10.0.2.2:8099", isDebugBuild: false));
        }

        [Test]
        public void DevBuildTakesTrimmedUrl()
        {
            Assert.AreEqual("http://10.0.2.2:8099",
                LvnLaunchOverrides.Resolve(" http://10.0.2.2:8099 ", isDebugBuild: true));
        }

        [Test]
        public void EmptyAndWhitespaceMeanNoOverride()
        {
            Assert.IsNull(LvnLaunchOverrides.Resolve(null, isDebugBuild: true));
            Assert.IsNull(LvnLaunchOverrides.Resolve("", isDebugBuild: true));
            Assert.IsNull(LvnLaunchOverrides.Resolve("   ", isDebugBuild: true));
        }

        [Test]
        public void TrailingSlashDropsForCleanPathJoins()
        {
            Assert.AreEqual("http://127.0.0.1:8099",
                LvnLaunchOverrides.Resolve("http://127.0.0.1:8099/", isDebugBuild: true));
        }
    }
}
