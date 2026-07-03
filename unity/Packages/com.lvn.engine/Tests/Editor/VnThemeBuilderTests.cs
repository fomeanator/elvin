using Lvn.Content;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// The manifest → in-game theme mapping (VnThemeBuilder): every config field
    /// is optional and falls back to the engine's VnTheme default, so a host can
    /// theme the dialogue box and choices piece by piece from the manifest.
    public class VnThemeBuilderTests
    {
        [Test]
        public void Null_ReturnsPlainDefaults()
        {
            var t = VnThemeBuilder.From(null);
            var d = new VnTheme();
            Assert.AreEqual(d.BodyFontSize, t.BodyFontSize);
            Assert.AreEqual(d.ChoiceCornerRadius, t.ChoiceCornerRadius);
            Assert.AreEqual(d.CharsPerSecond, t.CharsPerSecond);
        }

        [Test]
        public void EmptySections_KeepDefaults()
        {
            var ui = new LvnUiConfig { dialogue = new DialogueConfig(), choices = new ChoicesConfig() };
            var t = VnThemeBuilder.From(ui);
            var d = new VnTheme();
            Assert.AreEqual(d.BodyFontSize, t.BodyFontSize);
            Assert.AreEqual(d.PanelColor, t.PanelColor, "absent colour keeps the default");
            Assert.AreEqual(d.ChoiceSpacing, t.ChoiceSpacing);
        }

        [Test]
        public void Dialogue_AppliesColorsAndSizes()
        {
            var ui = new LvnUiConfig
            {
                dialogue = new DialogueConfig
                {
                    panel_color = "#ff0000",
                    text_color = "#00ff00",
                    body_size = 50,
                    corner_radius = 20,
                    edge_padding = 40,
                    chars_per_second = 80,
                }
            };
            var t = VnThemeBuilder.From(ui);

            Assert.AreEqual(1f, t.PanelColor.r, 0.01f);
            Assert.AreEqual(0f, t.PanelColor.g, 0.01f);
            Assert.AreEqual(1f, t.TextColor.g, 0.01f);
            Assert.AreEqual(50, t.BodyFontSize);
            Assert.AreEqual(20f, t.PanelCornerRadius, 0.01f);
            Assert.AreEqual(40f, t.EdgePadding, 0.01f);
            Assert.AreEqual(80f, t.CharsPerSecond, 0.01f);
        }

        [Test]
        public void Choices_AppliesWidthSpacingAndColor()
        {
            var ui = new LvnUiConfig
            {
                choices = new ChoicesConfig
                {
                    color = "#101820",
                    font_size = 30,
                    min_width_percent = 50,
                    max_width_percent = 90,
                    spacing = 16,
                    corner_radius = 6,
                }
            };
            var t = VnThemeBuilder.From(ui);

            Assert.AreEqual(30, t.ChoiceFontSize);
            Assert.AreEqual(50f, t.ChoiceMinWidthPercent, 0.01f);
            Assert.AreEqual(90f, t.ChoiceMaxWidthPercent, 0.01f);
            Assert.AreEqual(16f, t.ChoiceSpacing, 0.01f);
            Assert.AreEqual(6f, t.ChoiceCornerRadius, 0.01f);
            // colour parsed (not the default)
            Assert.AreNotEqual(new VnTheme().ChoiceColor, t.ChoiceColor);
        }

        [Test]
        public void Dialogue_AppliesFontAndNvl()
        {
            var ui = new LvnUiConfig
            {
                dialogue = new DialogueConfig { font = "Fonts/Serif", nvl = true, nvl_top = 0.2f }
            };
            var t = VnThemeBuilder.From(ui);
            Assert.AreEqual("Fonts/Serif", t.FontResourcePath);
            Assert.IsTrue(t.Nvl);
            Assert.AreEqual(0.2f, t.NvlTop, 0.001f);
        }

        [Test]
        public void Nvl_DefaultsOffWhenAbsent()
        {
            var t = VnThemeBuilder.From(new LvnUiConfig { dialogue = new DialogueConfig() });
            Assert.IsFalse(t.Nvl);
            Assert.AreEqual("", t.FontResourcePath);
        }

        [Test]
        public void GarbageColor_FallsBackToBaselineField()
        {
            var ui = new LvnUiConfig { dialogue = new DialogueConfig { speaker_color = "not-a-color" } };
            var t = VnThemeBuilder.From(ui);
            Assert.AreEqual(new VnTheme().SpeakerColor, t.SpeakerColor);
        }

        [Test]
        public void BackgroundImages_MapUrlsAndSlices()
        {
            var ui = new LvnUiConfig
            {
                dialogue = new DialogueConfig
                {
                    panel_image = "/content/ui/panel.png",
                    name_image = "/content/ui/name.png",
                    panel_slice = 24,
                },
                choices = new ChoicesConfig
                {
                    button_image = "/content/ui/btn.png",
                    button_hover_image = "/content/ui/btn_hover.png",
                    button_slice = 16,
                }
            };
            var t = VnThemeBuilder.From(ui);

            Assert.AreEqual("/content/ui/panel.png", t.PanelImageUrl);
            Assert.AreEqual("/content/ui/name.png", t.PlateImageUrl);
            Assert.AreEqual(24, t.PanelSlice);
            Assert.AreEqual("/content/ui/btn.png", t.ChoiceImageUrl);
            Assert.AreEqual("/content/ui/btn_hover.png", t.ChoiceHoverImageUrl);
            Assert.AreEqual(16, t.ChoiceSlice);
        }

        [Test]
        public void BackgroundImages_AbsentLeaveThemeUnskinned()
        {
            var t = VnThemeBuilder.From(new LvnUiConfig { dialogue = new DialogueConfig(), choices = new ChoicesConfig() });
            Assert.IsNull(t.PanelImageUrl);
            Assert.IsNull(t.ChoiceImageUrl);
            Assert.AreEqual(0, t.PanelSlice);
            Assert.AreEqual(0, t.ChoiceSlice);
        }

        [Test]
        public void Sounds_MapUrlsAndClampVolume()
        {
            var ui = new LvnUiConfig
            {
                sounds = new SoundsConfig
                {
                    click = "/content/ui/click.wav",
                    choice = "/content/ui/choice.wav",
                    type = "/content/ui/type.wav",
                    volume = 1.7f,
                }
            };
            var t = VnThemeBuilder.From(ui);
            Assert.AreEqual("/content/ui/click.wav", t.ClickSoundUrl);
            Assert.AreEqual("/content/ui/choice.wav", t.ChoiceSoundUrl);
            Assert.AreEqual("/content/ui/type.wav", t.TypeSoundUrl);
            Assert.AreEqual(1f, t.UiSoundVolume, 0.001f, "volume clamps to 0..1");
        }

        [Test]
        public void Sounds_AbsentStaySilentAtFullVolume()
        {
            var t = VnThemeBuilder.From(new LvnUiConfig { sounds = new SoundsConfig() });
            Assert.IsNull(t.ClickSoundUrl);
            Assert.IsNull(t.ChoiceSoundUrl);
            Assert.IsNull(t.TypeSoundUrl);
            Assert.AreEqual(1f, t.UiSoundVolume, 0.001f);
        }

        [Test]
        public void Baseline_IsOverriddenNotReplaced()
        {
            // start from a custom baseline; only present fields change.
            var baseline = new VnTheme { BodyFontSize = 99, ChoiceFontSize = 77 };
            var ui = new LvnUiConfig { dialogue = new DialogueConfig { body_size = 40 } };
            var t = VnThemeBuilder.From(ui, baseline);
            Assert.AreEqual(40, t.BodyFontSize, "present field overrides baseline");
            Assert.AreEqual(77, t.ChoiceFontSize, "absent field keeps baseline");
        }
    }
}
