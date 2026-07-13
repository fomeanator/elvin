using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// The novel's loading screen, built entirely in code and themed from a
    /// <see cref="LoadingScreenConfig"/> (manifest <c>ui.loading</c>): backdrop,
    /// dark scrim, optional fog, and a progress bar (optional track/fill/frame
    /// art, else solid colours) with percent, current-file and rotating-tip
    /// labels. The bar maths live in the pure <see cref="LoadingProgressModel"/>;
    /// this only renders. Drop it into any <c>UIDocument.rootVisualElement</c>
    /// (or another full-screen element) and <see cref="RunAsync"/> until your
    /// loading predicate is done.
    /// </summary>
    public sealed class LoadingScreen : VisualElement
    {
        private readonly LoadingScreenConfig _cfg;
        private readonly ILvnAssets _assets;

        private readonly VisualElement _bg;
        private readonly VisualElement _fog;
        private readonly VisualElement _scrim;
        private readonly VisualElement _fill;
        private readonly Label _percent;
        private readonly Label _file;
        private readonly Label _hint;
        private readonly Label _chapterTitle;
        private readonly Label _chapterSubtitle;

        private readonly LoadingProgressModel _model;
        private readonly ProgressRenderGate _gate = new ProgressRenderGate();
        private readonly float _fillSpan;
        private readonly float _scrimOpacity;

        public LoadingScreen(LoadingScreenConfig cfg, ILvnAssets assets)
        {
            _cfg = cfg ?? new LoadingScreenConfig();
            _assets = assets;
            _fillSpan = _cfg.fill_span_percent ?? LoadingProgressModel.FillSpanPercent;
            _model = new LoadingProgressModel(fillSpanPercent: _fillSpan);
            _scrimOpacity = _cfg.scrim_opacity ?? 0.65f;

            ScreenUi.Stretch(this);
            style.backgroundColor = UiColor.Parse(_cfg.bg_color, Color.black);
            pickingMode = PickingMode.Position; // swallow taps under the loader

            _bg = ScreenUi.Stretch(new VisualElement());
            Add(_bg);

            _scrim = ScreenUi.Stretch(new VisualElement());
            _scrim.style.backgroundColor = UiColor.Parse(_cfg.scrim_color, Color.black);
            _scrim.style.opacity = _scrimOpacity;
            Add(_scrim);

            _fog = ScreenUi.Stretch(new VisualElement());
            _fog.style.opacity = 0f;
            _fog.pickingMode = PickingMode.Ignore;
            Add(_fog);

            // ── progress bar (centred on bar_x/bar_y) ──
            float barX = _cfg.bar_x ?? 0.5f;
            float barY = _cfg.bar_y ?? 0.82f;
            float barW = _cfg.bar_width ?? 0.7f;
            float barH = _cfg.bar_height ?? 0.018f;

            var bar = ScreenUi.ProgressBar(
                barX, barY, barW, barH,
                UiColor.Parse(_cfg.bar_track_color, new Color(1f, 1f, 1f, 0.13f)),
                UiColor.Parse(_cfg.bar_fill_color, new Color(0.78f, 0.63f, 0.31f)),
                out var track, out _fill);
            Add(bar);

            var frame = ScreenUi.Stretch(new VisualElement());
            frame.pickingMode = PickingMode.Ignore;
            bar.Add(frame);

            // ── labels (placed relative to the bar) ──
            _hint = ScreenUi.CenterLabel(barY - 0.07f, UiColor.Parse(_cfg.hint_color, new Color(0.81f, 0.78f, 0.74f)), 24);
            _hint.style.display = (_cfg.show_hint ?? true) ? DisplayStyle.Flex : DisplayStyle.None;
            Add(_hint);

            _percent = ScreenUi.CenterLabel(barY + 0.02f, UiColor.Parse(_cfg.percent_color, Color.white), 26);
            _percent.style.display = (_cfg.show_percent ?? true) ? DisplayStyle.Flex : DisplayStyle.None;
            Add(_percent);

            // Chapter-title reveal, Liminal-style: the name lives ON the loading
            // screen (over the chapter's backdrop) instead of being a separate
            // screen after it — one entry surface, no flash between stages.
            _chapterTitle = ScreenUi.CenterLabel(0.34f, UiColor.Parse(_cfg.percent_color, new Color(0.96f, 0.93f, 0.85f)), 64);
            _chapterTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            _chapterTitle.style.opacity = 0f;
            Add(_chapterTitle);
            _chapterSubtitle = ScreenUi.CenterLabel(0.40f, UiColor.Parse(_cfg.hint_color, new Color(0.80f, 0.72f, 0.56f)), 34);
            _chapterSubtitle.style.opacity = 0f;
            Add(_chapterSubtitle);

            _file = ScreenUi.CenterLabel(barY + 0.055f, UiColor.Parse(_cfg.file_color, new Color(0.60f, 0.58f, 0.54f)), 18);
            _file.style.display = (_cfg.show_file ?? true) ? DisplayStyle.Flex : DisplayStyle.None;
            Add(_file);

            // Static art from the config (async — non-fatal if missing).
            _ = ScreenUi.AssignBgAsync(_fog, _cfg.fog_url, _assets);
            _ = ScreenUi.AssignBgAsync(track, _cfg.bar_track_url, _assets);
            _ = ScreenUi.AssignBgAsync(_fill, _cfg.bar_fill_url, _assets);
            _ = ScreenUi.AssignBgAsync(frame, _cfg.bar_frame_url, _assets);
            if (!string.IsNullOrEmpty(_cfg.bg_url)) _ = ScreenUi.AssignBgAsync(_bg, _cfg.bg_url, _assets);
        }

        /// <summary>Drives the loading bar until <paramref name="isDone"/> returns
        /// true and at least <c>min_seconds</c> have elapsed. <paramref name="progress"/>
        /// (0..1), when supplied, is the authoritative source; otherwise the bar
        /// creeps on a time floor and snaps to full when done. <paramref name="bgUrl"/>
        /// overrides the config backdrop (e.g. the chapter's loading bg);
        /// <paramref name="fileLabel"/> optionally feeds the under-bar filename.</summary>
        public async Task RunAsync(
            Func<bool> isDone,
            Func<float> progress = null,
            CancellationToken ct = default,
            string bgUrl = null,
            Func<string> fileLabel = null,
            string chapterTitle = null,
            string subtitle = null,
            float titleHoldSeconds = 0f,
            float? minSecondsOverride = null)
        {
            _model.Reset();
            _gate.Reset();
            style.display = DisplayStyle.Flex;
            style.opacity = 1f;
            ScreenUi.SetText(_percent, "");
            ScreenUi.SetText(_file, "");
            ScreenUi.SetText(_hint, "");
            SetFill(0f);

            // Chapter-title reveal on the loader itself (fade in over the first
            // half-second, hold for titleHoldSeconds after the work is done).
            bool hasTitle = !string.IsNullOrEmpty(chapterTitle) || !string.IsNullOrEmpty(subtitle);
            ScreenUi.SetText(_chapterTitle, chapterTitle ?? "");
            ScreenUi.SetText(_chapterSubtitle, subtitle ?? "");
            _chapterTitle.style.display = string.IsNullOrEmpty(chapterTitle) ? DisplayStyle.None : DisplayStyle.Flex;
            _chapterSubtitle.style.display = string.IsNullOrEmpty(subtitle) ? DisplayStyle.None : DisplayStyle.Flex;
            _chapterTitle.style.opacity = 0f;
            _chapterSubtitle.style.opacity = 0f;

            if (!string.IsNullOrEmpty(bgUrl)) _ = ScreenUi.AssignBgAsync(_bg, bgUrl, _assets);

            var tips = _cfg.tips;
            float minSeconds = minSecondsOverride ?? _cfg.min_seconds ?? 0f;
            float hold = hasTitle ? Mathf.Max(0f, titleHoldSeconds) : 0f;
            float start = Time.unscaledTime;
            float lastTip = -999f;
            float doneAt = -1f; // when work + floor completed — the title hold runs from here
            int tipIdx = 0;

            while (true)
            {
                if (ct.IsCancellationRequested) break;
                float now = Time.unscaledTime;
                float elapsed = now - start;
                bool done = isDone == null || isDone();
                bool floorDone = done && elapsed >= minSeconds;
                if (floorDone && doneAt < 0f) doneAt = now;

                if (hasTitle)
                {
                    float a = Mathf.Clamp01(elapsed / 0.5f);
                    _chapterTitle.style.opacity = a;
                    _chapterSubtitle.style.opacity = a;
                }

                // rotate tips
                if (tips != null && tips.Length > 0 && now - lastTip >= 3.5f)
                {
                    ScreenUi.SetText(_hint, tips[tipIdx % tips.Length] ?? "");
                    tipIdx++;
                    lastTip = now;
                }

                float target = progress != null
                    ? Mathf.Clamp01(progress())
                    : (minSeconds > 0f ? Mathf.Clamp01(elapsed / minSeconds) : 1f);

                if (floorDone) _model.SnapToFull();
                else _model.TickToward(Mathf.Min(target, 0.97f), Time.unscaledDeltaTime);

                SetFill(_model.FillPercent);
                if (_percent != null && _gate.PercentMoved(_model.Percent))
                    ScreenUi.SetText(_percent, (floorDone ? 100 : _model.Percent) + "%");

                if (_file != null && fileLabel != null)
                {
                    var text = (!done) ? (fileLabel() ?? "") : "";
                    if (_gate.LabelChanged(text)) ScreenUi.SetText(_file, text);
                }

                if (floorDone && now - doneAt >= hold) { SetFill(_fillSpan); break; }

                try { await Task.Yield(); }
                catch (OperationCanceledException) { break; }
            }
        }

        /// <summary>Fade the whole loader out (call after RunAsync before swapping
        /// to the title card or the scene).</summary>
        public Task FadeOutAsync(float seconds = 0.4f, CancellationToken ct = default) =>
            ScreenFx.FadeAsync(this, 1f, 0f, seconds, ct);

        public void Hide()
        {
            style.display = DisplayStyle.None;
            style.opacity = 1f;
        }

        private void SetFill(float fillPercent)
        {
            if (_fill != null && _gate.FillMoved(fillPercent))
                _fill.style.width = Length.Percent(Mathf.Clamp(fillPercent, 0f, _fillSpan));
        }

    }
}
