using System.Threading.Tasks;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    // The shared bottom window (VnPanelHost): one dialogue-skinned frame that
    // hosts any content. Transitions run at 0s here — the structural contract
    // (open/swap/close, content parenting) is what these tests pin down.
    public class PanelHostTests
    {
        private static VnPanelHost Host()
            => new VnPanelHost(new VnTheme()) { TransitionSeconds = 0f };

        [Test]
        public async Task Show_OpensAndParentsTheContent()
        {
            var host = Host();
            var content = new Label("hello");
            await host.ShowAsync(content);

            Assert.IsTrue(host.IsOpen);
            Assert.AreSame(content, host.Content);
            Assert.IsTrue(host.style.display == DisplayStyle.Flex);
            Assert.IsNotNull(content.parent, "content lives inside the frame");
        }

        [Test]
        public async Task Show_SwapsContentInsideTheSameFrame()
        {
            var host = Host();
            var a = new Label("a");
            var b = new Label("b");
            await host.ShowAsync(a);
            var frame = a.parent;

            await host.ShowAsync(b);
            Assert.AreSame(b, host.Content);
            Assert.IsNull(a.parent, "old content is released");
            Assert.AreSame(frame, b.parent, "the SAME frame hosts the new content — no reframe");
            Assert.IsTrue(host.IsOpen, "the window never closed during the swap");
        }

        [Test]
        public async Task Show_SameContentIsANoOp()
        {
            var host = Host();
            var a = new Label("a");
            await host.ShowAsync(a);
            await host.ShowAsync(a);
            Assert.AreSame(a, host.Content);
            Assert.IsNotNull(a.parent);
        }

        [Test]
        public async Task Hide_ClosesAndReleases()
        {
            var host = Host();
            var a = new Label("a");
            await host.ShowAsync(a);
            await host.HideAsync();

            Assert.IsFalse(host.IsOpen);
            Assert.IsNull(host.Content);
            Assert.IsNull(a.parent);
            Assert.IsTrue(host.style.display == DisplayStyle.None);
        }

        [Test]
        public void HostedSheet_DrawsNoPanelOfItsOwn()
        {
            var hosted = new Lvn.UI.Screens.WardrobeSheet(null, null, null, null, hosted: true);
            // the frame (position/background) belongs to VnPanelHost — a hosted
            // sheet must not absolute-dock itself like the standalone one does
            Assert.AreNotEqual(Position.Absolute, hosted.style.position.value);
            var standalone = new Lvn.UI.Screens.WardrobeSheet(null, null, null, null);
            Assert.AreEqual(Position.Absolute, standalone.style.position.value);
        }
    }
}
