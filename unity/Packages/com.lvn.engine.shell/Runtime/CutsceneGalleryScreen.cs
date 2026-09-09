using System.Collections.Generic;
using Lvn.Content;
using Lvn.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ГАЛЕРЕЯ КАТСЦЕН — сцены, которые игрок уже прожил.
    ///
    /// <para>Катсцена здесь — не ролик и не картинка, а ОТРЕЗОК СЦЕНАРИЯ,
    /// названный автором: <c>cutscene start Имя|id</c> … <c>cutscene end</c>.
    /// Карточка показывает превью (кадр, который стоял в начале сцены) и имя;
    /// тап переигрывает тот же отрезок с начала. Поэтому на сервере не
    /// появляется ни кадра, ни записи — только сценарий, который там и так
    /// лежит.</para>
    ///
    /// <para>Отличие от CG-галереи намеренное: та показывает АРТ (одна
    /// картинка на весь экран), эта — СЦЕНЫ (их играют). Общего у них только
    /// слово «галерея», поэтому и экраны разные.</para>
    /// </summary>
    public sealed class CutsceneGalleryScreen : LvnOverlayScreen, ILvnContentAware
    {
        /// <summary>Карточка галереи: адрес сцены и чем её показать.</summary>
        public sealed class Entry
        {
            public string Id;        // адрес карточки: «новелла:прохождение»
            public string TitleId;   // новелла — по ней ищется снимок кадра
            public string Key;       // адрес прохождения — под ним лежит снимок
            public string SceneId;   // метка сцены в сценарии — с неё её играют
            public string Name;      // авторское имя; на экране проходит через каталог перевода
            public string Chapter;
            public string Poster;    // адрес фона, если сцена его меняла
            public long At;          // когда прожито — различает повторы одной сцены
        }

        private readonly ILvnAssets _assets;
        private readonly List<Entry> _entries = new List<Entry>();
        private readonly VisualElement _sheet;
        private readonly Label _title;
        private readonly ScrollView _grid;
        private readonly Label _counter;
        private readonly Button _forget;
        private readonly Label _empty;
        private bool _stageGlass;

        /// <summary>ЧТО ИГРОК ВЫБРАЛ, когда экран закрылся. Не колбэк «играй
        /// сейчас»: закрытие экрана — анимация, и она возвращает интерфейс
        /// витрины ПОСЛЕ того, как сцена уже началась — катсцена шла поверх
        /// панелей главной («ui не скрывается» — Илья 09.09). Хост дожидается
        /// закрытия и только потом играет.</summary>
        public Entry Picked { get; private set; }

        /// <summary>Забыть прошлый выбор перед показом: экран живёт долго, и
        /// вчерашняя плитка не должна играть при следующем открытии.</summary>
        public void ClearPick() => Picked = null;

        /// <summary>СТЕРЕТЬ СОБРАННОЕ — зовётся вторым нажатием «Очистить».
        /// Само хранилище экран не трогает: собирает галерею хост (он один
        /// знает все новеллы каталога), он же и забывает. Пока хост не дал
        /// этого действия, кнопки нет — галерея не обещает того, чего не
        /// умеет.</summary>
        public System.Action OnForget;

        /// <summary>ВЫБРОСИТЬ ОДНУ КАРТОЧКУ — зовётся вторым нажатием корзины.
        /// Хранилище опять же не наше: экран показывает, хост помнит.</summary>
        public System.Action<Entry> OnDrop;

        private string _skin;
        private bool StageDressed => !string.IsNullOrEmpty(_skin);

        public CutsceneGalleryScreen(ILvnAssets assets)
        {
            _assets = assets;

            var sheet = _sheet = Sheet();
            AdoptSheet(sheet);

            var header = ScreenUi.Row();
            header.style.marginBottom = LvnTokens.Space3;
            sheet.Add(header);

            var back = ScreenUi.BackButton(Close, 52f, 36f);
            back.style.marginRight = LvnTokens.Space2;
            header.Add(back);

            var title = _title = Lvn.UI.LvnRedress.Bind(new Label(),
                () => LvnWords.Of("cutscenes.title", "Cutscenes"));
            LvnChrome.Heading(title);
            title.style.color = LvnTokens.Text;
            title.style.fontSize = LvnTokens.TextLg;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.flexGrow = 1;
            header.Add(title);

            _counter = new Label();
            _counter.style.color = LvnTokens.Gold;
            _counter.style.fontSize = LvnTokens.TextXs;
            LvnStyler.Chip(_counter, LvnTokens.Veil(0.35f), LvnTokens.Radius, padY: LvnTokens.Space1);
            header.Add(_counter);

            // ОЧИСТИТЬ — рядом со счётом, тихой плашкой: снос коллекции не
            // должен выглядеть привлекательнее самих сцен. Через тот же обряд
            // взведения, что и удаление аккаунта: пережитое необратимо, а
            // промах пальцем по шапке — обычное дело.
            _forget = new Button();
            _forget.style.fontSize = LvnTokens.TextXs;
            _forget.style.marginLeft = LvnTokens.Space2;
            LvnAir.Pad(_forget, LvnTokens.Space2, LvnTokens.Space1);
            LvnStyler.Plate(_forget, LvnTokens.Faint, LvnTokens.TextDim, LvnTokens.RadiusSm);
            Lvn.UI.LvnAskTwice.AskTwice(_forget,
                calm: () => LvnWords.Of("cutscenes.forget", "Clear"),
                armed: () => LvnWords.Of("cutscenes.forget_sure", "Erase all?"),
                confirmed: () => OnForget?.Invoke(),
                armedTint: LvnTokens.Bad);
            header.Add(_forget);

            _empty = Lvn.UI.LvnRedress.Bind(new Label(),
                () => LvnWords.Of("cutscenes.empty", "Scenes you have lived through appear here"));
            _empty.style.color = LvnTokens.TextDim;
            _empty.style.fontSize = LvnTokens.TextSm;
            _empty.style.unityTextAlign = TextAnchor.MiddleCenter;
            _empty.style.marginTop = LvnTokens.Space5;
            sheet.Add(_empty);

            _grid = Lvn.UI.LvnScroll.Vertical();
            _grid.style.flexGrow = 1;
            LvnFlow.Wrap(_grid.contentContainer, Justify.FlexStart);
            sheet.Add(_grid);
        }

        /// <inheritdoc cref="ILvnContentAware.SetContent"/>
        public void SetContent(LvnManifest manifest)
            => LvnStageKit.TakeSkin(manifest, ref _skin, () => { StageDress(); Rebuild(); });

        /// <summary>ОБЛИК «СЦЕНА»: лист — то же стекло в рисованной рамке, что
        /// у профиля и детали новеллы, заголовок — золотом. Одеваемся один раз:
        /// рамка рисуется картинкой, и второй слой лёг бы поверх первого.</summary>
        private void StageDress()
        {
            if (!StageDressed || _sheet == null || _stageGlass) return;
            _stageGlass = true;
            LvnStageKit.GlassSheet(_sheet, _skin, _assets, LvnTokens.Radius);
            _title.style.color = LvnTokens.Gold;   // заголовки витрины — золотом
        }

        /// <summary>Чем наполнить галерею. Порядок — как пришёл: игрок видит
        /// сцены в том порядке, в каком их прожил.</summary>
        public void SetEntries(IReadOnlyList<Entry> entries)
        {
            _entries.Clear();
            if (entries != null)
                foreach (var e in entries)
                    if (e != null && !string.IsNullOrEmpty(e.Id)) _entries.Add(e);
            Rebuild();
        }

        public override void Rebuild()
        {
            if (_grid == null) return;
            _grid.Clear();
            _counter.text = LvnWords.Of("cutscenes.counter", "{0} seen", _entries.Count.ToString());
            _counter.style.display = _entries.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            // Стирать нечего — и кнопки нет: пустая галерея не предлагает
            // опасного действия.
            _forget.style.display = _entries.Count > 0 && OnForget != null
                ? DisplayStyle.Flex : DisplayStyle.None;
            _empty.style.display = _entries.Count > 0 ? DisplayStyle.None : DisplayStyle.Flex;
            // ПОВТОРЫ ОДНОЙ СЦЕНЫ ПОДПИСЫВАЕМ ДАТОЙ. Пока карточка на сцену
            // одна, дата — лишний шум; как только рядом легло второе
            // прохождение, только она и отвечает, где какое.
            var times = new Dictionary<string, int>();
            foreach (var e in _entries)
            {
                var name = e.Name ?? e.SceneId ?? "";
                times.TryGetValue(name, out int n);
                times[name] = n + 1;
            }
            foreach (var e in _entries)
                _grid.Add(Tile(e, times.TryGetValue(e.Name ?? e.SceneId ?? "", out int c) && c > 1));
        }

        /// <summary>Во сколько раз карточка выше своей ширины. Кадр вертикальный,
        /// как короткое видео: сцену смотрят с телефона в руке, и горизонтальная
        /// плитка показывала полоску вместо кадра («покрупнее надо и как бы
        /// вертикальные, как шортсы» — Илья 09.09).</summary>
        private const float TileAspect = 16f / 9f;

        /// <summary>Карточка сцены: превью во всю плитку, имя полосой снизу.
        /// Превью может не быть (сцена началась раньше, чем доехал фон) — тогда
        /// плитка остаётся тонированной, но открывается так же.
        ///
        /// <para>Высоту считаем от живой ширины, а не числом: в UITK нет
        /// «сохранять пропорции», а колонок две — на узком экране и на широком
        /// плитка обязана остаться тем же кадром.</para></summary>
        private VisualElement Tile(Entry e, bool dated = false)
        {
            var cell = new VisualElement();
            cell.style.width = Length.Percent(48.5f);
            cell.style.marginRight = Length.Percent(1.5f);
            cell.style.marginBottom = LvnTokens.Space2;
            float lastW = 0f;
            cell.RegisterCallback<GeometryChangedEvent>(evt =>
            {
                float w = evt.newRect.width;
                if (w <= 0f || Mathf.Approximately(w, lastW)) return;
                lastW = w;
                cell.style.height = w * TileAspect;
            });
            cell.style.backgroundColor = LvnTokens.Surface;
            cell.style.overflow = Overflow.Hidden;
            // Плитка — карточка облика: та же рисованная рамка, что у карточек
            // новелл на главной, иначе галерея выпадает из витрины.
            if (StageDressed)
                LvnStageKit.HollowFrame(cell, LvnStageKit.SkinUrl(_skin, "card-back.png"), _assets,
                                        LvnStageKit.CardBackW, LvnStageKit.CardBackH,
                                        LvnStageKit.CardBackCornerPx, LvnStageKit.CardBackPxPerDp,
                                        index: 0, solid: true);
            else LvnChrome.Frame(cell, LvnTokens.RadiusSm, LvnTokens.Border, 1f);

            var art = ScreenUi.Stretch(new VisualElement());
            art.pickingMode = PickingMode.Ignore;
            LvnPicture.Fit(art);
            // cover: кадр главы горизонтальный, а плитка стоячая — картинку
            // заполняем и подрезаем, иначе в карточке останутся чёрные поля.
            // Сперва снимок сцены (его снял сам движок, когда фон внутри не
            // менялся), потом — адрес фона: снимок точнее, это её собственный кадр.
            var shot = !string.IsNullOrEmpty(e.TitleId) && !string.IsNullOrEmpty(e.Key)
                ? LvnCutsceneStore.LoadPoster(e.TitleId, e.Key) : null;
            if (shot != null)
            {
                LvnPicture.Fit(art, cover: true);
                art.style.backgroundImage = new StyleBackground(shot);
            }
            else if (!string.IsNullOrEmpty(e.Poster))
            {
                LvnPicture.Photo(art, e.Poster, _assets, cover: true);
            }
            cell.Add(art);

            var cap = new VisualElement();
            LvnChrome.BottomStrip(cap);
            LvnAir.Pad(cap, LvnTokens.Space2, LvnTokens.Space1);
            cap.style.backgroundColor = LvnTokens.Veil(0.55f);
            cap.pickingMode = PickingMode.Ignore;
            cell.Add(cap);

            // Имя переводится тем же каталогом, что и реплики: ключ — авторская
            // строка (её собирает `lvnconv locale`), поэтому английская сборка
            // покажет английское название той же сцены.
            var name = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of(e.Name, e.Name));
            name.style.color = LvnTokens.Text;
            name.style.fontSize = LvnTokens.TextXs;
            name.pickingMode = PickingMode.Ignore;
            cap.Add(name);

            if (dated && e.At > 0)
            {
                var when = new Label(System.DateTimeOffset.FromUnixTimeSeconds(e.At)
                                     .ToLocalTime().ToString("dd.MM"));
                when.style.color = LvnTokens.TextDim;
                when.style.fontSize = LvnTokens.TextXs;
                when.pickingMode = PickingMode.Ignore;
                cap.Add(when);
            }

            cell.AddManipulator(new Clickable(() => OpenArt(e)));
            LvnMotion.Tappable(cell);
            if (OnDrop != null) cell.Add(DropButton(e));
            return cell;
        }

        /// <summary>
        /// АРТ ВО ВЕСЬ ЭКРАН — то, зачем галерею открывают чаще всего.
        ///
        /// <para>Катсцена у нас переигрывается сценарием, и первое нажатие
        /// сразу уводило в сцену. Но в новеллах галерея — это прежде всего
        /// КАРТИНКА: «многим людям просто на картинку посмотреть хочется»
        /// (Илья 09.09, со слов партнёра). Поэтому тап раскрывает кадр, а
        /// пересмотр остаётся отдельной кнопкой под ним.</para>
        ///
        /// <para>Разворот живёт внутри экрана, а не отдельной ширмой: он ничего
        /// не грузит заново — тот же снимок и тот же адрес фона, что на
        /// плитке.</para>
        /// </summary>
        private void OpenArt(Entry e)
        {
            if (e == null || _art != null) return;

            var art = _art = new VisualElement();
            art.style.position = Position.Absolute;
            art.style.left = 0; art.style.right = 0; art.style.top = 0; art.style.bottom = 0;
            art.style.backgroundColor = new Color(0f, 0f, 0f, 0.96f);
            art.style.justifyContent = Justify.Center;

            var frame = new VisualElement();
            frame.style.flexGrow = 1;
            frame.style.marginBottom = LvnTokens.Space5;
            // contain, а не cover: кадр разглядывают целиком, и подрезать его
            // ради заполнения экрана значило бы прятать то самое, ради чего
            // разворот и открыли.
            LvnPicture.Fit(frame);
            var shot = !string.IsNullOrEmpty(e.TitleId) && !string.IsNullOrEmpty(e.Key)
                ? LvnCutsceneStore.LoadPoster(e.TitleId, e.Key) : null;
            if (shot != null) frame.style.backgroundImage = new StyleBackground(shot);
            else if (!string.IsNullOrEmpty(e.Poster)) LvnPicture.Photo(frame, e.Poster, _assets);
            art.Add(frame);

            var name = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of(e.Name, e.Name));
            name.style.color = LvnTokens.Text;
            name.style.fontSize = LvnTokens.TextBase;
            name.style.unityTextAlign = TextAnchor.MiddleCenter;
            name.style.marginBottom = LvnTokens.Space2;
            art.Add(name);

            var play = new Button(() => { Picked = e; CloseArt(); Close(); });
            Lvn.UI.LvnRedress.Bind(play, () => LvnWords.Of("cutscenes.play", "Play the moment"));
            play.style.fontSize = LvnTokens.TextSm;
            LvnAir.Pad(play, LvnTokens.Space4, LvnTokens.Space3);
            LvnAir.MarginX(play, LvnTokens.Space5);
            play.style.marginBottom = LvnTokens.Space5;
            LvnStyler.Plate(play, LvnTokens.Accent, Color.white, LvnTokens.Radius);
            art.Add(play);

            var back = ScreenUi.BackButton(CloseArt, 52f, 36f);
            back.style.position = Position.Absolute;
            back.style.top = LvnTokens.Space3;
            back.style.left = LvnTokens.Space2;
            art.Add(back);

            // Разворот вешаем на сам экран, а не на лист: лист — стекло в
            // рамке витрины, и кадр внутри него смотрелся бы открыткой в раме,
            // а не картинкой во весь экран.
            Add(art);
        }

        private void CloseArt()
        {
            if (_art == null) return;
            _art.RemoveFromHierarchy();
            _art = null;
        }

        private VisualElement _art;

        /// <summary>КОРЗИНА В УГЛУ КАРТОЧКИ. Первое нажатие взводит и краснеет,
        /// второе выбрасывает; тап мимо корзины по-прежнему открывает сцену —
        /// поэтому кнопка своя, а не жест по всей плитке. Обряд взведения тот
        /// же, что у удаления аккаунта: коллекция необратима.</summary>
        private Button DropButton(Entry e)
        {
            var bin = new Button();
            bin.style.position = Position.Absolute;
            bin.style.top = LvnTokens.Space1;
            bin.style.right = LvnTokens.Space1;
            bin.style.width = 28f;
            bin.style.height = 28f;
            bin.style.paddingLeft = 0f; bin.style.paddingRight = 0f;
            bin.style.paddingTop = 0f; bin.style.paddingBottom = 0f;
            bin.style.marginLeft = 0f; bin.style.marginRight = 0f;
            bin.style.marginTop = 0f; bin.style.marginBottom = 0f;
            bin.style.alignItems = Align.Center;
            bin.style.justifyContent = Justify.Center;
            bin.style.backgroundColor = LvnTokens.Veil(0.55f);
            bin.style.borderTopLeftRadius = 14f; bin.style.borderTopRightRadius = 14f;
            bin.style.borderBottomLeftRadius = 14f; bin.style.borderBottomRightRadius = 14f;

            var glyph = Lvn.UI.LvnIcons.Make(Lvn.UI.LvnIcon.Trash, 16f, LvnTokens.Text);
            glyph.pickingMode = PickingMode.Ignore;
            bin.Add(glyph);

            // Слов у кнопки-значка нет — переспрос виден заливкой: спокойная
            // корзина тёмная, взведённая красная. Обряд сам её красит и сам
            // разоружает, когда экран закрыли или прошло четыре секунды.
            Lvn.UI.LvnAskTwice.AskTwice(bin,
                calm: () => "",
                armed: () => "",
                confirmed: () => OnDrop?.Invoke(e),
                armedTint: LvnTokens.Bad);

            // ТАП ПО КОРЗИНЕ НЕ ОТКРЫВАЕТ СЦЕНУ. Плитка ловит клик целиком, а
            // события всплывают: без остановки первое же нажатие на корзину
            // увело бы игрока в пересмотр, и удалить он ничего не смог бы.
            bin.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());
            bin.RegisterCallback<PointerUpEvent>(evt => evt.StopPropagation());
            bin.RegisterCallback<ClickEvent>(evt => evt.StopPropagation());
            return bin;
        }
    }
}
