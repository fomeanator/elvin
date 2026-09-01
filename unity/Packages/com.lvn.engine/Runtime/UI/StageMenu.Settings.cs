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
            // ЧТО настраивается — у КАТАЛОГА, здесь только вид. Набор жил в
            // двух местах, и имена уже разошлись: прозрачность окна звалась
            // здесь window_opacity, а в оболочке settings.box_opacity — и
            // переводчик переводил ровно половину.
            foreach (var d in LvnSettingsCatalog.Reading()) scroll.Add(RowFor(d));
            foreach (var d in LvnSettingsCatalog.Audio(_theme.SimpleAudioSliders)) scroll.Add(RowFor(d));

            // Language — only when the content ships catalogs (manifest.languages).
            // Tapping cycles Original → each language → Original.
            if (LvnLocale.Offered)
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
            btn.style.height = LvnTokens.Touch;
            btn.style.fontSize = LvnTokens.TextSm;
            LvnAir.PadX(btn, LvnTokens.Space3);
            LvnStyler.Primary(btn, 14f);
            LvnFonts.Apply(btn, _theme.Font);
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
            seg.style.marginTop = LvnTokens.Space1;
            card.Add(seg);
            return card;
        }

        // Кнопка варианта в стиле сценного меню: плашка из темы новеллы, а не
        // из токенов оболочки — глава рисуется её палитрой.
        private void StyleOption(Button b, bool active)
        {
            var tint = _theme.MenuTextColor;
            b.style.fontSize = LvnTokens.TextSm;
            b.style.whiteSpace = WhiteSpace.Normal;   // крупный кегль и широкая гарнитура переносятся
            LvnAir.Pad(b, LvnTokens.Space2, LvnTokens.Space1);
            LvnStyler.Plate(b, UiColor.WithAlpha(tint, active ? 0.28f : 0.10f),
                tint, LvnTokens.RadiusSm);
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

        // Ступени и допуск сравнения — из дома ручек: те же пять значений
        // стояли здесь и в настройках оболочки двумя списками.
        private VisualElement ScaleSegment(Func<float> get, Action<float> set)
            => LvnSegment.Of(LvnKnobs.Scale,
                st => L(st.Key, st.En),
                st => LvnKnobs.At(get(), st),
                st => set(st.K),
                StyleOption);

        private VisualElement SettingCard()
        {
            var card = new VisualElement();
            var tint = _theme.MenuTextColor;
            card.style.backgroundColor = UiColor.WithAlpha(tint, 0.06f);
            LvnChrome.Round(card, LvnTokens.RadiusSm);
            LvnAir.Pad(card, LvnTokens.Space3, LvnTokens.Space2);
            card.style.marginBottom = LvnTokens.Space2;
            return card;
        }

        /// <summary>Строка настройки в СЦЕНЕ: компактная карточка. Что
        /// показывать, знает каталог; как — этот метод.</summary>
        private VisualElement RowFor(LvnSettingDef d)
            => d.Kind == LvnSettingKind.Switch
                ? ToggleRow(LvnSettingsCatalog.Label(d, _theme), d.Flag(), v => d.SetFlag(v))
                : SliderRow(LvnSettingsCatalog.Label(d, _theme), d.Min, d.Max, d.Num(),
                            v => d.SetNum(v), live: d.Live);

        /// <summary>
        /// Ряд с ползунком. Вид — из дома, и МОМЕНТ ПРИМЕНЕНИЯ тоже.
        ///
        /// <para><paramref name="live"/> — только для того, что слышно на ходу.
        /// Предпросмотр стоял здесь у ВСЕХ рядов без разбора, и три настройки,
        /// к звуку отношения не имеющие (скорость текста, задержка авто,
        /// прозрачность окна), записывались на устройство на каждое движение
        /// пальца — с фиксацией на диск и событием «настройки изменились»
        /// каждый кадр. Это и есть «ползунок ненадёжный»: он дёргается под
        /// пальцем, потому что каждый кадр чинит за собой полсцены. В
        /// настройках оболочки те же три ползунка уже вели себя гладко —
        /// разошлись ровно эти две записи одного правила.</para>
        /// </summary>
        private VisualElement SliderRow(string label, float min, float max, float value,
                                        Action<float> onChange, bool live = false)
        {
            var card = SettingCard();
            card.Add(Text(label, 24, FontStyle.Normal));
            card.Add(LvnSlider.Make(min, max, value, onChange,
                onPreview: live ? onChange : null,
                accent: LvnTokens.Accent,
                track: UiColor.WithAlpha(_theme.MenuTextColor, 0.18f)));
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
            var offBg = UiColor.WithAlpha(tint, 0.18f);
            var track = new VisualElement();
            track.style.width = 64; track.style.height = 36;
            LvnChrome.Round(track, LvnTokens.Radius);
            track.style.flexDirection = FlexDirection.Row;
            track.style.alignItems = Align.Center;
            LvnAir.PadX(track, LvnTokens.Tight);
            var knob = new VisualElement();
            knob.style.width = 28; knob.style.height = 28;
            LvnChrome.Round(knob, LvnTokens.Radius);
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
