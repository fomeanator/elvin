using System.Collections.Generic;
using System.Globalization;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ОБЛИК «СЦЕНА» ГЛАВНОЙ — макет партнёра (Figma «main screen»): героиня
    /// слева во весь рост, справа столбик — панель сообщений, карточка
    /// текущей новеллы с прогрессом и кнопка награды за рекламу; снизу
    /// рисованное меню с большой круглой кнопкой по центру.
    ///
    /// <para>Включается ДАННЫМИ: <c>ui.browse.skin</c> — адрес папки с артом
    /// рамок (nav.png, panel.png, card-back.png, card-front.png, adv.png,
    /// plus.png). Рамки со свечением, срезами и бликами код рисовать не
    /// должен — это рисунок, а не приём (см. <see cref="LvnTheme.DialogueFrame"/>).
    /// Всё, что живёт, — подписи, обложка, прогресс, число награды — остаётся
    /// живым: слова из словаря, арт из манифеста, цифры из кошелька.</para>
    ///
    /// <para>Героиню и полотно за ней рисует СЦЕНА (<c>ui.browse.canvas</c>,
    /// кукла гардероба) — здесь только то, что лежит поверх.</para>
    ///
    /// <para>Размеры — в единицах МАКЕТА (390 dp шириной) через один
    /// множитель: числа макета читаются как есть, а холст панели 1080 их
    /// не искажает. Полки подборок этот облик не строит: витрина открывается
    /// с карточки и с панели сообщений («библиотека»).</para>
    /// </summary>
    public sealed partial class BrowseHub
    {
        // Размеры, картинки, подписи и кнопки — у НАБОРА ДЕТАЛЕЙ облика
        // (LvnStageKit): те же детали ждут магазин и профиль. Здесь только
        // короткие окна в него и то, что знает один хаб, — папку арта.
        private static float D(float dp) => LvnStageKit.D(dp);
        private static T At<T>(T el, float x, float y, float w, float h) where T : VisualElement
            => LvnStageKit.At(el, x, y, w, h);

        /// <summary>Облик «сцена» включён — арт рамок назван.</summary>
        private bool Staged => !string.IsNullOrEmpty(_cfg?.skin);

        /// <summary>Площадка рекламы за награду (<c>ui.store.ad_placement</c>) —
        /// кладёт хост; пусто — кнопки награды нет.</summary>
        public string AdPlacement;

        /// <summary>Игрок нажал значок награды за ролик. Пусто — витрина
        /// показывает ролик сама (так было до экрана награды).</summary>
        public System.Action OnAdTap;

        /// <summary>Игрок открыл крутки (TR-47). Пусто — кнопки нет.</summary>
        public System.Action OnSpin;
        private VisualElement _stageSpin;

        private VisualElement _stageStack, _stageCard, _stageAd, _stageCover, _stageFill;
        private Label _stageChapter, _stageTitle, _stageSubtitle, _stageAdAmount;
        private LvnTitle _stageFeatured;

        // Склейка адреса — у набора деталей: тем же правилом бут греет рамки.
        private string SkinUrl(string file) => LvnStageKit.SkinUrl(_cfg.skin, file);

        private VisualElement StageImage(string file, float x, float y, float w, float h)
            => LvnStageKit.Art(SkinUrl(file), _assets, x, y, w, h);
        private static Label StageLabel(System.Func<string> text, float size, Color color, bool medium = false)
            => LvnStageKit.Text(text, size, color, medium);
        private static VisualElement StageButton(System.Func<string> text, System.Action onTap)
            => LvnStageKit.Button(text, onTap);

        // ── страница ─────────────────────────────────────────────────────────

        /// <summary>Страница главной в облике «сцена»: столбик справа,
        /// прижатый к низу над рисованным меню. Полотно и героиня — без
        /// вуалей.</summary>
        private VisualElement BuildStageView()
        {
            // ПАСПОРТ ОБЛИКА — ПЕРЕД СБОРКОЙ. Размеры рамок и столбиков берутся
            // из манифеста, а не из констант кода: другой арт описывают рядом
            // с артом. Зов идёт от каждого экрана облика, поэтому порядок их
            // появления роли не играет.
            LvnStageSkin.Apply(_cfg?.skin_metrics);
            var view = new VisualElement { pickingMode = PickingMode.Ignore };
            ScreenUi.Stretch(view);

            // ВУАЛЕЙ НЕТ. Здесь стояли две тени — сверху (130) под шапкой и
            // снизу (197) над меню, «героиня уходит в тень». С полотном,
            // которое теперь живёт и ездит с камерой, они читались как грязь
            // на картине, а не как подложка под текст («что за тень на
            // главной? надо убрать её» — Илья 08.09). Шапка и меню несут свой
            // фон сами.

            var stack = new VisualElement { pickingMode = PickingMode.Ignore };
            _stageStack = stack;
            stack.style.position = Position.Absolute;
            stack.style.right = D(LvnStageSkin.Home.Right);
            stack.style.bottom = D(LvnStageSkin.Home.Bottom);
            stack.style.width = D(LvnStageSkin.Home.Width);
            stack.style.flexDirection = FlexDirection.Column;
            stack.style.alignItems = Align.FlexEnd;
            stack.style.justifyContent = Justify.FlexEnd;
            view.Add(stack);

            stack.Add(StagePanel());
            stack.Add(StageGap());
            _stageCard = StageCard();
            stack.Add(_stageCard);
            stack.Add(StageGap());
            _stageAd = StageAdButton();
            stack.Add(_stageAd);

            // КРУТКИ (TR-47) — рядом с наградой за ролик: обе кнопки про то,
            // как получить валюту, не платя деньгами. Пункта нет, пока хозяин
            // не дал, чем его открыть: гача заводится сервером, и обещать её
            // без сервера незачем.
            if (OnSpin != null)
            {
                _stageSpin = LvnStageKit.Button(() => LvnWords.Of("gacha.title", "Spin"), () => OnSpin());
                _stageSpin.style.height = D(LvnStageSkin.Adv.Height);
                _stageSpin.style.marginTop = LvnTokens.Space1;
                LvnStageKit.HollowFrame(_stageSpin, SkinUrl("card-back.png"), _assets,
                                        LvnStageKit.CardBackW, LvnStageKit.CardBackH,
                                        LvnStageKit.CardBackCornerPx, LvnStageKit.CardBackPxPerDp,
                                        index: 0, solid: true);
                stack.Add(_stageSpin);
            }

            // Высота экрана известна только после раскладки — и меняется на
            // повороте; столбик подгоняется при каждой смене геометрии.
            view.RegisterCallback<GeometryChangedEvent>(_ => ApplyStageSafeArea());

            // Награда за рекламу: число приходит с сервера (каталог площадок)
            // и меняется по мере просмотров — слушаем, пока хаб на экране.
            Lvn.LvnLeash.WhileOnScreen(this,
                () => Lvn.Services.LvnAds.Changed += RefreshStageAd,
                () => Lvn.Services.LvnAds.Changed -= RefreshStageAd,
                RefreshStageAd);
            return view;
        }

        /// <summary>Зазор столбика: 33 dp по макету, на коротком экране
        /// ужимается, чтобы столбик не залез под шапку.</summary>
        private static VisualElement StageGap()
        {
            var gap = new VisualElement { pickingMode = PickingMode.Ignore };
            gap.style.height = D(33f);
            gap.style.minHeight = D(8f);
            gap.style.flexShrink = 1;
            return gap;
        }

        /// <summary>Панель сообщений: плашка-заголовок, строка состояния и
        /// кнопка. Ведёт в библиотеку — все новеллы одним списком.</summary>
        private VisualElement StagePanel()
        {
            var p = new VisualElement { pickingMode = PickingMode.Ignore };
            float pw = LvnStageSkin.Panel.Width, ph = LvnStageSkin.Panel.Height;
            p.style.width = D(pw); p.style.height = D(ph);
            p.style.marginRight = D(2f);
            p.style.flexShrink = 0;
            p.Add(StageImage("panel.png", 0f, 0f, D(pw), D(ph)));

            var head = LvnStageKit.Plaque(() => LvnWords.Pick("hub.news", _cfg.news_title, "News"));
            At(head, 0f, 0f, D(pw), D(28f));
            p.Add(head);

            var body = StageLabel(
                () => LvnWords.Pick("hub.news_empty", _cfg.news_empty_text, "No new messages").ToUpperInvariant(),
                LvnTokens.TextBase, _dim, medium: true);
            At(body, 0f, D(43f), D(pw), D(28f));
            p.Add(body);

            var open = StageButton(() => LvnWords.Pick("hub.open", _cfg.open_text, "Open"), ShowLibrary);
            open.name = "stage-open-panel";
            At(open, D(25f), D(82f), D(150f), D(42f));
            p.Add(open);
            return p;
        }

        /// <summary>Все новеллы одним списком — куда ведёт панель, пока у
        /// сообщений нет своего экрана.</summary>
        private void ShowLibrary()
        {
            // КОМНАТА ВМЕСТО ВИДА. Если оболочка дала дверь в комнату списка —
            // идём туда: полотно едет, возврат работает. Старый вид внутри
            // хаба остаётся только там, где комнаты нет (обычная тема).
            if (OpenTitles != null) { OpenTitles(); return; }
            var all = new List<string>();
            foreach (var kv in _titles) all.Add(kv.Key);
            var lib = new LvnCollection
            {
                id = LibraryId,
                name = LvnWords.Pick("hub.library", _cfg.library_text, "Library"),
                titles = all,
            };
            ShowCollection(lib);
        }

        /// <summary>Карточка текущей новеллы: обложка в рамке, полоса глав,
        /// название, подзаголовок и кнопка. Рамка — двумя картинками: задник
        /// ПОД обложкой (иначе его полупрозрачная заливка затемнила бы арт),
        /// перед — поверх (кромка обложки, плашка, кнопка).</summary>
        private VisualElement StageCard()
        {
            var c = new VisualElement();
            float cw = LvnStageSkin.CardFront.Width, chh = LvnStageSkin.CardFront.Height;
            c.style.width = D(cw); c.style.height = D(chh);
            c.style.flexShrink = 0;
            c.Add(StageImage("card-back.png", -D(3f), D(7f),
                              D(LvnStageSkin.CardBack.Width), D(LvnStageSkin.CardBack.Height)));

            _stageCover = new VisualElement { pickingMode = PickingMode.Ignore };
            At(_stageCover, D(10f), D(19f), D(237f), D(131f));
            _stageCover.style.backgroundColor = _card;
            LvnChrome.Round(_stageCover, D(5f));
            _stageCover.style.overflow = Overflow.Hidden;
            LvnPicture.Fit(_stageCover);
            c.Add(_stageCover);

            c.Add(StageImage("card-front.png", 0f, 0f, D(cw), D(chh)));

            var head = LvnStageKit.Plaque(() =>
                _stageFeatured != null && LvnProgress.Current(_stageFeatured) != null
                    ? LvnWords.Pick("hub.continue", _cfg.continue_text, "Continue")
                    : LvnWords.Pick("hub.featured", _cfg.featured_text, "Featured"));
            At(head, D(23f), 0f, D(211f), D(28f));
            c.Add(head);

            // Полоса глав — деталь набора; ход двигает BuildStage.
            var row = ScreenUi.Row(new VisualElement { pickingMode = PickingMode.Ignore });
            At(row, D(17f), D(124f), D(224f), D(14f));
            var bar = LvnStageKit.Progress(out _stageFill);
            bar.style.flexGrow = 1;
            row.Add(bar);
            _stageChapter = StageLabel(() => ChapterCounter(_stageFeatured), LvnTokens.TextSm, _text);
            _stageChapter.style.marginLeft = D(14f);
            _stageChapter.style.flexShrink = 0;
            row.Add(_stageChapter);
            c.Add(row);

            // Название и подзаголовок — столбиком: длинное имя переносится, и
            // подпись под ним съезжает следом, а не ложится поверх.
            var caption = new VisualElement { pickingMode = PickingMode.Ignore };
            caption.style.position = Position.Absolute;
            caption.style.left = D(12f); caption.style.top = D(162f); caption.style.width = D(234f);
            _stageTitle = StageLabel(() => _stageFeatured == null ? string.Empty
                : LvnWords.Name("title", _stageFeatured.id, _stageFeatured.name), LvnTokens.TextXl, LvnTokens.Gold);
            _stageTitle.style.whiteSpace = WhiteSpace.Normal;
            caption.Add(_stageTitle);
            _stageSubtitle = StageLabel(() => _stageFeatured == null ? string.Empty
                : LvnWords.Name("subtitle", _stageFeatured.id, _stageFeatured.subtitle ?? ""), LvnTokens.TextXs, LvnTokens.Silver);
            _stageSubtitle.style.marginTop = D(6f);
            caption.Add(_stageSubtitle);
            c.Add(caption);

            var open = StageButton(() => LvnWords.Pick("hub.open", _cfg.open_text, "Open"), OpenFeatured);
            open.name = "stage-open-card";
            At(open, D(54f), D(213f), D(150f), D(42f));
            c.Add(open);

            // Вся карточка — дверь в деталь, как и кнопка на ней.
            c.AddManipulator(new Clickable(OpenFeatured));
            return c;
        }

        private void OpenFeatured()
        {
            var t = _stageFeatured;
            if (t == null) return;
            if (IsLocked(t)) { FireLockedHint(LvnWords.Name("title", t.id, t.name), t.locked_hint ?? ""); return; }
            OpenDetail(t, CurrentCollectionOf(t));
        }

        /// <summary>«Глава 5/12»: досягнутая глава против всех. Слово главы —
        /// у подписей (<c>ui.chapter_word</c>), как в списке глав.</summary>
        private static string ChapterCounter(LvnTitle t)
        {
            if (t == null) return string.Empty;
            int total = t.ChaptersOf().Count;
            if (total <= 0) return string.Empty;
            int reached = Mathf.Clamp(LvnProgress.Reached(t), 1, total);
            var word = string.IsNullOrEmpty(LvnCaptions.ChapterWord) ? LvnCaptions.DefaultChapterWord : LvnCaptions.ChapterWord;
            return $"{word} {reached}/{total}";
        }

        /// <summary>Кнопка награды за рекламу: «+10 ◆». Число — с сервера
        /// (каталог площадок), показ — через дом рекламы.</summary>
        private VisualElement StageAdButton()
        {
            var b = new VisualElement { name = "stage-ad" };
            float aw = LvnStageSkin.Adv.Width, ah = LvnStageSkin.Adv.Height;
            b.style.width = D(aw); b.style.height = D(ah);
            b.style.flexShrink = 0;
            b.Add(StageImage("adv.png", 0f, 0f, D(aw), D(ah)));
            _stageAdAmount = StageLabel(null, LvnTokens.TextLg, LvnTokens.Gold, medium: true);
            At(_stageAdAmount, D(45f), D(9f), D(36f), D(22f));
            // Число прижато к значку видео слева; кристалл нарисован правее.
            _stageAdAmount.style.unityTextAlign = TextAnchor.MiddleLeft;
            b.Add(_stageAdAmount);
            // НАЖАТИЕ ВЕДЁТ НА ЭКРАН, А НЕ СРАЗУ В РОЛИК. Прямой показ не
            // говорил игроку ни что он получит, ни сколько показов осталось,
            // ни почему ничего не случилось, когда ролика не оказалось: значок
            // просто молчал («в главном меню по нажатию переход», «щас вообще
            // не срабатывает» — Илья 09.09). Разговор ведёт хозяин, витрина
            // только сообщает о нажатии.
            b.AddManipulator(new Clickable(() =>
            {
                if (string.IsNullOrEmpty(AdPlacement)) return;
                if (OnAdTap != null) { OnAdTap(); return; }
                LvnAsync.Fire(Lvn.Services.LvnAds.WatchAndRewardAsync(AdPlacement), "StageAd");
            }));
            LvnMotion.Tappable(b);
            return b;
        }

        private void RefreshStageAd()
        {
            if (_stageAd == null) return;
            var st = string.IsNullOrEmpty(AdPlacement) ? null : Lvn.Services.LvnAds.StateOf(AdPlacement);
            bool show = Lvn.Services.LvnAds.Available && st != null && st.Amount > 0;
            _stageAd.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (show && _stageAdAmount != null) _stageAdAmount.text = "+" + st.Amount.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Наполнить облик данными: кто на карточке, её арт и
        /// прогресс. Зовётся вместо сборки полок при каждой смене данных.</summary>
        private void BuildStage()
        {
            if (_stageCard == null) return;
            var resume = ResumableTitle();
            var featured = resume ?? FirstTitle();
            if (featured == null)
            {
                var orphans = OrphanTitles();
                if (orphans.Count > 0) _titles.TryGetValue(orphans[0], out featured);
            }
            _stageFeatured = featured;
            _stageCard.style.display = featured == null ? DisplayStyle.None : DisplayStyle.Flex;
            if (featured == null) return;
            var art = featured.CardArt();
            // ЖИВОЙ СПАЙН ВМЕСТО ОБЛОЖКИ — тем же приёмом, что в карточках
            // ленты: постер вешает фигуру фоном на элемент интерфейса. Облик
            // «сцена» про это не знал и ставил статичную картинку, хотя
            // спайн у новеллы есть (просьба Ильи 08.09: «спайн в главное
            // меню вместо бг новеллы»).
            //
            // ОБЛОЖКУ ПРИ ЭТОМ НЕ СТАВИМ: и она, и спайн пишут ОДНО поле
            // backgroundImage, и обе едут асинхронно — приехавшая позже
            // обложка молча затёрла бы фигуру. Не сложился спайн — постер
            // сам откатится на обложку (последний довод Attach).
            var cardSpine = Lvn.UI.LvnSpineBridge.Available ? SpineForTitle(featured) : null;
            if (cardSpine != null)
                Lvn.UI.LvnSpinePoster.Attach(_stageCover, cardSpine,
                    url => _assets.LoadTextAsync(url, default),
                    url => _assets.LoadSpriteAsync(url, default),
                    (_assets as Lvn.UI.CachingAssets)?.Loader,
                    () => { if (!string.IsNullOrEmpty(art)) LvnPicture.Photo(_stageCover, art, _assets); });
            else if (!string.IsNullOrEmpty(art)) LvnPicture.Photo(_stageCover, art, _assets);
            int total = featured.ChaptersOf().Count;
            float frac = total > 0 ? Mathf.Clamp01((float)Mathf.Clamp(LvnProgress.Reached(featured), 1, total) / total) : 0f;
            LvnStageKit.Fill(_stageFill, frac);
            foreach (var l in new[] { _stageTitle, _stageSubtitle, _stageChapter })
                Lvn.UI.LvnRedress.Refresh(l);
            Lvn.UI.LvnRedress.All(_stageCard);
            if (!string.IsNullOrEmpty(AdPlacement))
                LvnAsync.Fire(Lvn.Services.LvnAds.GetCatalogAsync(), "StageAdCatalog");
        }

        /// <summary>Домашняя полоса телефона в макете: нижние 34 dp кадра
        /// нарисованы внутри меню. Вырез меньше — меню остаётся высотой макета;
        /// больше — растёт на разницу, и столбик поднимается вместе с ним.</summary>
        private static float StageHomeBarDp => LvnStageSkin.HomeBar;
        /// <summary>Шапка макета: ряд 32 dp плюс логотип, свисающий под него
        /// (блок 58 dp), плюс воздух до столбика.</summary>
        private const float StageTopBlockDp = 58f + 12f;
        /// <summary>Столбик без воздуха: панель, карточка, кнопка награды и
        /// два минимальных зазора — ниже он не ужмётся, дальше только масштаб.</summary>
        private const float StageStackMinDp = 124f + 255f + 39f + 2f * 8f;
        /// <summary>Ниже этого столбик не масштабируем: подписи перестают читаться.</summary>
        private const float StageMinScale = 0.55f;

        /// <summary>
        /// АДАПТИВ ОБЛИКА «СЦЕНА»: макет нарисован под один телефон (390×844),
        /// а экраны разные — 16:9, 20:9, планшет 4:3.
        ///
        /// <para>Ширина у панели всегда 1080 единиц (match width), поэтому по
        /// горизонтали макет ложится как есть. Меняется высота, и вот как её
        /// принимает столбик справа: он стоит от нижнего меню до шапки, и на
        /// короткой высоте сначала ужимаются зазоры (33 → 8 dp), а если и так
        /// не влезает — столбик масштабируется от своего нижнего правого угла,
        /// чтобы верхняя панель не заехала под логотип. Рисованное меню и
        /// шапка считают от вырезов, но не короче, чем нарисовано в макете.
        /// Кукла — у сцены (<see cref="LvnMenuStage.DollHeightOnScreen"/>).</para>
        /// </summary>
        private void ApplyStageSafeArea()
        {
            float inset = Mathf.Max(LvnEdges.Bottom(this), D(StageHomeBarDp));
            if (_bottomNav != null)
            {
                _bottomNav.style.paddingBottom = 0;
                _bottomNav.style.height = D(146f - StageHomeBarDp) + inset;
            }
            // Внутренние страницы (подборка, деталь) начинаются ПОД шапкой
            // облика: её логотип свисает ниже ряда, и заголовок страницы
            // ложился под аватар.
            float top = LvnEdges.Top(this) + D(StageTopBlockDp);
            if (_collectionView != null) _collectionView.style.paddingTop = top;
            if (_detailView != null) _detailView.style.paddingTop = top;
            if (_stageStack == null) return;
            float bottom = LvnStageKit.BottomAboveBar(this, LvnStageSkin.Home.Bottom);
            _stageStack.style.bottom = bottom;
            _stageStack.style.top = top;
            float viewH = resolvedStyle.height;
            if (float.IsNaN(viewH) || viewH <= 1f) return;   // до первой раскладки
            float avail = viewH - top - bottom;
            float need = D(StageStackMinDp);
            float scale = avail >= need ? 1f : Mathf.Max(StageMinScale, avail / need);
            _stageStack.style.scale = new Scale(new Vector2(scale, scale));
            _stageStack.style.transformOrigin = new TransformOrigin(Length.Percent(100f), Length.Percent(100f));
        }

        // ── нижнее меню ──────────────────────────────────────────────────────

        /// <summary>Рисованное нижнее меню: гардероб слева, магазин справа,
        /// по центру большая круглая кнопка «домой» с подписью-состоянием.
        /// Значки нарисованы в самой картинке; живут только слова.</summary>
        private VisualElement StageNav()
        {
            // Коробка меню выше нарисованной полосы: над ней в тех же 146 dp
            // стоит кольцо, а кнопка награды из столбика заходит в её верх.
            // Коробка тапы НЕ ловит — иначе она глотала бы нажатия по кнопке
            // награды (тур 08.09: до кнопки доходил только отпуск). Ловят
            // нарисованная полоса (floor), вкладки и кольцо.
            var nav = new VisualElement { pickingMode = PickingMode.Ignore };
            _bottomNav = nav;
            nav.style.height = D(146f);
            nav.style.flexShrink = 0;
            // ПОД ЛЕНТОЙ — СЦЕНА, А НЕ ЧЁРНОТА. Здесь стояла заливка цветом
            // фона от 74 dp до низа: рисунок меню ниже своей панели прозрачен,
            // и полосу «домашней кнопки» закрашивали, чтобы она читалась как
            // продолжение меню. На живом экране это ровно наоборот — чёрный
            // язык под рисованной лентой, оборванный ровной кромкой («там ещё
            // чернота под нижним меню, надо чтобы фон туда заезжал внутрь до
            // низа экрана самого» — Илья 08.09). Полотно витрины идёт во весь
            // экран и само доходит до нижней кромки; ленте достаточно своего
            // рисунка.
            // Ниже 74 dp рисунок прозрачен, но ПОЛОСА ЛОВИТ ТАПЫ: под ней едут
            // страницы вкладок, и щель между кнопками не должна пропускать
            // нажатие к магазину. Заливки нет — полотно видно до низа.
            var floor = new VisualElement();
            floor.style.position = Position.Absolute;
            floor.style.left = 0; floor.style.right = 0; floor.style.top = D(74f); floor.style.bottom = 0;
            floor.style.backgroundColor = Color.clear;
            nav.Add(floor);
            var art = new VisualElement { name = "stage-img", pickingMode = PickingMode.Ignore };
            art.style.position = Position.Absolute;
            art.style.left = -D(12f); art.style.right = -D(12f); art.style.top = -D(12f);
            art.style.height = D(170f);
            LvnPicture.Skin(art, SkinUrl("nav.png"), _assets, what: "StageSkin");
            nav.Add(art);

            var ink = Color.Lerp(_dim, _bg, 0.45f);
            nav.Add(StageTab(LvnTabs.Wardrobe, left: true, ink));
            nav.Add(StageTab(LvnTabs.Store, left: false, ink));

            // Центр: круг нарисован; слово и подпись состояния — живые.
            var home = new VisualElement { name = "stage-tab-home" };
            At(home, 0f, -D(20f), D(142f), D(162.5f));
            home.style.left = Length.Percent(50f);
            home.style.marginLeft = -D(71f);
            var word = StageLabel(() => LvnTabs.Label(LvnTabs.Home, _cfg).ToUpperInvariant(),
                LvnTokens.TextLg, LvnTokens.Gold, medium: true);
            At(word, 0f, D(89.5f), D(142f), D(18f));
            home.Add(word);
            // Подпись состояния уже круга: две короткие строки внутри кольца,
            // как в макете, а не одна длинная поверх его кромки.
            var hint = StageLabel(() => LvnWords.Pick("hub.nav_home_hint", _cfg.nav_home_hint, ""),
                LvnTokens.TextXs, LvnTokens.Silver);
            At(hint, D(21f), D(108.7f), D(100f), D(26f));
            hint.style.whiteSpace = WhiteSpace.Normal;
            home.Add(hint);
            var goHome = TabAction(LvnTabs.Home);
            if (goHome != null) { home.AddManipulator(new Clickable(goHome)); LvnMotion.Tappable(home); }
            nav.Add(home);
            // Центральная кнопка в ряду вкладок числится, но красится своим
            // золотом, а не подсветкой активной вкладки.
            _navTabs.Add(new TabRef { Index = LvnTabs.Home, Root = home, Painted = LvnTokens.Gold });
            SetActiveTab(0, instant: true);
            return nav;
        }

        private VisualElement StageTab(int index, bool left, Color ink)
        {
            var tab = new VisualElement { name = "stage-tab-" + index };
            tab.style.position = Position.Absolute;
            tab.style.top = D(80f); tab.style.bottom = 0;
            tab.style.width = D(150f);
            if (left) tab.style.left = D(9.5f); else tab.style.right = D(9.5f);
            tab.style.justifyContent = Justify.FlexEnd;
            tab.style.alignItems = Align.Center;
            tab.style.paddingBottom = D(13f);
            var lb = StageLabel(() => _theme.Heading(NavLabel(index)), LvnTokens.TextSm, ink);
            tab.Add(lb);
            var act = TabAction(index);
            if (act != null) { tab.AddManipulator(new Clickable(act)); LvnMotion.Tappable(tab); }
            _navTabs.Add(new TabRef { Index = index, Root = tab, Label = lb, Painted = ink });
            return tab;
        }
    }
}
