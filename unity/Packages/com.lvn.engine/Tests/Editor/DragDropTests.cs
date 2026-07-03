using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// The drag & drop verb's pure parts.
    public class DragDropTests
    {
        [Test]
        public void ParseDropMap_PairsAndJunk()
        {
            var m = VnStage.ParseDropMap("bag:apple_in_bag pond:apple_lost");
            Assert.AreEqual(2, m.Count);
            Assert.AreEqual("apple_in_bag", m["bag"]);
            Assert.AreEqual("apple_lost", m["pond"]);

            Assert.AreEqual(0, VnStage.ParseDropMap(null).Count);
            Assert.AreEqual(0, VnStage.ParseDropMap("   ").Count);
            var junk = VnStage.ParseDropMap("noseparator :nolhs norhs: ok:label");
            Assert.AreEqual(1, junk.Count, "malformed pairs are skipped, valid ones kept");
            Assert.AreEqual("label", junk["ok"]);
        }

        [Test]
        public void ParseDropMap_CommaSeparatedToo()
        {
            var m = VnStage.ParseDropMap("bag:a,box:b");
            Assert.AreEqual("a", m["bag"]);
            Assert.AreEqual("b", m["box"]);
        }
    }
}
