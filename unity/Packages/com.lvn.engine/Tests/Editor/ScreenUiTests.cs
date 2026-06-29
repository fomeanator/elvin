using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    // Guards the shared shell-screen UI primitives that used to live as private
    // copies in five or six screens — so the consolidation can't silently drift.
    public class ScreenUiTests
    {
        [Test]
        public void Stretch_PinsElementToAllEdges()
        {
            var el = ScreenUi.Stretch(new VisualElement());
            Assert.AreEqual(Position.Absolute, el.style.position.value);
            Assert.AreEqual(0f, el.style.left.value.value);
            Assert.AreEqual(0f, el.style.right.value.value);
            Assert.AreEqual(0f, el.style.top.value.value);
            Assert.AreEqual(0f, el.style.bottom.value.value);
        }

        [Test]
        public void ProgressBar_BuildsTrackUnderZeroWidthFill()
        {
            var bar = ScreenUi.ProgressBar(0.5f, 0.8f, 0.6f, 0.02f, Color.gray, Color.white,
                out var track, out var fill);
            Assert.AreEqual(2, bar.childCount, "bar holds track + fill");
            Assert.AreSame(track, bar[0], "track sits behind");
            Assert.AreSame(fill, bar[1], "fill sits in front");
            Assert.AreEqual(Position.Absolute, bar.style.position.value);
            Assert.AreEqual(0f, fill.style.width.value.value, "fill starts empty");
            Assert.AreEqual(LengthUnit.Percent, fill.style.width.value.unit);
        }

        [Test]
        public void CenterLabel_IsCentredAndIgnoresInput()
        {
            var l = ScreenUi.CenterLabel(0.5f, Color.white, 20f);
            Assert.AreEqual(TextAnchor.MiddleCenter, l.style.unityTextAlign.value);
            Assert.AreEqual(PickingMode.Ignore, l.pickingMode);
            Assert.AreEqual(20f, l.style.fontSize.value.value);
        }

        [Test]
        public void SetText_IsNullSafe()
        {
            Assert.DoesNotThrow(() => ScreenUi.SetText(null, "x"));
            var l = new Label();
            ScreenUi.SetText(l, "hi");
            Assert.AreEqual("hi", l.text);
        }
    }
}
