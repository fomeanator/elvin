using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// The between-chapters screen (manifest <c>ui.chapter_end</c>): a scrim
    /// with "Конец главы", the finished chapter's name, and up to two buttons —
    /// continue to the next chapter (hidden on the last one) and back to the
    /// menu. <see cref="ShowAsync"/> resolves true → play the next chapter,
    /// false → return to the menu. Purely presentational: the chapter loop in
    /// NovelApp owns what "next" means.
    /// </summary>
    public sealed class ChapterEndScreen : VisualElement, ILvnHides
    {
        private readonly ChapterEndConfig _cfg;
        private readonly Label _title;
        private readonly Label _chapter;
        private readonly Button _continue;
        private readonly Button _menu;
        // Ожидание закрытия — у дома: связка из пяти обязательных частей
        // (флаг продолжения, отпустить прошлого ждущего, подписать отмену,
        // дождаться, снять подписку) стоила слишком дорого, чтобы держать
        // её копию. Свою пятую часть этот экран отдал дому.
        private readonly LvnCloseGate _gate = new LvnCloseGate();

        public ChapterEndScreen(ChapterEndConfig cfg, ILvnAssets assets)
        {
            _cfg = cfg ?? new ChapterEndConfig();

            ScreenUi.Stretch(this);
            style.backgroundColor = UiColor.Named(_cfg.bg_color, UiColor.WithAlpha(LvnTokens.Bg, 0.92f));
            style.alignItems = Align.Center;
            style.justifyContent = Justify.Center;
            style.display = DisplayStyle.None;

            var column = new VisualElement();
            column.style.alignItems = Align.Center;
            column.style.width = Length.Percent(82f);
            Add(column);

            _title = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Pick("chapter_end.title", _cfg.title, "End of chapter"));
            _title.style.unityTextAlign = TextAnchor.MiddleCenter;
            _title.style.color = UiColor.Named(_cfg.title_color, LvnTokens.Text);
            _title.style.fontSize = LvnTokens.TextOr(_cfg.title_size, LvnTokens.TextDisplay);
            _title.style.unityFontStyleAndWeight = FontStyle.Bold;
            column.Add(_title);

            _chapter = new Label();
            _chapter.style.unityTextAlign = TextAnchor.MiddleCenter;
            _chapter.style.color = UiColor.Named(_cfg.subtitle_color, LvnTokens.TextDim);
            _chapter.style.fontSize = LvnTokens.TextOr(_cfg.subtitle_size, LvnTokens.TextLg);
            _chapter.style.marginTop = LvnTokens.Space2;
            _chapter.style.whiteSpace = WhiteSpace.Normal;
            column.Add(_chapter);

            _continue = MakeButton(LvnWords.Pick("chapter_end.continue", _cfg.continue_label, "Continue"), primary: true);
            _continue.style.marginTop = LvnTokens.Space6; // пауза перед действием — крупнейшая ступень
            _continue.clicked += () => Resolve(true);
            column.Add(_continue);

            _menu = MakeButton(LvnWords.Pick("chapter_end.menu", _cfg.menu_label, "To menu"), primary: false);
            _menu.style.marginTop = LvnTokens.Space3;
            _menu.clicked += () => Resolve(false);
            column.Add(_menu);
        }

        private Button MakeButton(string text, bool primary)
        {
            var b = new Button { text = text };
            b.style.width = Length.Percent(100f);
            b.style.minHeight = 120;
            b.style.fontSize = LvnTokens.TextLg;
            b.style.unityFontStyleAndWeight = FontStyle.Bold;
            LvnStyler.Plate(b,
                primary ? UiColor.Named(_cfg.button_color, LvnTokens.Accent)
                        : UiColor.Named(_cfg.button_secondary_color, LvnTokens.Faint),
                UiColor.Named(_cfg.button_text_color, LvnTokens.Text),
                _cfg.button_radius ?? LvnTokens.RadiusLg);
            return b;
        }

        /// <summary>Show over everything and wait for a choice. True → continue
        /// to the next chapter; false → back to the menu. With no next chapter
        /// the continue button hides and the only way out is the menu (false).</summary>
        public Task<bool> ShowAsync(string chapterName, bool hasNext)
        {
            _chapter.text = chapterName ?? "";
            _chapter.style.display = string.IsNullOrEmpty(chapterName) ? DisplayStyle.None : DisplayStyle.Flex;
            _continue.style.display = hasNext ? DisplayStyle.Flex : DisplayStyle.None;
            style.display = DisplayStyle.Flex;
            return _gate.ReopenAsync();
        }

        /// <summary>Уйти с экрана и ОТПУСТИТЬ ждущего: экран конца главы
        /// держит цикл глав, и убранный молча он бы его подвесил. Своего ухода
        /// у него не было вовсе, поэтому набор экранов гасил показ мимо него —
        /// ровно тот случай, ради которого метка и заведена.</summary>
        public void Hide()
        {
            style.display = DisplayStyle.None;
            _gate.Release(false);   // некуда продолжать — значит, в меню
        }

        private void Resolve(bool next)
        {
            style.display = DisplayStyle.None;
            _gate.Release(next);
        }
    }
}
