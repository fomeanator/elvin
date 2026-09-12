using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// КАРТОЧКИ И ПОЛОСЫ — из чего сложена лента хаба.
    ///
    /// <para>Полоса сборника, крупная карточка-герой, обычная плитка: разные
    /// размеры одного и того же — обложка, название, состояние (замок, цена,
    /// «продолжить»). Отдельным домом, потому что это ВЁРСТКА, и менять её
    /// приходится по причинам вида «не читается на телефоне», а не по причинам
    /// хаба.</para>
    /// </summary>
    public sealed partial class BrowseHub
    {
        // Размер плитки и зазоры — в BrowseHub.Rhythm.cs: одни числа на все полки.

        private VisualElement CollectionRow(LvnCollection c, bool hero)
        {
            var row = new VisualElement { name = ShelfName };
            row.style.flexShrink = 0; // children of a vertical ScrollView must not shrink
            row.style.marginBottom = ShelfGap;

            var shown = new System.Collections.Generic.List<LvnTitle>();
            if (c.titles != null)
                foreach (var id in c.titles)
                    if (_titles.TryGetValue(id, out var t)) shown.Add(t);

            row.Add(ShelfHead(c, shown.Count));

            // ПУСТАЯ ПОЛКА ОСТАЁТСЯ НА МЕСТЕ. Прежде сборник без новелл исчезал
            // целиком, и игрок не знал, что раздел есть; теперь шапка стоит,
            // счётчик говорит «0», а под ним — слово автора о том, что здесь
            // будет (ui.browse.empty_text).
            if (shown.Count == 0) { row.Add(EmptyShelf()); return row; }

            var strip = Lvn.UI.LvnScroll.Horizontal();
            // И ВЕРТИКАЛЬНУЮ тоже. Полоса брала своё не от прокрутки, а от того,
            // что карточка выше отведённой ей строки: сбоку появлялся системный
            // ползунок чужого вида, а низ карточки обрезался.
            strip.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            strip.style.flexShrink = 0;
            strip.style.flexDirection = FlexDirection.Row;
            // Полоса того же роста, что плитка: одно число на обеих.
            strip.style.height = ShelfCardHeight;
            var entering = new System.Collections.Generic.List<VisualElement>(shown.Count);
            foreach (var t in shown)
            {
                var card = SliderCard(t, c, hero);
                strip.Add(card);
                entering.Add(card);
            }
            row.Add(strip);
            RevealShelf(entering);
            return row;
        }

        /// <summary>Шапка полки: название с разрядкой темы, счётчик новелл
        /// плашкой и «Все ›», когда есть что листать.</summary>
        private VisualElement ShelfHead(LvnCollection c, int count)
        {
            var head = ScreenUi.Row(spread: true);
            head.name = ShelfHeadName;
            head.style.marginBottom = HeadGap;

            var lead = ScreenUi.Row();
            lead.style.flexShrink = 1; lead.style.flexGrow = 1;
            // Подпись знает свой источник: при смене языка её перечитает дом,
            // а карточки не придётся пересобирать — вместе с ними уехали бы
            // прокрутка и то, на чём игрок остановился.
            var cid = c.id; var cname = c.name;
            var title = Lvn.UI.LvnRedress.Bind(new Label(),
                () => _theme.Heading(cid == LibraryId
                    ? LvnWords.Pick("hub.library", _cfg?.library_text, "Novels")
                    : Lvn.Content.LvnWords.Name("collection", cid, cname)));
            title.style.color = _text; title.style.fontSize = LvnTokens.TextXl;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.letterSpacing = _theme.Tracking;
            title.style.flexShrink = 1;
            lead.Add(title);
            lead.Add(CountPill(count));
            head.Add(lead);

            // «Все ›» — подпись и векторная стрелка. Стрелка символом «→» на
            // части шрифтов Android тоже отсутствует, а её пропажу замечаешь
            // позже прочих: пустое место в конце строки читается как отступ.
            // На пустой полке листать нечего — и звать некуда.
            if (count > 0)
            {
                var all = ScreenUi.Row();
                all.style.flexShrink = 0;
                var allText = Lvn.UI.LvnRedress.Bind(new Label(),
                    () => _theme.Heading(LvnWords.Pick("hub.all", _cfg?.all_text, "All")));
                allText.pickingMode = PickingMode.Ignore;
                allText.style.color = _accent; allText.style.fontSize = LvnTokens.TextLg;
                allText.style.unityFontStyleAndWeight = FontStyle.Bold;
                allText.style.letterSpacing = _theme.Tracking;
                all.Add(allText);
                var allArrow = LvnIcons.Make(LvnIcon.Chevron, 20f, _accent);
                allArrow.style.marginLeft = LvnTokens.Tight;
                all.Add(allArrow);
                all.RegisterCallback<ClickEvent>(_ => ShowCollection(c));
                LvnMotion.Tappable(all);
                head.Add(all);
            }
            return head;
        }

        /// <summary>Счётчик полки: сколько новелл на ней на самом деле —
        /// известных каталогу, а не перечисленных в сборнике.</summary>
        private Label CountPill(int count)
        {
            var pill = Lvn.UI.LvnRedress.Bind(new Label { name = ShelfCountName },
                () => LvnWords.Of("hub.count", "{0}", count));
            pill.pickingMode = PickingMode.Ignore;
            LvnStyler.Plate(pill, LvnTokens.Faint, _text, LvnTokens.RadiusPill);
            LvnAir.PadX(pill, LvnTokens.Space2);
            pill.style.marginLeft = LvnTokens.Space2;
            pill.style.flexShrink = 0;
            pill.style.alignSelf = Align.Center;
            pill.style.fontSize = LvnTokens.TextXs;
            pill.style.unityTextAlign = TextAnchor.MiddleCenter;
            return pill;
        }

        /// <summary>Пустая полка: компактная плашка во всю ширину со значком
        /// часов и словом автора. Не притворяется карточкой новеллы — иначе
        /// её бы нажимали.</summary>
        private VisualElement EmptyShelf()
        {
            var empty = ScreenUi.Row();
            empty.name = ShelfEmptyName;
            empty.pickingMode = PickingMode.Ignore;
            empty.style.height = EmptyShelfHeight;
            empty.style.flexShrink = 0;
            empty.style.backgroundColor = UiColor.WithAlpha(_card, 0.72f);
            LvnChrome.Frame(empty, _radius, UiColor.WithAlpha(_border, _border.a * 0.85f), LvnTokens.Hair);
            LvnAir.PadX(empty, LvnTokens.Space4);

            var well = new VisualElement { pickingMode = PickingMode.Ignore };
            well.style.width = LvnTokens.TouchLg; well.style.height = LvnTokens.TouchLg;
            well.style.flexShrink = 0;
            well.style.alignItems = Align.Center; well.style.justifyContent = Justify.Center;
            well.style.backgroundColor = UiColor.WithAlpha(_accent, 0.10f);
            LvnChrome.Frame(well, LvnTokens.RadiusPill, UiColor.WithAlpha(_accent, 0.28f), LvnTokens.Hair);
            well.Add(LvnIcons.Make(LvnIcon.Clock, LvnTokens.TextLg, UiColor.WithAlpha(_accent, 0.82f)));
            empty.Add(well);

            var word = Lvn.UI.LvnRedress.Bind(new Label(),
                () => LvnWords.Pick("hub.empty", _cfg?.empty_text, "Coming soon"));
            word.pickingMode = PickingMode.Ignore;
            word.style.color = _text; word.style.fontSize = LvnTokens.TextBase;
            word.style.marginLeft = LvnTokens.Space3;
            word.style.whiteSpace = WhiteSpace.Normal;
            word.style.flexShrink = 1;
            empty.Add(word);
            return empty;
        }

        // A poster card inside a slider: gradient depth, a cost/lock chip top-right,
        // the title + a "Подробнее" button at the bottom. Whole card opens detail.
        // A shelf card with a poster and its own dark caption plinth. Before this
        // the title sat naked on the heroine/canvas behind it, so a bright raindrop
        // or a pale sleeve could erase a word at a glance.
        private VisualElement SliderCard(LvnTitle t, LvnCollection from, bool hero)
        {
            bool locked = IsLocked(t);
            var card = new VisualElement { name = ShelfCardName };
            card.style.width = CardW;
            card.style.height = ShelfCardHeight;
            card.style.flexShrink = 0;      // horizontal slider: keep the poster size
            card.style.marginRight = CardGap;
            // ЗАМОК ГАСИТ ОБЛОЖКУ, А НЕ ПЛИТКУ: полупрозрачная плитка целиком
            // гасила и подпись, и название закрытой новеллы не читалось.
            // Вуаль ложится на постер (ниже), слова остаются на цоколе.
            bool active = !locked && LvnProgress.Current(t) != null;
            var plinth = LvnTokens.PanelBg;
            card.style.backgroundColor = UiColor.WithAlpha(plinth, 0.93f);
            card.style.overflow = Overflow.Hidden;
            // «Читаю сейчас» — единственная плитка с весом: рамка акцентом,
            // свечение из-под цоколя (ниже). У остальных рамка темы.
            LvnChrome.Frame(card, _radius + 2f,
                active ? UiColor.WithAlpha(_accent, 0.9f) : UiColor.WithAlpha(_border, _border.a * 0.85f), 1f);

            // Poster has only the top rounding; the caption below is visibly part
            // of the same physical card rather than loose text under an image.
            var poster = new VisualElement();
            poster.style.width = Length.Percent(100f);
            // Коробка нарочно ВЫШЕ пропорции фигуры (спайн noel = 2310×2778,
            // w/h 0.83): постер вписывает спайн целиком (background-size
            // contain, см. LvnSpinePoster), поэтому фигура видна во весь рост
            // и не тянется, а лишняя высота уходит в поле карточки.
            poster.style.height = PosterH;
            poster.style.overflow = Overflow.Hidden;
            poster.style.backgroundColor = _card;
            LvnChrome.RoundTop(poster, _radius + 2f);

            string art = t.CardArt();
            // Живой спайн вместо статичного постера (Илья: «заменить их бг на
            // спайн»). Каждая карточка поднимает СВОЮ фигуру. Обложка — запас
            // для карточек БЕЗ спайна.
            // ...но только если мост со spine-unity ЖИВОЙ. Без него Attach
            // тихо выходит, а обложку мы на такой карточке уже не ставим —
            // получился бы пустой прямоугольник вместо новеллы. Нет моста —
            // карточка честно откатывается на обложку.
            var cardSpine = Lvn.UI.LvnSpineBridge.Available ? SpineForTitle(t) : null;
            if (cardSpine != null)
            {
                // ОБЛОЖКУ НА СПАЙН-КАРТОЧКЕ НЕ СТАВИМ. И обложка, и спайн пишут
                // ОДНО поле backgroundImage, оба асинхронно: обложка, приехавшая
                // позже фигуры, молча затёрла бы её. Не сложилось со спайном —
                // постер остаётся полем карточки, без мигания подменой.
                Lvn.UI.LvnSpinePoster.Attach(poster, cardSpine,
                    url => _assets.LoadTextAsync(url, default),
                    url => _assets.LoadSpriteAsync(url, default),
                    // Закрепление страниц атласа — через тот же загрузчик, что их
                    // выдал; иначе стриминговое окно унесёт текстуры из-под
                    // живого скелета.
                    (_assets as Lvn.UI.CachingAssets)?.Loader,
                    // Спайн не сложился (нет файлов, страницу унесла уборка) —
                    // карточка НЕ остаётся пустой, а показывает обложку.
                    () =>
                    {
                        if (!string.IsNullOrEmpty(art)) LvnPicture.Layer(poster, art, _assets);
                        else poster.style.backgroundImage = PosterFallbackImage(useAccent: hero);
                    });
            }
            else if (!string.IsNullOrEmpty(art))
            {
                LvnPicture.Layer(poster, art, _assets);
            }
            else
            {
                poster.style.backgroundImage = PosterFallbackImage(useAccent: hero);
            }
            if (locked)
            {
                var veil = new VisualElement { name = ShelfLockName, pickingMode = PickingMode.Ignore };
                ScreenUi.Stretch(veil);
                veil.style.backgroundColor = LvnTokens.Veil(LockedVeil);
                poster.Add(veil);
            }
            else if (active)
            {
                var glow = new VisualElement { name = ShelfGlowName, pickingMode = PickingMode.Ignore };
                glow.style.position = Position.Absolute;
                glow.style.left = 0; glow.style.right = 0; glow.style.bottom = 0;
                glow.style.height = Length.Percent(GlowPercent);
                glow.style.backgroundImage = LvnBackdrop.Vertical(
                    UiColor.WithAlpha(_accent, 0f), UiColor.WithAlpha(_accent, GlowAlpha), smooth: true);
                poster.Add(glow);
            }
            // cost / lock chip, small, floated on the poster
            var chip = locked ? Chip(LvnWords.Pick("hub.locked", _cfg?.locked_text, "Locked"), _dim, LvnIcon.Lock)
                : (t.cost != null && t.cost.amount > 0 ? CostChip(t.cost) : null);
            if (chip != null)
            {
                chip.style.position = Position.Absolute; chip.style.top = 12; chip.style.right = 12;
                poster.Add(chip);
            }
            card.Add(poster);

            // A solid caption field is the readability contract for a shelf:
            // title and chapter metadata must never compete with the moving scene.
            var caption = new VisualElement { pickingMode = PickingMode.Ignore };
            LvnAir.Pad(caption, LvnTokens.Space2);
            caption.style.flexGrow = 1;
            caption.style.backgroundColor = UiColor.WithAlpha(plinth, 0.98f);

            var tid0 = t.id; var tname0 = t.name;
            var name = Lvn.UI.LvnRedress.Bind(new Label(),
                () => Lvn.Content.LvnWords.Name("title", tid0, tname0));
            name.style.color = _text; name.style.fontSize = LvnTokens.TextBase;
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.whiteSpace = WhiteSpace.Normal;
            name.style.maxHeight = 108;     // две строки не съедают метаданные (карточка вдвое крупнее)
            name.style.overflow = Overflow.Hidden;
            caption.Add(name);

            // Подзаголовок («Сезон 1 · Глава 0 — Вербовка») — тоже данные:
            // переведён — берём перевод, нет — читаем латиницей, чтобы он не
            // висел кириллицей посреди английской карточки.
            var tsub0 = t.subtitle ?? t.card?.description;
            string sub = Lvn.Content.LvnWords.Name("subtitle", tid0, tsub0);
            if (!string.IsNullOrEmpty(sub))
            {
                var subLbl = Lvn.UI.LvnRedress.Bind(new Label(),
                    () => Lvn.Content.LvnWords.Name("subtitle", tid0, tsub0));
                subLbl.style.color = _dim; subLbl.style.fontSize = LvnTokens.TextSm; subLbl.style.marginTop = LvnTokens.Tight;
                subLbl.style.whiteSpace = WhiteSpace.NoWrap;
                subLbl.style.overflow = Overflow.Hidden;
                subLbl.style.textOverflow = TextOverflow.Ellipsis;
                caption.Add(subLbl);
            }
            card.Add(caption);

            LvnMotion.Tappable(card);
            card.RegisterCallback<ClickEvent>(evt =>
            {
                if (locked) { FireLockedHint(Lvn.Content.LvnWords.Name("title", t.id, t.name), t.locked_hint ?? ""); }
                else OpenDetail(t, from);
            });
            return card;
        }

        // A full-width list card (one per row): a thumbnail on the left, then the
        // name + a mini-description + a progress bar, and a cost/lock chip.
        private VisualElement TitleCard(LvnTitle t)
        {
            if (Staged) return StageTitleCard(t);   // список носит тот же облик, что главная
            bool locked = IsLocked(t);
            var card = new VisualElement();
            card.style.flexDirection = FlexDirection.Row;
            card.style.height = 128;
            card.style.backgroundColor = _card;
            Lvn.UI.LvnChrome.Border(card, _border, 1f);
            card.style.opacity = locked ? 0.55f : 1f;
            LvnChrome.Round(card, _radius);
            card.style.marginBottom = LvnTokens.Space2;
            card.style.overflow = Overflow.Hidden;

            // thumbnail (left)
            var thumb = new VisualElement { pickingMode = PickingMode.Ignore };
            thumb.style.width = 128; thumb.style.height = Length.Percent(100f);
            thumb.style.backgroundColor = _theme.SurfaceHi;
            Edge(thumb);
            var art = t.CardArt();
            if (!string.IsNullOrEmpty(art)) LvnPicture.Photo(thumb, art, _assets);
            card.Add(thumb);

            // text column (right)
            var col = new VisualElement();
            col.style.flexGrow = 1; col.style.justifyContent = Justify.Center;
            LvnAir.Pad(col, LvnTokens.Space3, LvnTokens.Space2);

            var top = new VisualElement();
            top.style.flexDirection = FlexDirection.Row; top.style.justifyContent = Justify.SpaceBetween;
            top.style.alignItems = Align.Center;
            var tid0 = t.id; var tname0 = t.name;
            var name = Lvn.UI.LvnRedress.Bind(new Label(),
                () => Lvn.Content.LvnWords.Name("title", tid0, tname0));
            name.style.color = _text; name.style.fontSize = LvnTokens.TextLg;
            name.style.unityFontStyleAndWeight = FontStyle.Bold; name.style.flexGrow = 1;
            top.Add(name);
            if (locked) top.Add(Chip(LvnWords.Pick("hub.locked", _cfg?.locked_text, "Locked"), _dim, LvnIcon.Lock));
            else if (t.cost != null && t.cost.amount > 0) top.Add(CostChip(t.cost));
            col.Add(top);

            var tid1 = t.id; var tsub1 = t.card?.description ?? t.subtitle ?? "";
            var desc = Lvn.UI.LvnRedress.Bind(new Label(),
                () => Lvn.Content.LvnWords.Name("subtitle", tid1, tsub1));
            desc.style.color = _dim; desc.style.fontSize = LvnTokens.TextSm; desc.style.marginTop = LvnTokens.Tight;
            desc.style.whiteSpace = WhiteSpace.Normal;
            desc.style.overflow = Overflow.Hidden;
            col.Add(desc);

            // ПОЛОСА ПРОЧИТАННОГО — настоящая. Здесь стояли зашитые 35%
            // («demo progress»): одинаковые у непочатой новеллы и у почти
            // пройденной, у всех игроков и во всех историях. Полосу читают как
            // сведения о себе, поэтому заглушка тут врала прямо в лицо.
            float read = locked ? 0f : LvnProgress.Fraction(t);
            if (read > 0f)
            {
                var track = new VisualElement();
                track.style.height = 6; track.style.marginTop = LvnTokens.Space2; track.style.flexShrink = 0;
                track.style.backgroundColor = _theme.SurfaceHi; LvnChrome.Round(track, LvnTokens.RadiusXs); track.style.overflow = Overflow.Hidden;
                var fill = new VisualElement();
                fill.style.height = Length.Percent(100f);
                fill.style.width = Length.Percent(read * 100f);
                fill.style.backgroundColor = _accent; LvnChrome.Round(fill, LvnTokens.RadiusXs);
                track.Add(fill); col.Add(track);
            }

            card.Add(col);

            LvnMotion.Tappable(card);
            card.RegisterCallback<ClickEvent>(evt =>
            {
                if (locked) { FireLockedHint(Lvn.Content.LvnWords.Name("title", t.id, t.name), t.locked_hint ?? ""); }
                else OpenDetail(t, CurrentCollectionOf(t));
            });
            return card;
        }
    }
}
