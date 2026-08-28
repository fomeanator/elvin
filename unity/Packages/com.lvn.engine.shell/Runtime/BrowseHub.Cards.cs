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
        private VisualElement CollectionRow(LvnCollection c, bool hero)
        {
            var row = new VisualElement();
            row.style.flexShrink = 0; // children of a vertical ScrollView must not shrink
            row.style.marginBottom = 30;

            var head = new VisualElement();
            head.style.flexDirection = FlexDirection.Row;
            head.style.alignItems = Align.Center;
            head.style.justifyContent = Justify.SpaceBetween;
            head.style.marginBottom = 14;
            // Подпись знает свой источник: при смене языка её перечитает дом,
            // а карточки не придётся пересобирать — вместе с ними уехали бы
            // прокрутка и то, на чём игрок остановился.
            var cid = c.id; var cname = c.name;
            var title = Lvn.UI.LvnRedress.Bind(new Label(),
                () => _theme.Heading(cid == LibraryId
                    ? LvnWords.Pick("hub.library", _cfg?.library_text, "Novels")
                    : Lvn.Content.LvnWords.Name("collection", cid, cname)));
            title.style.color = _text; title.style.fontSize = 54;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.letterSpacing = _theme.Tracking;
            head.Add(title);
            // «Все ›» — подпись и векторная стрелка. Стрелка символом «→» на
            // части шрифтов Android тоже отсутствует, а её пропажу замечаешь
            // позже прочих: пустое место в конце строки читается как отступ.
            var all = new VisualElement();
            all.style.flexDirection = FlexDirection.Row;
            all.style.alignItems = Align.Center;
            var allText = Lvn.UI.LvnRedress.Bind(new Label(),
                () => _theme.Heading(LvnWords.Pick("hub.all", _cfg?.all_text, "All")));
            allText.pickingMode = PickingMode.Ignore;
            allText.style.color = _accent; allText.style.fontSize = 36;
            allText.style.unityFontStyleAndWeight = FontStyle.Bold;
            allText.style.letterSpacing = _theme.Tracking;
            all.Add(allText);
            var allArrow = LvnIcons.Make(LvnIcon.Chevron, 20f, _accent, 0f, _theme.IconGlow);
            allArrow.style.marginLeft = 4;
            all.Add(allArrow);
            all.RegisterCallback<ClickEvent>(_ => ShowCollection(c));
            LvnMotion.Tappable(all);
            head.Add(all);
            row.Add(head);

            var strip = Lvn.UI.LvnScroll.Horizontal();
            // И ВЕРТИКАЛЬНУЮ тоже. Полоса брала своё не от прокрутки, а от того,
            // что карточка выше отведённой ей строки: сбоку появлялся системный
            // ползунок чужого вида, а низ карточки обрезался.
            strip.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            strip.style.flexShrink = 0;
            strip.style.flexDirection = FlexDirection.Row;
            var entering = new System.Collections.Generic.List<VisualElement>();
            if (c.titles != null)
                foreach (var id in c.titles)
                    if (_titles.TryGetValue(id, out var t))
                    {
                        var card = SliderCard(t, c, hero);
                        strip.Add(card);
                        entering.Add(card);
                    }
            // Карточки приезжают со сдвигом, а не разом: одновременное появление
            // читается как перерисовка экрана, последовательное — как намерение.
            // Пустой сборник — не строка нулевой высоты, а ОТСУТСТВИЕ строки.
            // Фиксированная высота ниже иначе зарезервировала бы полэкрана
            // пустоты под заголовком, который не о чем.
            if (entering.Count == 0) return null;
            // Подпись теперь живёт на собственном матовом цоколе, а не поверх
            // шумного полотна меню. Высота считается от постера и этой плашки:
            // ни буквы, ни нижняя кромка не могут провалиться под навигацию.
            strip.style.height = 292f + 112f;
            // Плитки просто проступают: волна с въездом и пружиной читалась
            // как дёрганье списка (Илья 26.08).
            Lvn.UI.LvnMotion.FadeInAll(entering);
            row.Add(strip);
            return row;
        }

        // A poster card inside a slider: gradient depth, a cost/lock chip top-right,
        // the title + a "Подробнее" button at the bottom. Whole card opens detail.
        // A shelf card with a poster and its own dark caption plinth. Before this
        // the title sat naked on the heroine/canvas behind it, so a bright raindrop
        // or a pale sleeve could erase a word at a glance.
        private VisualElement SliderCard(LvnTitle t, LvnCollection from, bool hero)
        {
            bool locked = IsLocked(t);
            var card = new VisualElement();
            card.style.width = 250;
            card.style.flexShrink = 0;      // horizontal slider: keep the poster size
            card.style.marginRight = 18;
            card.style.opacity = locked ? 0.5f : 1f;
            var plinth = LvnTokens.PanelBg;
            card.style.backgroundColor = new Color(plinth.r, plinth.g, plinth.b, 0.93f);
            card.style.overflow = Overflow.Hidden;
            LvnChrome.Round(card, _radius + 2f);
            LvnChrome.Border(card, new Color(_border.r, _border.g, _border.b, _border.a * 0.85f), 1f);

            // Poster has only the top rounding; the caption below is visibly part
            // of the same physical card rather than loose text under an image.
            var poster = new VisualElement();
            poster.style.width = Length.Percent(100f);
            poster.style.height = 292;
            poster.style.overflow = Overflow.Hidden;
            poster.style.backgroundColor = _card;
            poster.style.borderTopLeftRadius = _radius + 2f;
            poster.style.borderTopRightRadius = _radius + 2f;

            string art = t.CardArt();
            if (!string.IsNullOrEmpty(art))
            {
                var img = new VisualElement { pickingMode = PickingMode.Ignore };
                ScreenUi.Stretch(img);
                LvnPicture.Fit(img);
                poster.Add(img);
                ScreenUi.SetBg(img, art, _assets);
            }
            else
            {
                poster.style.backgroundImage = PosterFallbackImage(useAccent: hero);
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
            caption.style.paddingTop = 13; caption.style.paddingBottom = 12;
            caption.style.paddingLeft = 14; caption.style.paddingRight = 14;
            caption.style.flexGrow = 1;
            caption.style.backgroundColor = new Color(plinth.r, plinth.g, plinth.b, 0.98f);

            var tid0 = t.id; var tname0 = t.name;
            var name = Lvn.UI.LvnRedress.Bind(new Label(),
                () => Lvn.Content.LvnWords.Name("title", tid0, tname0));
            name.style.color = _text; name.style.fontSize = 32;
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.whiteSpace = WhiteSpace.Normal;
            name.style.maxHeight = 54;      // две строки не съедают метаданные
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
                subLbl.style.color = _dim; subLbl.style.fontSize = 22; subLbl.style.marginTop = 4;
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
            bool locked = IsLocked(t);
            var card = new VisualElement();
            card.style.flexDirection = FlexDirection.Row;
            card.style.height = 128;
            card.style.backgroundColor = _card;
            card.style.borderTopWidth = 1; card.style.borderBottomWidth = 1;
            card.style.borderLeftWidth = 1; card.style.borderRightWidth = 1;
            Lvn.UI.LvnChrome.Tint(card, _border);
            card.style.opacity = locked ? 0.55f : 1f;
            LvnChrome.Round(card, _radius);
            card.style.marginBottom = 14;
            card.style.overflow = Overflow.Hidden;

            // thumbnail (left)
            var thumb = new VisualElement { pickingMode = PickingMode.Ignore };
            thumb.style.width = 128; thumb.style.height = Length.Percent(100f);
            thumb.style.backgroundColor = _theme.SurfaceHi;
            Edge(thumb);
            LvnPicture.Fit(thumb);
            var art = t.CardArt();
            if (!string.IsNullOrEmpty(art)) ScreenUi.SetBg(thumb, art, _assets);
            card.Add(thumb);

            // text column (right)
            var col = new VisualElement();
            col.style.flexGrow = 1; col.style.justifyContent = Justify.Center;
            col.style.paddingLeft = 18; col.style.paddingRight = 16;
            col.style.paddingTop = 14; col.style.paddingBottom = 14;

            var top = new VisualElement();
            top.style.flexDirection = FlexDirection.Row; top.style.justifyContent = Justify.SpaceBetween;
            top.style.alignItems = Align.Center;
            var tid0 = t.id; var tname0 = t.name;
            var name = Lvn.UI.LvnRedress.Bind(new Label(),
                () => Lvn.Content.LvnWords.Name("title", tid0, tname0));
            name.style.color = _text; name.style.fontSize = 36;
            name.style.unityFontStyleAndWeight = FontStyle.Bold; name.style.flexGrow = 1;
            top.Add(name);
            if (locked) top.Add(Chip(LvnWords.Pick("hub.locked", _cfg?.locked_text, "Locked"), _dim, LvnIcon.Lock));
            else if (t.cost != null && t.cost.amount > 0) top.Add(CostChip(t.cost));
            col.Add(top);

            var tid1 = t.id; var tsub1 = t.card?.description ?? t.subtitle ?? "";
            var desc = Lvn.UI.LvnRedress.Bind(new Label(),
                () => Lvn.Content.LvnWords.Name("subtitle", tid1, tsub1));
            desc.style.color = _dim; desc.style.fontSize = 24; desc.style.marginTop = 5;
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
                track.style.height = 6; track.style.marginTop = 10; track.style.flexShrink = 0;
                track.style.backgroundColor = _theme.SurfaceHi; LvnChrome.Round(track, 3f); track.style.overflow = Overflow.Hidden;
                var fill = new VisualElement();
                fill.style.height = Length.Percent(100f);
                fill.style.width = Length.Percent(read * 100f);
                fill.style.backgroundColor = _accent; LvnChrome.Round(fill, 3f);
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
