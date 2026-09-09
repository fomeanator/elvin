using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ДЕТАЛЬ НОВЕЛЛЫ — ПОПАП В ОБЛИКЕ «СЦЕНА».
    ///
    /// <para>Деталь была полноэкранной страницей прежней темы: непрозрачная
    /// Полночь, обложка во весь верх, главы строками, синяя кнопка. Рядом
    /// главная, гардероб и магазин уже переодеты, и тап по карточке выбрасывал
    /// игрока из витрины в другое приложение («надо переделать попап с
    /// информацией детальной» — Илья 09.09).</para>
    ///
    /// <para>Теперь это лист ПОВЕРХ витрины: скрим, тап мимо — закрыть, рамка
    /// облика со своей серединой (как у гардероба и профиля). Внутри — та же
    /// карточка, что на главной: задник и перёд рамки, обложка в окне, плашка
    /// состояния, полоса глав, название золотом. Главы — плашками, золото у
    /// текущей. Кнопки — плашки витрины, «Играть» с золотой гранью.</para>
    ///
    /// <para>Логика не тронута: те же Play/Cancel/ShowRestartMenu, тот же
    /// Rebuild; облик только меняет, чем строится каждая часть.</para>
    /// </summary>
    public sealed partial class TitleDetailScreen : ILvnContentAware
    {
        private string _skin;
        private VisualElement _sheet;
        private bool _stageSheet;
        private bool StageDressed => !string.IsNullOrEmpty(_skin);
        private static float D(float dp) => LvnStageKit.D(dp);
        private string SkinUrl(string file) => LvnStageKit.SkinUrl(_skin, file);

        public void SetContent(LvnManifest manifest)
            => LvnStageKit.TakeSkin(manifest, ref _skin, () => { StageSheet(); Rebuild(); });

        /// <summary>Страница становится листом: корень — скрим, содержимое
        /// переезжает в лист с рамкой облика. Делается один раз.</summary>
        private void StageSheet()
        {
            if (!StageDressed || _stageSheet) return;
            _stageSheet = true;

            style.backgroundColor = LvnTokens.Veil(0.55f);
            style.backgroundImage = new StyleBackground(StyleKeyword.None);
            pickingMode = PickingMode.Position;
            RegisterCallback<ClickEvent>(e => { if (e.target == this) Cancel(); });

            var sheet = _sheet = new VisualElement();
            LvnStageKit.SheetFrame(sheet, this, tab: false);   // поля, верх и низ — по паспорту
            sheet.style.overflow = Overflow.Hidden;
            sheet.pickingMode = PickingMode.Position;
            LvnStageKit.GlassSheet(sheet, _skin, _assets, LvnTokens.Radius);

            _scroll.RemoveFromHierarchy();
            _actionBar.RemoveFromHierarchy();
            sheet.Add(_scroll);
            sheet.Add(_actionBar);
            Add(sheet);
        }

        /// <summary>Края листа — по паспорту, с учётом вырезов экрана.</summary>
        private void StageSafeArea()
        {
            if (_sheet != null) LvnStageKit.SheetEdges(_sheet, this, tab: false);
        }

        /// <summary>Обложка — той же карточкой, что на главной. Кнопка «назад»
        /// живёт здесь же: карточка пересобирается в каждом Rebuild, и кнопка с
        /// ней, без дублей.</summary>
        private VisualElement BuildStageHero()
        {
            float cw = LvnStageSkin.CardFront.Width, ch = LvnStageSkin.CardFront.Height;
            var wrap = new VisualElement();
            wrap.style.flexShrink = 0;
            wrap.style.alignItems = Align.Center;
            wrap.style.paddingTop = D(6f);

            var c = new VisualElement();
            c.style.width = D(cw); c.style.height = D(ch);
            c.style.flexShrink = 0;
            c.Add(LvnStageKit.Art(SkinUrl("card-back.png"), _assets, -D(3f), D(7f),
                                  D(LvnStageSkin.CardBack.Width), D(LvnStageSkin.CardBack.Height)));

            var cover = new VisualElement { pickingMode = PickingMode.Ignore };
            LvnStageKit.At(cover, D(10f), D(19f), D(237f), D(131f));
            cover.style.backgroundColor = LvnTokens.Surface;
            LvnChrome.Round(cover, D(5f));
            cover.style.overflow = Overflow.Hidden;
            LvnPicture.Fit(cover);
            var art = ShownHero;
            if (!string.IsNullOrEmpty(art)) LvnPicture.Photo(cover, art, _assets);
            c.Add(cover);

            c.Add(LvnStageKit.Art(SkinUrl("card-front.png"), _assets, 0f, 0f, D(cw), D(ch)));

            var head = LvnStageKit.Plaque(() => LvnProgress.Current(Title) != null
                ? LvnWords.Of("hub.continue", "Continue")
                : LvnWords.Of("hub.open", "Open"));
            LvnStageKit.At(head, D(23f), 0f, D(211f), D(28f));
            c.Add(head);

            var row = ScreenUi.Row(new VisualElement { pickingMode = PickingMode.Ignore });
            LvnStageKit.At(row, D(17f), D(124f), D(224f), D(14f));
            var bar = LvnStageKit.Progress(out var fill);
            bar.style.flexGrow = 1;
            row.Add(bar);
            var chapters = Title.ChaptersOf();
            int total = chapters.Count;
            int reached = total > 0 ? Mathf.Clamp(LvnProgress.Reached(Title), 0, total) : 0;
            var counter = LvnStageKit.Text(() => $"{reached}/{total}", LvnTokens.TextSm, LvnTokens.Text);
            counter.style.marginLeft = D(14f);
            counter.style.flexShrink = 0;
            row.Add(counter);
            c.Add(row);
            LvnStageKit.Fill(fill, total > 0 ? (float)reached / total : 0f);

            var caption = new VisualElement { pickingMode = PickingMode.Ignore };
            caption.style.position = Position.Absolute;
            caption.style.left = D(12f); caption.style.top = D(162f); caption.style.width = D(234f);
            var name = LvnStageKit.Text(() => ShownName, LvnTokens.TextXl, LvnTokens.Gold);
            name.style.whiteSpace = WhiteSpace.Normal;
            caption.Add(name);
            var sub = LvnStageKit.Text(() => LvnWords.Name("subtitle", Title?.id, Title?.subtitle ?? ""),
                                       LvnTokens.TextXs, LvnTokens.TextDim);
            sub.style.marginTop = D(6f);
            caption.Add(sub);
            c.Add(caption);

            var play = LvnStageKit.Button(() => LvnWords.Of("hub.play", "Play"), Play);
            LvnStageKit.At(play, D(54f), D(213f), D(150f), D(42f));
            c.Add(play);

            wrap.Add(c);

            var back = new Button(Back) { text = "‹" };
            _backBtn = back;
            back.style.position = Position.Absolute;
            back.style.left = 0; back.style.top = 0;
            back.style.width = LvnTokens.Touch; back.style.height = LvnTokens.Touch;
            back.style.fontSize = LvnTokens.TextLg;
            LvnAir.PadY(back, 0);
            back.style.unityTextAlign = TextAnchor.MiddleCenter;
            LvnStyler.Plate(back, UiColor.WithAlpha(LvnTokens.PanelBg, 0.82f), LvnTokens.Gold, D(8f));
            wrap.Add(back);
            return wrap;
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

        /// <summary>Панель действий: «Начать заново» тихой плашкой, «Играть» —
        /// плашкой с золотой гранью, цена — плашкой рядом.</summary>
        private void StageActionBar(VisualElement bar)
        {
            bar.style.flexDirection = FlexDirection.Column;
            LvnAir.PadX(bar, 0f);
            bar.style.paddingTop = D(8f);
            // Лист уходит под ленту меню — кнопки стоят над её плотной частью.
            bar.style.paddingBottom = D(2f) + D(LvnStageSkin.Sheet.Under);
            bar.style.backgroundColor = Color.clear;
            LvnChrome.ClearBorder(bar);

            if (LvnProgress.Touched(Title))
            {
                var restart = StagePlateButton(() => LvnWords.Of("title.restart", "Start over"), ShowRestartMenu, primary: false);
                restart.style.marginBottom = D(6f);
                bar.Add(restart);
            }
            var row = ScreenUi.Row();
            bar.Add(row);
            var play = StagePlateButton(() => LvnWords.Of("hub.play", "Play"), Play, primary: true);
            play.style.flexGrow = 1;
            row.Add(play);

            var price = ShownPrice;
            if (price.Free) return;
            var cost = new VisualElement();
            cost.style.flexShrink = 0;
            cost.style.marginLeft = D(8f);
            LvnAir.Pad(cost, D(10f), D(6f));
            LvnStyler.Plate(cost, UiColor.WithAlpha(LvnTokens.PanelBg, 0.82f), LvnTokens.Text, D(6f));
            cost.Add(LvnPriceTag.Tag(price.Currency, price.Amount,
                new LvnPriceTag.Row { FontSize = 26f, IconSize = 22f, Gap = 6f }));
            row.Add(cost);
        }

        private VisualElement StagePlateButton(System.Func<string> text, System.Action onTap, bool primary)
        {
            var b = new VisualElement();
            b.style.height = D(42f);
            b.style.justifyContent = Justify.Center;
            b.style.alignItems = Align.Center;
            LvnStyler.Plate(b, UiColor.WithAlpha(LvnTokens.PanelBg, primary ? 0.94f : 0.6f),
                            primary ? LvnTokens.Gold : LvnTokens.TextDim, D(6f));
            if (primary) LvnStyler.Chosen(b, true, LvnTokens.Gold);
            var l = LvnStageKit.Text(() => (text() ?? string.Empty).ToUpperInvariant(), LvnTokens.TextBase,
                                     primary ? LvnTokens.Gold : LvnTokens.TextDim, medium: true);
            LvnFonts.Apply(l, LvnFonts.Display);
            b.Add(l);
            b.AddManipulator(new Clickable(onTap));
            LvnMotion.Tappable(b);
            return b;
        }
    }
}
