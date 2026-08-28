using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// НАСТРОЙКИ В СЦЕНЕ — часть <see cref="StageMenu"/>: громкости, скорость
    /// текста, язык и комфорт-переключатели, не выходя из главы.
    /// </summary>
    public sealed partial class StageMenu
    {
        private void ShowSettings()
        {
            var p = Panel(L("settings", "Settings"));
            var scroll = new ScrollView();
            scroll.style.flexGrow = 1;
            p.Add(scroll);

            // ЧИТАЮТ ЗДЕСЬ. Набор настроек чтения жил только в оболочке, и
            // игрок, которому мелко ПРЯМО СЕЙЧАС, обязан был выйти из главы,
            // чтобы это поправить. Состав сведён; вид у каждого экрана свой.
            scroll.Add(SegmentRow(L("font", "Font"), FontSegment()));
            scroll.Add(SegmentRow(L("text_size", "Text size"), ScaleSegment(
                () => LvnPrefs.TextScale, v => LvnPrefs.TextScale = v)));
            scroll.Add(SegmentRow(L("ui_size", "Interface size"), ScaleSegment(
                () => LvnPrefs.UiScale, v => { LvnPrefs.UiScale = v; LvnPanel.ApplyUiScale(); })));
            scroll.Add(SliderRow(L("text_speed", "Text speed"), 0.25f, 3f, LvnPrefs.TextSpeed, v => LvnPrefs.TextSpeed = v));
            scroll.Add(ToggleRow(L("auto_advance", "Auto-advance"), LvnPrefs.AutoAdvance, v => LvnPrefs.AutoAdvance = v));
            scroll.Add(SliderRow(L("auto_delay", "Auto delay"), 0.5f, 2.5f, LvnPrefs.AutoDelayScale, v => LvnPrefs.AutoDelayScale = v));
            if (_theme.SimpleAudioSliders)
            {
                // «Музыка» и «Звук» — двухползунковый режим (ui.settings.
                // simple_audio): звук ведёт эффекты+эмбиент+голос одним движком.
                scroll.Add(SliderRow(L("music", "Music"), 0f, 1f, LvnPrefs.VolMusic, v => LvnPrefs.VolMusic = v));
                scroll.Add(SliderRow(L("sound", "Sound"), 0f, 1f, LvnPrefs.VolSfx,
                    v => { LvnPrefs.VolSfx = v; LvnPrefs.VolAmbient = v; LvnPrefs.VolVoice = v; }));
            }
            else
            {
                scroll.Add(SliderRow(L("music", "Music"), 0f, 1f, LvnPrefs.VolMusic, v => LvnPrefs.VolMusic = v));
                scroll.Add(SliderRow(L("ambient", "Ambient"), 0f, 1f, LvnPrefs.VolAmbient, v => LvnPrefs.VolAmbient = v));
                scroll.Add(SliderRow(L("sfx", "Sound FX"), 0f, 1f, LvnPrefs.VolSfx, v => LvnPrefs.VolSfx = v));
                scroll.Add(SliderRow(L("voice", "Voice"), 0f, 1f, LvnPrefs.VolVoice, v => LvnPrefs.VolVoice = v));
            }
            scroll.Add(SliderRow(L("window_opacity", "Window opacity"), 0.2f, 1f, LvnPrefs.DialogOpacity, v => LvnPrefs.DialogOpacity = v));
            scroll.Add(ToggleRow(L("skip_read_only", "Skip read text only"), LvnPrefs.SkipReadOnly, v => LvnPrefs.SkipReadOnly = v));
            scroll.Add(ToggleRow(L("reduce_motion", "Reduce motion"), LvnPrefs.ReduceMotion, v => LvnPrefs.ReduceMotion = v));

            // Language — only when the content ships catalogs (manifest.languages).
            // Tapping cycles Original → each language → Original.
            if (LvnPrefs.AvailableLocales.Count > 0)
                scroll.Add(LanguageRow());
        }

        private VisualElement LanguageRow()
        {
            var card = SettingCard();
            card.style.flexDirection = FlexDirection.Row;
            card.style.justifyContent = Justify.SpaceBetween;
            card.style.alignItems = Align.Center;
            card.Add(Text(L("language", "Language"), 24, FontStyle.Normal));

            string Caption(string code) => LvnPrefs.LocaleTitle(code);

            var btn = new Button { text = Caption(LvnPrefs.Locale) };
            btn.style.minWidth = 150;
            btn.style.height = 48;
            btn.style.fontSize = 22;
            btn.style.paddingLeft = 18; btn.style.paddingRight = 18;
            btn.style.color = LvnTokens.OnAccent;
            btn.style.backgroundColor = LvnTokens.Accent;
            LvnChrome.ClearBorder(btn);
            LvnChrome.Round(btn, 14f);
            if (_theme.Font != null) btn.style.unityFont = new StyleFont(_theme.Font);
            btn.clicked += () =>
            {
                LvnPrefs.Locale = LvnPrefs.NextLocale(LvnPrefs.Locale, LvnPrefs.AvailableLocales);
                btn.text = Caption(LvnPrefs.Locale);
            };
            card.Add(btn);
            return card;
        }

        // Карточка-строка настроек: фон-плашка вместо голого лейбла на чёрном.
        // Тон — от текста темы, акцент — из LvnTokens (та же Полночь, что и в
        // оболочке): внутриигровые настройки выглядели «сырыми системными» на
        // фоне отполированных экранов витрины (живой репорт со скрином).
        // Ряд вариантов в сценном меню: подпись сверху, выбор под ней — на
        // ширине панели главы вариантам иначе не хватает места.
        private VisualElement SegmentRow(string label, VisualElement seg)
        {
            var card = SettingCard();
            card.Add(Text(label, 24, FontStyle.Normal));
            seg.style.marginTop = 8;
            card.Add(seg);
            return card;
        }

        // Кнопка варианта в стиле сценного меню: плашка из темы новеллы, а не
        // из токенов оболочки — глава рисуется её палитрой.
        private void StyleOption(Button b, bool active)
        {
            var tint = _theme.MenuTextColor;
            b.style.fontSize = 22;
            b.style.whiteSpace = WhiteSpace.Normal;   // крупный кегль и широкая гарнитура переносятся
            b.style.paddingLeft = 14; b.style.paddingRight = 14;
            b.style.paddingTop = 8; b.style.paddingBottom = 8;
            LvnChrome.ClearBorder(b);
            LvnChrome.Round(b, 10f);
            b.style.backgroundColor = active
                ? new Color(tint.r, tint.g, tint.b, 0.28f)
                : new Color(tint.r, tint.g, tint.b, 0.10f);
            b.style.color = tint;
        }

        private VisualElement FontSegment()
        {
            var options = new List<string> { "" };
            foreach (var f in LvnFonts.Families) options.Add(f.Id);
            return LvnSegment.Of(options,
                id => string.IsNullOrEmpty(id) ? L("font_author", "As authored")
                     : Lvn.Content.LvnWords.Name("font", id, LvnFonts.FamilyOf(id).Title),
                id => (LvnPrefs.FontFamily ?? "") == id,
                id => LvnPrefs.FontFamily = id,
                StyleOption);
        }

        private static readonly (float k, string key, string en)[] SizeSteps =
        {
            (0.85f, "size_xs", "XS"), (0.92f, "size_s", "S"), (1f, "size_m", "M"),
            (1.15f, "size_l", "L"), (1.3f, "size_xl", "XL"),
        };

        private VisualElement ScaleSegment(Func<float> get, Action<float> set)
            => LvnSegment.Of(SizeSteps,
                st => L(st.key, st.en),
                st => Mathf.Abs(get() - st.k) < 0.01f,
                st => set(st.k),
                StyleOption);

        private VisualElement SettingCard()
        {
            var card = new VisualElement();
            var tint = _theme.MenuTextColor;
            card.style.backgroundColor = new Color(tint.r, tint.g, tint.b, 0.06f);
            LvnChrome.Round(card, 12f);
            card.style.paddingLeft = 16; card.style.paddingRight = 16;
            card.style.paddingTop = 12; card.style.paddingBottom = 12;
            card.style.marginBottom = 10;
            return card;
        }

        private VisualElement SliderRow(string label, float min, float max, float value, Action<float> onChange)
        {
            var card = SettingCard();
            card.Add(Text(label, 24, FontStyle.Normal));

            var accent = LvnTokens.Accent;
            var tint = _theme.MenuTextColor;
            var s = new Slider(min, max) { value = value };
            s.style.height = 40;
            s.style.marginTop = 6;
            s.style.marginLeft = 0; s.style.marginRight = 0;

            var tracker = s.Q("unity-tracker");
            VisualElement fill = null;
            if (tracker != null)
            {
                tracker.style.height = 8;
                tracker.style.marginTop = 16;
                tracker.style.backgroundColor = new Color(tint.r, tint.g, tint.b, 0.18f);
                LvnChrome.Round(tracker, 4f);
                LvnChrome.ClearBorder(tracker);
                fill = new VisualElement();
                fill.style.position = Position.Absolute;
                fill.style.left = 0; fill.style.top = 0; fill.style.bottom = 0;
                fill.style.backgroundColor = accent;
                LvnChrome.Round(fill, 4f);
                fill.pickingMode = PickingMode.Ignore;
                tracker.Add(fill);
            }
            var dragger = s.Q("unity-dragger");
            if (dragger != null)
            {
                dragger.style.width = 28; dragger.style.height = 28;
                dragger.style.top = 6;
                dragger.style.backgroundColor = accent;
                LvnChrome.Round(dragger, 14f);
                LvnChrome.ClearBorder(dragger);
            }
            void SyncFill(float v)
            {
                if (fill != null)
                    fill.style.width = Length.Percent(Mathf.Clamp01((v - min) / (max - min)) * 100f);
            }
            SyncFill(value);
            s.RegisterValueChangedCallback(e => { onChange(e.newValue); SyncFill(e.newValue); });
            card.Add(s);
            return card;
        }

        private VisualElement ToggleRow(string label, bool value, Action<bool> onChange)
        {
            var card = SettingCard();
            card.style.flexDirection = FlexDirection.Row;
            card.style.justifyContent = Justify.SpaceBetween;
            card.style.alignItems = Align.Center;
            card.Add(Text(label, 24, FontStyle.Normal));

            var tint = _theme.MenuTextColor;
            var offBg = new Color(tint.r, tint.g, tint.b, 0.18f);
            var track = new VisualElement();
            track.style.width = 64; track.style.height = 36;
            LvnChrome.Round(track, 18f);
            track.style.flexDirection = FlexDirection.Row;
            track.style.alignItems = Align.Center;
            track.style.paddingLeft = 4; track.style.paddingRight = 4;
            var knob = new VisualElement();
            knob.style.width = 28; knob.style.height = 28;
            LvnChrome.Round(knob, 14f);
            knob.style.backgroundColor = Color.white;
            knob.pickingMode = PickingMode.Ignore;
            track.Add(knob);
            bool cur = value;
            void Paint()
            {
                track.style.backgroundColor = cur ? LvnTokens.Accent : offBg;
                track.style.justifyContent = cur ? Justify.FlexEnd : Justify.FlexStart;
            }
            Paint();
            // Переключает вся карточка — попадать пальцем в один тумблер
            // на телефоне неудобно.
            card.RegisterCallback<ClickEvent>(_ => { cur = !cur; onChange(cur); Paint(); });
            card.Add(track);
            return card;
        }
    }
}
