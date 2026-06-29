using Lvn.UI;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// The dialogue box is fully theme-driven — no magic numbers. These checks
    /// build it from a customised VnTheme and assert the resulting VisualElement
    /// styles track the theme, so a manifest-built theme actually restyles the UI.
    public class DialogueBoxThemeTests
    {
        private static float Px(StyleLength len) => len.value.value;

        [Test]
        public void Padding_FontSize_And_MinHeight_FollowTheme()
        {
            var theme = new VnTheme
            {
                EdgePadding = 40f,
                BottomPadding = 50f,
                PanelPaddingX = 30f,
                PanelPaddingY = 26f,
                PanelMinHeight = 200f,
                NamePaddingX = 18f,
                BodyFontSize = 42,
                SpeakerFontSize = 30,
            };
            var db = new DialogueBox(theme);

            Assert.AreEqual(40f, Px(db.style.paddingLeft), 0.01f, "edge padding");
            Assert.AreEqual(50f, Px(db.style.paddingBottom), 0.01f, "bottom padding");

            var panel = db.Q<VisualElement>("vn-panel");
            Assert.AreEqual(30f, Px(panel.style.paddingLeft), 0.01f, "panel padding x");
            Assert.AreEqual(26f, Px(panel.style.paddingTop), 0.01f, "panel padding y");
            Assert.AreEqual(200f, Px(panel.style.minHeight), 0.01f, "panel min height");

            var body = db.Q<Label>("vn-body");
            Assert.AreEqual(42f, Px(body.style.fontSize), 0.01f, "body font size");

            var plate = db.Q<VisualElement>("vn-plate");
            Assert.AreEqual(18f, Px(plate.style.paddingLeft), 0.01f, "name padding x");

            var speaker = db.Q<Label>("vn-speaker");
            Assert.AreEqual(30f, Px(speaker.style.fontSize), 0.01f, "speaker font size");
        }

        [Test]
        public void Defaults_MatchReferenceLook()
        {
            var db = new DialogueBox(new VnTheme());
            Assert.AreEqual(24f, Px(db.style.paddingLeft), 0.01f);
            var panel = db.Q<VisualElement>("vn-panel");
            Assert.AreEqual(128f, Px(panel.style.minHeight), 0.01f);
            // ADV (default): no top anchor, panel doesn't grow.
            Assert.AreEqual(StyleKeyword.Null, db.style.top.keyword, "ADV leaves top unset");
        }

        [Test]
        public void Nvl_StretchesPanelFromTopInset()
        {
            var db = new DialogueBox(new VnTheme { Nvl = true, NvlTop = 0.15f });
            Assert.AreEqual(15f, db.style.top.value.value, 0.01f, "top inset as percent");
            Assert.AreEqual(LengthUnit.Percent, db.style.top.value.unit);
            var panel = db.Q<VisualElement>("vn-panel");
            Assert.AreEqual(1f, panel.style.flexGrow.value, 0.01f, "NVL panel fills the region");
        }
    }
}
