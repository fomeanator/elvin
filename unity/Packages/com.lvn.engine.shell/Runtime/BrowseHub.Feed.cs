using System.Collections.Generic;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ЛЕНТА ГЛАВНОЙ — что стоит на первой странице хаба и когда её
    /// пересобирать.
    ///
    /// <para>Витринная новелла сверху (продолжить начатую или позвать в первую),
    /// плитки сборников, полоса «Новеллы» для всего, что ни в один сборник не
    /// попало. Плюс отпечаток: лента пересобирается, только когда изменилось
    /// то, ЧТО она показывает, — иначе каждый возврат на главную стирал бы
    /// прокрутку под пальцем игрока.</para>
    ///
    /// <para>Тема уехала из <c>BrowseHub.cs</c> целиком и без правок: тот файл
    /// вырос до девятисот строк, и в нём соседствовали три разговора — поток
    /// выбора, оформление мелочей и вот эта лента.</para>
    /// </summary>
    public sealed partial class BrowseHub
    {
        // ── builders ──────────────────────────────────────────────────────────────
        // Отпечаток того, ЧТО лента показывает: сборники с их составом,
        // одиночные новеллы, витринная и её кнопка, плюс замки (они зависят от
        // глобальных статов, а те приезжают по сети). Совпал — пересобирать
        // нечего.
        private string TilesStamp()
        {
            var sb = new System.Text.StringBuilder();
            var resume = ResumableTitle();
            sb.Append(resume?.id).Append('|');
            sb.Append((ResumableTitle() ?? FirstTitle())?.id).Append('|');
            foreach (var c in _collections)
            {
                sb.Append(c?.id).Append(':');
                if (c?.titles != null)
                    foreach (var id in c.titles)
                    {
                        sb.Append(id);
                        if (_titles.TryGetValue(id, out var t)) sb.Append(IsLocked(t) ? '#' : '.');
                        sb.Append(',');
                    }
                sb.Append(';');
            }
            foreach (var id in OrphanTitles())
            {
                sb.Append(id);
                if (_titles.TryGetValue(id, out var t)) sb.Append(IsLocked(t) ? '#' : '.');
                sb.Append(',');
            }
            return sb.ToString();
        }
        private string _tilesStamp;

        private void BuildHubTiles()
        {
            // Облик «сцена» полок не строит — у него одна карточка и панель,
            // и наполняет их он сам (BrowseHub.Stage.cs).
            if (Staged) { BuildStage(); return; }
            if (_hubRows == null) return;
            // ЛЕНТА НЕ ПЕРЕСОБИРАЕТСЯ ВПУСТУЮ. Вход в хаб звал сборку дважды
            // (SetData и следом PickTitleAsync — «обновить замки по свежим
            // флагам»), и вторая сборка не просто повторяла работу: она заново
            // проигрывала ВХОДНУЮ АНИМАЦИЮ по уже видимому контенту. Игрок
            // видел, как хаб разок мигает на ровном месте.
            var stamp = TilesStamp();
            if (_tilesStamp == stamp) return;
            _tilesStamp = stamp;
            _hubRows.Clear();
            // Any title not curated into a collection (e.g. a freshly imported novel)
            // still shows — grouped into an auto "library" row so the hub reflects the
            // real content, not just the hand-authored shelves.
            var orphans = OrphanTitles();
            // Feature the title the player can CONTINUE, if any; else a recommended one.
            // Воздух сверху (Илья: «главную вниз, как гардероб») — лента
            // стартует под героиней и скроллится поверх неё. РАСТЯЖКА, а не
            // фикс: при коротком контенте воздух добирает всё свободное место
            // и прижимает ряды вниз к нижнему меню (Илья 27.08); при длинном —
            // сжимается до минимума в 30%.
            var air = new VisualElement { pickingMode = PickingMode.Ignore };
            air.style.minHeight = Length.Percent(30f);
            air.style.flexGrow = 1;
            air.style.flexShrink = 0;
            _hubRows.Add(air);
            // Баннер «Рекомендуем» убран по просьбе Ильи — такого блока нет.
            // Пустой сборник тоже полка: шапка, счётчик «0» и слово автора.
            for (int i = 0; i < _collections.Count; i++)
                _hubRows.Add(CollectionRow(_collections[i], hero: i == 0));
            if (orphans.Count > 0)
            {
                // ИМЕНИ У СЛУЖЕБНОГО СБОРНИКА НЕТ НАРОЧНО. Оно вычислялось
                // здесь строкой и застывало в модели: смена языка полосу
                // «Новеллы» не доставала — данные пересобирает не переодевание,
                // а перезаход в хаб. Как зовётся эта полоса, знает подпись
                // (см. CollectionRow), и знает по ключу.
                var lib = new LvnCollection { id = LibraryId, titles = orphans };
                _hubRows.Add(CollectionRow(lib, hero: _collections.Count == 0));
            }
            var cc = _hubRows.contentContainer;
            if (cc.childCount > 0) cc[cc.childCount - 1].style.marginBottom = LastRowLift;
            AnimateIn(_hubRows); // staggered entrance
        }

        /// <summary>
        /// НА СКОЛЬКО ПОСЛЕДНИЙ РЯД ПОДНЯТ НАД НИЖНИМ МЕНЮ.
        ///
        /// <para>Не ступень отступов и не должна ею быть: величину Илья
        /// подбирал лесенкой на живом экране (+20, +40, +50 — раньше ряд стоял
        /// вплотную и последняя карточка пряталась за меню). Поэтому у неё
        /// имя и объяснение, а не голое число в середине сборки: округлив её
        /// до ближайшей ступени, мы вернём ровно тот дефект, ради которого
        /// её и подбирали.</para>
        /// </summary>
        private const float LastRowLift = 110f;

        // Titles present in the manifest but not referenced by any collection —
        // preserves manifest order (dictionary order follows insertion in SetData).
        private List<string> OrphanTitles()
        {
            var inCol = new HashSet<string>();
            foreach (var c in _collections)
                if (c.titles != null)
                    foreach (var id in c.titles) inCol.Add(id);
            var orphans = new List<string>();
            foreach (var kv in _titles)
                if (!inCol.Contains(kv.Key)) orphans.Add(kv.Key);
            return orphans;
        }

        private LvnTitle FirstTitle()
        {
            foreach (var c in _collections)
                if (c.titles != null)
                    foreach (var id in c.titles)
                        if (_titles.TryGetValue(id, out var t)) return t;
            return null;
        }

        // The first title the player has an in-progress save for (LvnProgress) — the
        // "Продолжить" candidate for the featured banner. Null if nothing to resume.
        private LvnTitle ResumableTitle()
        {
            foreach (var c in _collections)
                if (c.titles != null)
                    foreach (var id in c.titles)
                        if (_titles.TryGetValue(id, out var t) && !IsLocked(t) && LvnProgress.Current(t) != null)
                            return t;
            return null;
        }

        // A large featured hero at the top of the feed — a recommended title with
        // its art, a Play button and the cost. Fallback: the first title.
        private VisualElement FeaturedBanner(LvnTitle t, bool resume = false)
        {
            bool locked = IsLocked(t);
            var b = new VisualElement();
            b.style.height = 370; b.style.flexShrink = 0; b.style.marginBottom = LvnEdges.PageSide;
            b.style.overflow = Overflow.Hidden;
            LvnChrome.Round(b, _radius + 2f);

            string art = t.CardArt();
            if (!string.IsNullOrEmpty(art))
            {
                var img = new VisualElement { pickingMode = PickingMode.Ignore };
                ScreenUi.Stretch(img); img.style.backgroundColor = _card;
                b.Add(img); LvnPicture.Photo(img, art, _assets);
                var scrim = new VisualElement { pickingMode = PickingMode.Ignore };
                ScreenUi.Stretch(scrim);
                scrim.style.backgroundImage = Gradient(new Color(0f, 0f, 0f, 0.05f), new Color(0.03f, 0.01f, 0.03f, 0.92f));
                b.Add(scrim);
            }
            else b.style.backgroundImage = PosterFallbackImage(useAccent: true);
            // У витринного кадра есть тонкая рамка, но не тяжёлая неоновая
            // обводка: контраст должен остаться у одной кнопки «Играть».
            LvnChrome.Border(b, UiColor.WithAlpha(_accent, 0.52f), 1f);

            b.style.justifyContent = Justify.FlexEnd;
            LvnAir.PadX(b, LvnTokens.Space4);
            b.style.paddingBottom = LvnTokens.Space4;

            bool res0 = resume;
            var eyebrow = ScreenUi.Eyebrow(() =>
                (res0 ? LvnWords.Pick("hub.continue", _cfg.continue_text, "Continue")
                      : LvnWords.Pick("hub.featured", _cfg.featured_text, "Featured")).ToUpperInvariant(),
                24f, _accent);
            eyebrow.style.marginBottom = LvnTokens.Space1;
            b.Add(eyebrow);
            var title = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Name("title", t.id, t.name));
            title.style.color = _text; title.style.fontSize = LvnTokens.TextDisplay; title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.whiteSpace = WhiteSpace.Normal; b.Add(title);

            var actions = new VisualElement();
            actions.style.flexDirection = FlexDirection.Row; actions.style.alignItems = Align.Center;
            actions.style.marginTop = LvnTokens.Space2;
            var play = new Button(() => { if (locked) { FireLockedHint(LvnWords.Name("title", t.id, t.name), t.locked_hint ?? ""); } else OpenDetail(t, CurrentCollectionOf(t)); })
            { };
            bool lock0 = locked, res1 = resume;
            Lvn.UI.LvnRedress.Bind(play, () =>
                lock0 ? LvnWords.Pick("hub.locked", _cfg.locked_text, "Locked")
                      : res1 ? LvnWords.Pick("hub.continue", _cfg.continue_text, "Continue")
                             : LvnWords.Pick("hub.play", _cfg.play_text, "Play"));
            play.style.fontSize = LvnTokens.TextLg;
            LvnAir.Pad(play, LvnTokens.Space4, LvnTokens.Space2);
            LvnStyler.Plate(play, _accent, _accentText, LvnTokens.RadiusSm);
            actions.Add(play);
            if (!locked && t.cost != null && t.cost.amount > 0)
            {
                var chip = CostChip(t.cost); chip.style.marginLeft = LvnTokens.Space2;
                actions.Add(chip);
            }
            b.Add(actions);
            return b;
        }










        // One collection as a streaming-style row: a header (name + "Все →") over
        // a horizontal slider of title cards. "Все →" opens the full list; a card
        // (or its "Подробнее") opens the detail.
        private LvnCollection CurrentCollectionOf(LvnTitle t)
        {
            foreach (var c in _collections)
                if (c.titles != null && c.titles.Contains(t.id)) return c;
            return null;
        }

        private string PlayLabel(LvnTitle t) =>
            t.cost != null && t.cost.amount > 0
                ? (LvnWords.Pick("hub.play", _cfg.play_text, "Play")) + "  ·  " + string.Format(LvnWords.Pick("hub.cost", _cfg.cost_text, "{0}"), t.cost.amount)
                : (LvnWords.Pick("hub.play", _cfg.play_text, "Play"));
    }
}
