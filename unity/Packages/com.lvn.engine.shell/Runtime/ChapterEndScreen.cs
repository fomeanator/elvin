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
    public sealed class ChapterEndScreen : VisualElement
    {
        private readonly ChapterEndConfig _cfg;
        private readonly Label _title;
        private readonly Label _chapter;
        private readonly Button _continue;
        private readonly Button _menu;
        private TaskCompletionSource<bool> _tcs;

        public ChapterEndScreen(ChapterEndConfig cfg, ILvnAssets assets)
        {
            _cfg = cfg ?? new ChapterEndConfig();

            ScreenUi.Stretch(this);
            style.backgroundColor = UiColor.Parse(_cfg.bg_color, new Color(LvnTokens.Bg.r, LvnTokens.Bg.g, LvnTokens.Bg.b, 0.92f));
            style.alignItems = Align.Center;
            style.justifyContent = Justify.Center;
            style.display = DisplayStyle.None;

            var column = new VisualElement();
            column.style.alignItems = Align.Center;
            column.style.width = Length.Percent(82f);
            Add(column);

            _title = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Pick("chapter_end.title", _cfg.title, "End of chapter"));
            _title.style.unityTextAlign = TextAnchor.MiddleCenter;
            _title.style.color = UiColor.Parse(_cfg.title_color, LvnTokens.Text);
            _title.style.fontSize = _cfg.title_size ?? 64f;
            _title.style.unityFontStyleAndWeight = FontStyle.Bold;
            column.Add(_title);

            _chapter = new Label();
            _chapter.style.unityTextAlign = TextAnchor.MiddleCenter;
            _chapter.style.color = UiColor.Parse(_cfg.subtitle_color, LvnTokens.TextDim);
            _chapter.style.fontSize = _cfg.subtitle_size ?? 34f;
            _chapter.style.marginTop = 14;
            _chapter.style.whiteSpace = WhiteSpace.Normal;
            column.Add(_chapter);

            _continue = MakeButton(LvnWords.Pick("chapter_end.continue", _cfg.continue_label, "Continue"), primary: true);
            _continue.style.marginTop = 72;
            _continue.clicked += () => Resolve(true);
            column.Add(_continue);

            _menu = MakeButton(LvnWords.Pick("chapter_end.menu", _cfg.menu_label, "To menu"), primary: false);
            _menu.style.marginTop = 20;
            _menu.clicked += () => Resolve(false);
            column.Add(_menu);
        }

        private Button MakeButton(string text, bool primary)
        {
            var b = new Button { text = text };
            b.style.width = Length.Percent(100f);
            b.style.minHeight = 120;
            b.style.fontSize = 40;
            b.style.unityFontStyleAndWeight = FontStyle.Bold;
            b.style.color = UiColor.Parse(_cfg.button_text_color, LvnTokens.Text);
            b.style.backgroundColor = primary
                ? UiColor.Parse(_cfg.button_color, LvnTokens.Accent)
                : UiColor.Parse(_cfg.button_secondary_color, LvnTokens.Faint);
            LvnChrome.ClearBorder(b);
            LvnChrome.Round(b, _cfg.button_radius ?? 26f);
            return b;
        }

        /// <summary>Show over everything and wait for a choice. True → continue
        /// to the next chapter; false → back to the menu. With no next chapter
        /// the continue button hides and the only way out is the menu (false).</summary>
        public Task<bool> ShowAsync(string chapterName, bool hasNext)
        {
            _tcs?.TrySetResult(false); // a stale awaiter must never deadlock the loop
            _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _chapter.text = chapterName ?? "";
            _chapter.style.display = string.IsNullOrEmpty(chapterName) ? DisplayStyle.None : DisplayStyle.Flex;
            _continue.style.display = hasNext ? DisplayStyle.Flex : DisplayStyle.None;
            style.display = DisplayStyle.Flex;
            return _tcs.Task;
        }

        private void Resolve(bool next)
        {
            style.display = DisplayStyle.None;
            _tcs?.TrySetResult(next);
        }
    }
}
