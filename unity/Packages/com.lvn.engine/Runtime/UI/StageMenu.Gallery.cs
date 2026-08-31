using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// ГАЛЕРЕЯ И ИСТОРИЯ — часть <see cref="StageMenu"/>: что игрок уже видел.
    /// Открытые CG во всю ширину и лента прочитанных реплик с отмоткой.
    /// </summary>
    public sealed partial class StageMenu
    {
        // Slot thumbnails shown by the CURRENT panel — destroyed on every panel
        // swap/close (they are per-view decoded textures, not cached sprites).
        private readonly List<Texture2D> _thumbs = new List<Texture2D>();

        private void DestroyThumbs()
        {
            foreach (var t in _thumbs) if (t != null) UnityEngine.Object.Destroy(t);
            _thumbs.Clear();
        }

        private void ShowHistory()
        {
            _pane = ShowHistory;
            var p = Panel(L("history", "History"));
            var scroll = LvnScroll.Vertical();
            scroll.style.flexGrow = 1;
            p.Add(scroll);

            // Say-lines count from the end so a tap knows how many beats back its
            // line lives; the current line (0 back) and choice marks aren't jumps.
            var backlog = _stage.Backlog;
            int saysAfter = 0;
            for (int bi = backlog.Count - 1; bi >= 0; bi--)
                if (backlog[bi].style != "choice") saysAfter++;

            foreach (var (who, text, style) in backlog)
            {
                var line = new VisualElement();
                line.style.marginBottom = 8;
                if (style == "choice")
                {
                    // The branch the player took — indented, accented, arrowed.
                    var mark = Text("▸ " + text, 22, FontStyle.Italic);
                    mark.style.color = _theme.MenuFabColor;
                    line.style.marginLeft = 14;
                    line.Add(mark);
                }
                else
                {
                    saysAfter--;
                    if (!string.IsNullOrEmpty(who)) line.Add(Text(who, 24, FontStyle.Bold));
                    line.Add(Text(text, 24, FontStyle.Normal, dim: string.IsNullOrEmpty(who)));
                    // Tap-to-return: rewind to this line (the genre's history
                    // jump). Lines older than the snapshot history (or before a
                    // load, which clears it) aren't reachable — leave them inert.
                    // Отключается темой (ui.menu.history_jump=false): продукту с
                    // автосейвом история нужна как ЧТЕНИЕ, а случайный тап,
                    // отматывающий главу назад, читается игроком как баг.
                    int stepsBack = saysAfter;
                    int reach = _stage.Player != null ? _stage.Player.HistoryDepth - 1 : 0;
                    if (_theme.MenuHistoryJump && stepsBack > 0 && stepsBack <= reach)
                        line.RegisterCallback<PointerDownEvent>(e =>
                        {
                            e.StopPropagation();
                            Close();
                            _stage.RollbackSteps(stepsBack);
                        });
                }
                scroll.Add(line);
            }
            // Newest last — land the reader there.
            scroll.schedule.Execute(() =>
                scroll.scrollOffset = new Vector2(0, float.MaxValue)).ExecuteLater(50);
        }

        private void ShowGallery()
        {
            _pane = ShowGallery;
            var p = Panel(L("gallery", "Gallery"));
            var scroll = LvnScroll.Vertical();
            scroll.style.flexGrow = 1;
            p.Add(scroll);

            var grid = new VisualElement();
            grid.style.flexDirection = FlexDirection.Row;
            grid.style.flexWrap = Wrap.Wrap;
            scroll.Add(grid);

            var unlocked = LvnGalleryStore.Unlocked(_stage.SaveTitleId);
            foreach (var item in _stage.Gallery)
            {
                if (item == null) continue;
                bool open = unlocked.Contains(item.id);

                var cell = new VisualElement();
                cell.style.width = Length.Percent(31);
                cell.style.marginRight = Length.Percent(2);
                cell.style.marginBottom = 12;

                var frame = new VisualElement();
                frame.style.height = 110;
                frame.style.backgroundColor = new Color(0f, 0f, 0f, 0.35f);
                frame.style.justifyContent = Justify.Center;
                frame.style.alignItems = Align.Center;
                LvnChrome.Round(frame, 8f);
                cell.Add(frame);

                if (open)
                {
                    var img = new Image { scaleMode = ScaleMode.ScaleAndCrop };
                    img.style.width = Length.Percent(100);
                    img.style.height = Length.Percent(100);
                    frame.Add(img);
                    LoadCg(img, item.url);
                    var full = item; // capture per cell
                    frame.RegisterCallback<PointerDownEvent>(e =>
                    {
                        e.StopPropagation();
                        ShowCgFull(full);
                    });
                    if (!string.IsNullOrEmpty(item.name))
                        cell.Add(Text(item.name, 20, FontStyle.Normal, dim: true));
                }
                else frame.Add(Text("?", 30, FontStyle.Bold, dim: true));

                grid.Add(cell);
            }
        }

        // Fullscreen viewer for one unlocked CG — chrome-free art, tap closes
        // back to the grid.
        private void ShowCgFull(Lvn.Content.LvnGalleryItem item)
        {
            _pane = () => ShowCgFull(item);
            DestroyThumbs();
            _scrim.Clear();
            var img = new Image { scaleMode = ScaleMode.ScaleToFit };
            LvnChrome.Stretch(img);
            _scrim.Add(img);
            LoadCg(img, item.url);
            img.RegisterCallback<PointerDownEvent>(e => { e.StopPropagation(); ShowGallery(); });
        }

        // Sprites come through the stage's asset chain (cache-aware); a panel
        // closed mid-load just orphans the element — nothing to cancel.
        private void LoadCg(Image img, string url) => LvnAsync.Fire(LoadCgAsync(img, url), "LoadCg");

        private async Task LoadCgAsync(Image img, string url)
        {
            if (_stage.Assets == null || string.IsNullOrEmpty(url)) return;
            try
            {
                var sprite = await _stage.Assets.LoadSpriteAsync(url, System.Threading.CancellationToken.None);
                if (sprite != null && img.panel != null)
                {
                    img.sprite = sprite;
                    // ЗАКРЕПЛЯЕМ: кэш вытесняет по давности и не знает, что CG
                    // сейчас смотрят. Без пина открытая галерея белела ровно
                    // так же, как когда-то обложки хаба.
                    LvnPicture.Pin(img, sprite, _stage.Assets);
                }
            }
            catch { /* a missing CG just leaves the dark frame */ }
        }
    }
}
