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
            public string Id;
            public string Name;      // авторское имя; на экране проходит через каталог перевода
            public string Chapter;
            public string Poster;
        }

        private readonly ILvnAssets _assets;
        private readonly List<Entry> _entries = new List<Entry>();
        private readonly VisualElement _sheet;
        private readonly Label _title;
        private readonly ScrollView _grid;
        private readonly Label _counter;
        private readonly Label _empty;
        private bool _stageGlass;

        /// <summary>Проиграть выбранную катсцену. Ставит хост: галерея знает
        /// адрес, но не умеет открывать главы.</summary>
        public System.Action<Entry> OnPlay;

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

            _empty = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of(
                "cutscenes.empty", "Scenes you have lived through appear here"));
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
            _empty.style.display = _entries.Count > 0 ? DisplayStyle.None : DisplayStyle.Flex;
            foreach (var e in _entries) _grid.Add(Tile(e));
        }

        /// <summary>Карточка сцены: превью во всю плитку, имя полосой снизу.
        /// Превью может не быть (сцена началась раньше, чем доехал фон) — тогда
        /// плитка остаётся тонированной, но открывается так же.</summary>
        private VisualElement Tile(Entry e)
        {
            var cell = new VisualElement();
            cell.style.width = Length.Percent(48.5f);
            cell.style.height = 150;
            cell.style.marginRight = Length.Percent(1.5f);
            cell.style.marginBottom = LvnTokens.Space2;
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
            if (!string.IsNullOrEmpty(e.Poster)) LvnPicture.Photo(art, e.Poster, _assets);
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

            cell.AddManipulator(new Clickable(() => { Close(); OnPlay?.Invoke(e); }));
            LvnMotion.Tappable(cell);
            return cell;
        }
    }
}
