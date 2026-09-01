using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// ПРОЯВЛЕНИЕ РЕПЛИКИ — как текст проступает по буквам и когда он готов.
    ///
    /// <para>Реплика не печатается посимвольно рывками: слово проступает
    /// целиком, а внутри слова — плавностью вершин (проход по глифам красит
    /// хвост прозрачностью). Отсюда и соседство: начало показа, оценка его
    /// длительности, темп, тик и обход глифов — одна работа, а не пять.</para>
    ///
    /// <para>Оценка длительности стоит рядом с самим показом намеренно: по ней
    /// живёт автопрокрутка и озвучка, и разойдись она с настоящим темпом —
    /// голос обгонит текст.</para>
    /// </summary>
    public sealed partial class DialogueBox
    {
        private void SetRevealing(bool on)
        {
            if (IsRevealing == on) return;
            IsRevealing = on;
            RevealingChanged?.Invoke(on);
        }

        /// <summary>
        /// Begin revealing <paramref name="text"/> with the typewriter. Optional
        /// <paramref name="cps"/> overrides the theme speed for this line.
        /// Первые ~<see cref="VnTheme.InitialVisibleCharacters"/> символов (до
        /// конца слова) встают МГНОВЕННО — смысл ловится сразу, печатается
        /// только хвост.
        /// </summary>
        public void Reveal(string text, float? cps = null)
        {
            _tw.SetText(text ?? "");
            _cps = PaceFor(cps);
            _startTime = LvnClock.Now();
            _lastQuantum = -1;
            _tick?.Pause();

            // The budget is deliberately approximate: round it FORWARD to a word
            // boundary so the first readable block never opens as "предложе…".
            _initialReveal = _tw.WordEndAtOrAfter(InitialFor(_tw.VisibleCount));
            SetRevealing(_tw.VisibleCount > _initialReveal);
            RefreshAdvanceHint(); // hidden while revealing
            _revealProgress = _initialReveal;
            _wordCompleteChars = _initialReveal;
            _wordActiveEndChars = _initialReveal;
            _wordActiveAlpha = 0f;
            _body.text = _tw.Full();
            if (IsRevealing)
            {
                _body.MarkDirtyRepaint(); // same text as the last line? still restart at 0
                // ОДИН ТАЙМЕР НА ВСЕ РЕПЛИКИ. Здесь заводился новый на каждую:
                // прежний только приостанавливался и оставался в расписании
                // панели навсегда — за главу их набегали сотни. Пульсация
                // указателя двумя полями выше давно живёт правильно, и правило
                // у них общее: завести один раз, дальше будить и усыплять.
                _tick ??= schedule.Execute(Tick).Every(16);
                _tick.Resume();
            }
        }

        /// <summary>How long this line's tail will take at the current reader
        /// pace. Used to let a newly entering actor settle with the text instead
        /// of finishing its animation in an unrelated rhythm.</summary>
        public float EstimateRevealSeconds(string text, float? cps = null)
        {
            var probe = new RichTextTypewriter();
            probe.SetText(text ?? "");
            int initial = probe.WordEndAtOrAfter(InitialFor(probe.VisibleCount));
            int words = probe.WordsAfter(initial);
            float pace = PaceFor(cps);
            float wordsPerSecond = TypewriterClock.Progress(1f, pace) / AverageCharactersPerWord;
            return words / Mathf.Max(0.01f, wordsPerSecond);
        }

        /// <summary>
        /// ТЕМП ЭТОЙ СТРОКИ: скорость из команды, если автор её задал и она
        /// осмысленна, иначе тема.
        ///
        /// <para>Правило стояло дважды — в самой печати и в её ОЦЕНКЕ, — а они
        /// обязаны совпадать: по оценке входящий актёр рассчитывает, когда
        /// осесть вместе с текстом. Разойдись они на строке с авторской
        /// скоростью, и герой заканчивал бы движение в чужом ритме. Дублировать
        /// правило, обе половины которого сверяются друг с другом, — верный
        /// способ однажды их рассинхронизировать.</para>
        /// </summary>
        private float PaceFor(float? cps)
            => cps.HasValue && cps.Value > TypewriterClock.MinCps ? cps.Value : _theme.CharsPerSecond;

        /// <summary>Сколько символов встаёт мгновенно — не больше, чем есть.</summary>
        private int InitialFor(int visibleCount)
            => Mathf.Min(Mathf.Max(0, _theme.InitialVisibleCharacters), visibleCount);

        /// <summary>Snap to the full line immediately (e.g. on the first tap).</summary>
        public void Complete()
        {
            _tick?.Pause();
            SetRevealing(false);
            _body.MarkDirtyRepaint(); // repaint with the reveal ramp inactive
            RefreshAdvanceHint();
        }

        // Progress quantum of the last RevealTicked — one step per word.
        private int _lastQuantum = -1;

        private void Tick()
        {
            if (!IsRevealing) { _tick?.Pause(); return; }
            float elapsed = LvnClock.Since(_startTime);
            float wordProgress = TypewriterClock.Progress(elapsed, _cps) / AverageCharactersPerWord;
            _tw.WordReveal(_initialReveal, wordProgress,
                out _wordCompleteChars, out _wordActiveEndChars, out _wordActiveAlpha);
            if (_wordCompleteChars >= _tw.VisibleCount)
            {
                Complete();
                return;
            }
            _revealProgress = _wordCompleteChars
                + (_wordActiveEndChars - _wordCompleteChars) * _wordActiveAlpha;
            _body.MarkDirtyRepaint(); // vertex-tint pass only — no layout, no strings
            int q = Mathf.FloorToInt(wordProgress);
            if (q == _lastQuantum) return;
            _lastQuantum = q;
            RevealTicked?.Invoke();
        }

        // Per-word alpha before the text mesh renders. Vertices are
        // regenerated fresh for every repaint, so this only ever writes the
        // CURRENT frame's fade — nothing accumulates. Inactive (IsRevealing
        // false) it leaves the mesh untouched: the full line renders as-is.
        private void OnPostProcessGlyphs(TextElement.GlyphsEnumerable glyphs)
        {
            if (!IsRevealing) return;
            int count = glyphs.Count;
            if (count <= 0) return;

            // Boundaries are in CHARS (steps include spaces); glyphs are only
            // rendered quads. Rescale both complete and active word ends.
            int chars = _tw.VisibleCount;
            float completeGlyph = chars > 0 ? _wordCompleteChars * count / (float)chars : count;
            float activeGlyph = chars > 0 ? _wordActiveEndChars * count / (float)chars : count;

            int i = 0;
            foreach (TextElement.Glyph glyph in glyphs)
            {
                float midpoint = i + 0.5f;
                i++;
                if (midpoint <= completeGlyph) continue;
                byte b = midpoint <= activeGlyph
                    ? (byte)(_wordActiveAlpha * 255f + 0.5f)
                    : (byte)0;
                var verts = glyph.vertices;
                for (int v = 0; v < verts.Length; v++)
                {
                    var vert = verts[v];
                    var tint = vert.tint;
                    tint.a = b == 0 ? (byte)0 : (byte)(tint.a * b / 255);
                    vert.tint = tint;
                    verts[v] = vert;
                }
            }
        }
    }
}
