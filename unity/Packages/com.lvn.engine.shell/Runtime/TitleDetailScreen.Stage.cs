using System.Collections.Generic;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ДЕТАЛЬ НОВЕЛЛЫ — ПОПАП В ОБЛИКЕ «СЦЕНА» (макет «Выбор экспедиции», 17.09).
    ///
    /// <para>Деталь была полноэкранной страницей прежней темы, потом — листом
    /// с карточкой главной внутри. Макет рисует своё: лист 375×660 в
    /// светящейся рамке, лента-заголовок над верхней кромкой, крестик в углу,
    /// окно постера с ходом («Глава 5/12» или «Пройдено»), ниже название,
    /// «Мир экспедиции: 5» и эпоха, разделитель, описание, плашки жанров и
    /// статуса, второй разделитель; внизу три плашки: закладка, «Играть» и
    /// «заново».</para>
    ///
    /// <para>Главы, сохранения и статы в макете не нарисованы, а в игре
    /// нужны (главы — TR-63, сохранения — <c>detail_saves</c>). Они живут за
    /// закладкой: тап раскрывает их под вторым разделителем, лист
    /// прокручивается. «Заново» ведёт в прежнее меню перезапуска.</para>
    ///
    /// <para>Логика не тронута: те же Play/Cancel/ShowRestartMenu, тот же
    /// Rebuild; облик только меняет, чем строится каждая часть.</para>
    /// </summary>
    public sealed partial class TitleDetailScreen : ILvnContentAware
    {
        private string _skin;
        private VisualElement _sheet, _stageHero;
        private bool _stageSheet, _stageMore;
        private Dictionary<string, string> _genreColors;
        private bool StageDressed => !string.IsNullOrEmpty(_skin);
        private static float D(float dp) => LvnStageKit.D(dp);
        private string SkinUrl(string file) => LvnStageKit.SkinUrl(_skin, file);

        /// <summary>«Мир экспедиции: 5» — номер новеллы в её подборке; ставит
        /// хост из витрины. 0 — строка с номером не рисуется.</summary>
        public int WorldNumber;

        /// <summary>Раскрыты ли главы, сохранения и статы за закладкой.</summary>
        public bool StageMore => _stageMore;

        public void SetContent(LvnManifest manifest)
        {
            _genreColors = manifest?.ui?.browse?.genre_colors;
            LvnStageKit.TakeSkin(manifest, ref _skin, () => { StageSheet(); Rebuild(); });
        }

        /// <summary>Страница становится попапом: корень — затемнение, лист —
        /// рамка облика с окном, прокруткой и рядом плашек. Делается один раз.</summary>
        private void StageSheet()
        {
            if (!StageDressed || _stageSheet) return;
            _stageSheet = true;

            style.backgroundColor = LvnStageKit.Ink.Veil;
            style.backgroundImage = new StyleBackground(StyleKeyword.None);
            pickingMode = PickingMode.Position;
            RegisterCallback<ClickEvent>(e => { if (e.target == this) Cancel(); });

            var sheet = _sheet = new VisualElement { name = "stage-detail-sheet" };
            sheet.style.position = Position.Absolute;
            sheet.pickingMode = PickingMode.Position;
            StageEdges();
            LvnEdges.Follow(this, _ => StageEdges());
            LvnStageKit.Glow(sheet, _skin, _assets, ornamentH: 448f);

            _stageHero = new VisualElement { name = "stage-detail-window" };
            _stageHero.style.position = Position.Absolute;
            _stageHero.style.left = D(10f); _stageHero.style.right = D(9f);
            _stageHero.style.top = D(38f); _stageHero.style.height = D(191f);
            sheet.Add(_stageHero);

            _scroll.RemoveFromHierarchy();
            _scroll.style.position = Position.Absolute;
            _scroll.style.left = D(23f); _scroll.style.right = D(23f);
            _scroll.style.top = D(252f); _scroll.style.bottom = D(64f);   // над ярлыком цены
            sheet.Add(_scroll);
            // Край прокрутки тает в тон листа: на коротком экране текст уходит
            // под плашки, и резкий срез строки читался бы как ошибка вёрстки.
            var fade = new VisualElement { name = "stage-detail-fade", pickingMode = PickingMode.Ignore };
            fade.style.position = Position.Absolute;
            fade.style.left = D(23f); fade.style.right = D(23f);
            fade.style.bottom = D(64f); fade.style.height = D(28f);
            fade.style.backgroundImage = LvnBackdrop.Vertical(
                UiColor.WithAlpha(LvnStageKit.Ink.Bg, 0f), UiColor.WithAlpha(LvnStageKit.Ink.Bg, 0.9f), smooth: true);
            sheet.Add(fade);

            _actionBar.RemoveFromHierarchy();
            _actionBar.style.position = Position.Absolute;
            _actionBar.style.left = D(19f); _actionBar.style.right = D(17f);
            _actionBar.style.bottom = -D(13f); _actionBar.style.height = D(50f);
            ScreenUi.Row(_actionBar, spread: true);
            _actionBar.style.alignItems = Align.Center;
            sheet.Add(_actionBar);

            // Лента-заголовок: над верхней кромкой, по центру. Не кнопка.
            var ribbon = LvnStageKit.Plate(_skin, _assets, LvnStageSkin.Ribbon, "ribbon.png",
                () => LvnWords.Of("hub.detail_head", "Choose expedition"), 23.4f, null,
                name: "stage-detail-ribbon", ink: LvnStageKit.Ink.Ribbon, medium: false);
            ribbon.style.position = Position.Absolute;
            ribbon.style.top = -D(12f);
            ribbon.style.left = Length.Percent(50f);
            ribbon.style.translate = new Translate(Length.Percent(-50f), 0f);
            sheet.Add(ribbon);

            // Закрыть: крестик за правым верхним углом. Экспорт несёт своё
            // свечение — оттого запас 6.5 вокруг 36.
            var close = LvnStageKit.Art(SkinUrl("close.png"), _assets, 0f, 0f, D(36f), D(36f), bleed: 6.5f);
            close.name = "stage-detail-close";
            close.pickingMode = PickingMode.Position;
            close.style.left = StyleKeyword.Auto;
            close.style.right = -D(8f) - D(6.5f);
            close.style.top = -D(8f) - D(6.5f);
            close.AddManipulator(new Clickable(Back));
            LvnMotion.Tappable(close);
            sheet.Add(close);

            Add(sheet);
        }

        /// <summary>Края листа: ширина попапа макета по центру, верх под
        /// шапкой оболочки, низ над лентой меню — плашки внизу остаются
        /// нажимаемыми и на коротком экране.</summary>
        private void StageEdges()
        {
            if (_sheet == null) return;
            float side = Mathf.Max(0f, (LvnStageSkin.DesignWidth - LvnStageSkin.Popup.Width) * 0.5f);
            _sheet.style.left = D(side); _sheet.style.right = D(side);
            // Верх — под шапкой оболочки (она живёт поверх попапа): лента
            // заголовка выступает на 12 и не должна лечь на логотип.
            _sheet.style.top = LvnEdges.Top(this) + D(LvnStageSkin.Sheet.Top + 12f);
            _sheet.style.bottom = LvnStageKit.BottomAboveBar(this, LvnStageSkin.Sheet.Bottom);
        }

        private void StageSafeArea() => StageEdges();

        /// <summary>Собрать попап под открытую новеллу: окно с ходом, слова,
        /// плашки. Разделы глав/сохранений/статов — если раскрыты закладкой.</summary>
        private void RebuildStage()
        {
            StageSheet();

            // Окно: постер во всё окно, кромка поверх, ход в левом нижнем углу.
            _stageHero.Clear();
            var poster = new VisualElement { name = "stage-detail-poster", pickingMode = PickingMode.Ignore };
            LvnChrome.Stretch(poster);
            poster.style.overflow = Overflow.Hidden;
            LvnChrome.Round(poster, D(5f));
            LvnPicture.Fit(poster);
            var art = ShownHero;
            if (!string.IsNullOrEmpty(art)) LvnPicture.Photo(poster, art, _assets);
            _stageHero.Add(poster);
            LvnStageKit.Window(_stageHero, _skin, _assets);
            var course = LvnStageKit.Course(Title, _skin, _assets, textFirst: true);
            LvnStageKit.At(course, D(10f), D(154f), D(136f), D(27f));
            _stageHero.Add(course);

            // Слова: название, мир и эпоха, разделитель, описание, жанры,
            // статус, разделитель.
            _scroll.Clear();
            var body = new VisualElement { name = "stage-detail-body" };
            body.style.flexShrink = 0;
            var name = LvnStageKit.Para(() => ShownName.ToUpperInvariant(), 22f, LvnStageKit.Ink.Gold, medium: true);
            name.name = "stage-detail-name";
            body.Add(name);

            var meta = ScreenUi.Row(new VisualElement { name = "stage-detail-meta", pickingMode = PickingMode.Ignore });
            meta.style.marginTop = D(8f);
            meta.style.alignItems = Align.Center;
            if (WorldNumber > 0)
            {
                var world = ScreenUi.Row(new VisualElement { pickingMode = PickingMode.Ignore });
                world.style.alignItems = Align.Center;
                world.Add(LvnStageKit.Line(() => LvnWords.Of("hub.world", "Expedition world:"), 16f, LvnStageKit.Ink.Ochre));
                var num = LvnStageKit.Line(() => WorldNumber.ToString(), 16f, LvnStageKit.Ink.Ochre, medium: true);
                num.name = "stage-detail-world";
                num.style.marginLeft = D(4f);
                world.Add(num);
                meta.Add(world);
            }
            var era = LvnStageKit.Line(() => LvnWords.Name("subtitle", Title?.id, Title?.subtitle ?? ""), 16f, LvnStageKit.Ink.Ochre);
            era.name = "stage-detail-era";
            era.style.marginLeft = StyleKeyword.Auto;   // к правому краю
            if (string.IsNullOrEmpty(Title?.subtitle)) era.style.display = DisplayStyle.None;
            meta.Add(era);
            body.Add(meta);

            body.Add(Gap(14f));
            body.Add(LvnStageKit.Divider(_skin, _assets));
            body.Add(Gap(14f));

            var desc = LvnStageKit.Para(() => ShownSynopsis, 14f, LvnStageKit.Ink.Body);
            desc.name = "stage-detail-desc";
            body.Add(desc);

            if (Title?.genres != null && Title.genres.Count > 0)
            {
                var row = TagRow("hub.genre", "Story genre:");
                row.name = "stage-detail-genres";
                row.style.marginTop = D(14f);
                var chips = ScreenUi.Row(new VisualElement { pickingMode = PickingMode.Ignore });
                chips.style.marginLeft = StyleKeyword.Auto;
                foreach (var g in Title.genres)
                    if (!string.IsNullOrEmpty(g)) chips.Add(LvnStageKit.GenreChip(g, _genreColors));
                row.Add(chips);
                body.Add(row);
            }
            if (!string.IsNullOrEmpty(Title?.status))
            {
                var row = TagRow("hub.status", "Story status:");
                row.name = "stage-detail-status";
                row.style.marginTop = D(8f);
                var chip = LvnStageKit.StatusChip(() => Title.status);
                chip.style.marginLeft = StyleKeyword.Auto;
                row.Add(chip);
                body.Add(row);
            }

            body.Add(Gap(14f));
            body.Add(LvnStageKit.Divider(_skin, _assets));
            body.style.paddingBottom = D(12f);

            // За закладкой: статы, главы, сохранения — как и прежде в детали.
            if (_stageMore)
            {
                var more = new VisualElement { name = "stage-detail-more" };
                more.style.marginTop = D(12f);
                var stats = BuildStatsSection();
                if (stats != null) more.Add(stats);
                var chapters = BuildChaptersSection();
                if (chapters != null) more.Add(chapters);
                if (ShowSaves) more.Add(BuildSavesSection());
                body.Add(more);
                // Только что раскрыли — подвезти лист к разделу, иначе тап по
                // закладке снаружи ничем не отличим от промаха.
                if (_stageMoreFresh) _scroll.schedule.Execute(() => _scroll.ScrollTo(more)).ExecuteLater(60);
                _stageMoreFresh = false;
            }
            _scroll.Add(body);

            BuildStageActions(_actionBar);
        }

        private static VisualElement Gap(float dp)
        {
            var g = new VisualElement { pickingMode = PickingMode.Ignore };
            g.style.height = D(dp);
            g.style.flexShrink = 0;
            return g;
        }

        /// <summary>Строка «Жанр истории:» / «Статус истории:» — слово слева,
        /// плашки вызывающий прижимает вправо.</summary>
        private static VisualElement TagRow(string key, string fallback)
        {
            var row = ScreenUi.Row(new VisualElement { pickingMode = PickingMode.Ignore });
            row.style.alignItems = Align.Center;
            row.Add(LvnStageKit.Line(() => LvnWords.Of(key, fallback), 16f, LvnStageKit.Ink.Ribbon));
            return row;
        }

        /// <summary>Заголовок раздела — золотом, шрифтом витрины.</summary>
        private void StageHeader(VisualElement section)
        {
            if (!StageDressed || section == null || section.childCount == 0) return;
            if (section[0] is Label l)
            {
                l.style.color = LvnTokens.Gold;
                LvnFonts.Apply(l, LvnFonts.Display);
            }
        }

        /// <summary>Ряд главы — плашка тона панели; у текущей золотая грань,
        /// у пройденной — золотое слово, у закрытой — приглушённое.</summary>
        private VisualElement StageChapterRow(int no, string name, LvnChapterMark state)
        {
            bool locked = state == LvnChapterMark.Locked;
            bool current = state == LvnChapterMark.Current;
            var row = ScreenUi.Row();
            row.style.flexShrink = 0;
            row.style.marginTop = D(6f);
            LvnAir.Pad(row, D(10f), D(8f));
            LvnStyler.Plate(row, UiColor.WithAlpha(LvnTokens.PanelBg, 0.82f), LvnTokens.Text, D(8f));
            if (current) LvnStyler.Chosen(row, true, LvnTokens.Gold);

            var num = LvnStageKit.Text(() => no.ToString(), LvnTokens.TextSm,
                                       current ? LvnTokens.Gold : LvnTokens.TextDim, medium: true);
            num.style.width = D(24f);
            num.style.flexShrink = 0;
            num.style.marginRight = D(8f);
            num.style.unityTextAlign = TextAnchor.MiddleCenter;
            row.Add(num);

            var nameLbl = LvnStageKit.Text(() => name, LvnTokens.TextSm, locked ? LvnTokens.TextDim : LvnTokens.Text);
            nameLbl.style.flexGrow = 1;
            nameLbl.style.flexShrink = 1;
            nameLbl.style.overflow = Overflow.Hidden;
            nameLbl.style.textOverflow = TextOverflow.Ellipsis;
            nameLbl.style.whiteSpace = WhiteSpace.NoWrap;
            row.Add(nameLbl);

            var stateColor = state == LvnChapterMark.Done || current ? LvnTokens.Gold
                : state == LvnChapterMark.Open ? LvnTokens.Text : LvnTokens.TextDim;
            var stateLbl = LvnStageKit.Text(() =>
                state == LvnChapterMark.Done ? LvnWords.Of("chapter.done", "finished")
                : current ? LvnWords.Of("chapter.current", "current")
                : state == LvnChapterMark.Open ? LvnWords.Of("chapter.available", "available")
                : LvnWords.Of("chapter.locked", "locked"), LvnTokens.TextXs, stateColor);
            stateLbl.style.flexShrink = 0;
            stateLbl.style.marginLeft = D(8f);
            row.Add(stateLbl);

            row.style.opacity = locked ? 0.6f : 1f;
            return row;
        }

        /// <summary>Ряд плашек макета: закладка (главы и сохранения), «Играть»
        /// с ценой, если вход платный, и «заново» — тускнеет, пока нечего
        /// перезапускать.</summary>
        private void BuildStageActions(VisualElement bar)
        {
            bar.Clear();
            var more = LvnStageKit.IconPlate(_skin, _assets, LvnStageSkin.BtnIcon, "btn-icon.png",
                                             "icon-bookmark.png", 20f, ToggleMore, "stage-detail-more-btn");
            if (!_stageMore) more.style.opacity = 0.85f;
            bar.Add(more);

            var play = LvnStageKit.Plate(_skin, _assets, LvnStageSkin.BtnPlay, "btn-play.png",
                                         () => LvnWords.Of("hub.play", "Play"), 20f, Play, "stage-detail-play");
            // БЕСПЛАТНЫЙ ВХОД НЕ ПОКАЗЫВАЕТ ЦЕНУ: ярлык — только у платного,
            // в углу плашки, чтобы слово «Играть» осталось словом макета.
            var price = ShownPrice;
            if (!price.Free)
            {
                var cost = new VisualElement { name = "stage-detail-price", pickingMode = PickingMode.Ignore };
                cost.style.position = Position.Absolute;
                cost.style.top = -D(10f); cost.style.right = -D(6f);
                LvnAir.Pad(cost, D(6f), D(2f));
                cost.style.backgroundColor = LvnStageKit.Ink.ChipBg;
                LvnChrome.Frame(cost, D(4f), LvnStageKit.Ink.Edge, D(1f));
                cost.Add(LvnPriceTag.Tag(price.Currency, price.Amount,
                    new LvnPriceTag.Row { FontSize = D(12f), IconSize = D(12f), Gap = D(3f) }));
                play.Add(cost);
            }
            bar.Add(play);

            var again = LvnStageKit.IconPlate(_skin, _assets, LvnStageSkin.BtnIcon, "btn-icon.png",
                                              "icon-refresh.png", 20f, ShowRestartMenu, "stage-detail-restart");
            if (!LvnProgress.Touched(Title)) { again.SetEnabled(false); again.style.opacity = 0.45f; }
            bar.Add(again);
        }

        private bool _stageMoreFresh;

        private void ToggleMore()
        {
            _stageMore = !_stageMore;
            _stageMoreFresh = _stageMore;
            Rebuild();
        }
    }
}
