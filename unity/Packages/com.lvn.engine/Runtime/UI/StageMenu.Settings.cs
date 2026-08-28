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
            _pane = ShowSettings;
            var p = Panel(L("settings", "Settings"));
            var scroll = LvnScroll.Vertical();
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

            // Круг вариантов — общий с настройками оболочки: здесь он был свой
            // и без «авто», то есть два экрана предлагали разный выбор.
            string Caption(string code) => LvnLocale.Title(code);

            var btn = new Button { text = Caption(LvnLocale.Chosen) };
            btn.style.minWidth = 150;
            btn.style.height = 48;
            btn.style.fontSize = 22;
            btn.style.paddingLeft = 18; btn.style.paddingRight = 18;
            LvnStyler.Primary(btn, 14f);
            if (_theme.Font != null) btn.style.unityFont = new StyleFont(_theme.Font);
            btn.clicked += () =>
            {
                LvnLocale.Chosen = LvnLocale.Next(LvnLocale.Chosen);
                btn.text = Caption(LvnLocale.Chosen);
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
            // Ползунок — из дома: вид и правило «применяем при отпускании» были
            // записаны здесь и в настройках оболочки по отдельности и успели
            // разойтись. Громкость слышна только вживую, поэтому у неё есть
            // предпросмотр.
            card.Add(LvnSlider.Make(min, max, value, onChange, onPreview: onChange,
                accent: LvnTokens.Accent,
                track: new Color(_theme.MenuTextColor.r, _theme.MenuTextColor.g,
                                 _theme.MenuTextColor.b, 0.18f)));
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
