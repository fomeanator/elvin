using System;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.UIElements.Experimental;

namespace Lvn.UI
{
    /// <summary>
    /// КАК ВЕЩИ ПОЯВЛЯЮТСЯ И УХОДЯТ — один набор на весь движок.
    ///
    /// <para>Персонаж, панель, кнопка и всплывающая табличка обязаны двигаться
    /// одинаково: если у каждого своя анимация, экран выглядит собранным из
    /// чужих кусков. Поэтому виды перечислены здесь, а не заводятся на месте.</para>
    ///
    /// <para>Опорный образ — <b>телефон лежит на столе</b>. Элемент не
    /// «включается», он ВСПЛЫВАЕТ из-под стекла и УТОПАЕТ обратно: чуть меньше
    /// натуральной величины, полупрозрачный, и за две десятых секунды выходит
    /// на поверхность. Обратный ход не зеркальный — уход всегда быстрее
    /// прихода, иначе интерфейс кажется вязким.</para>
    /// </summary>
    public enum LvnAppearKind
    {
        None,
        Fade,       // просто проявление
        Rise,       // всплывает из глубины: масштаб + прозрачность
        Pop,        // упругий скачок — для наград и попаданий
        SlideUp,    // выезжает снизу — нижние панели
        SlideDown,  // сверху — таблички и оповещения
        SlideLeft,
        SlideRight,
        Drop,       // падает сверху и осаживается
        Unfold,     // раскрывается по высоте — списки, разделы
    }

    public static class LvnAppear
    {
        // UI Toolkit does not replace an experimental animation when another
        // Start() targets the same element. Both ticks keep writing opacity /
        // transform, and the old scheduled completion can mutate a newer beat.
        // One run owns one element; replacement stops both its animation and its
        // completion callback before the new run writes its first frame.
        private sealed class AnimationRun
        {
            public int Generation;
            public ValueAnimation<float> Animation;
            public IVisualElementScheduledItem Completion;
        }

        private static readonly ConditionalWeakTable<VisualElement, AnimationRun> Runs
            = new ConditionalWeakTable<VisualElement, AnimationRun>();

        private static (AnimationRun run, int generation) Begin(VisualElement el)
        {
            var run = Runs.GetOrCreateValue(el);
            run.Generation++;
            if (run.Animation != null)
            {
                try { run.Animation.Stop(); } catch { /* already completed/detached */ }
                run.Animation = null;
            }
            if (run.Completion != null)
            {
                try { run.Completion.Pause(); } catch { /* detached scheduler */ }
                run.Completion = null;
            }
            return (run, run.Generation);
        }

        private static bool Owns(AnimationRun run, int generation) =>
            run != null && run.Generation == generation;

        private static void Keep(AnimationRun run, int generation,
                                 ValueAnimation<float> animation)
        {
            if (!Owns(run, generation))
            {
                // Пока эта анимация заводилась, элемент забрал следующий ход.
                // Гасим её и молчим: Stop() у уже завершённой или отцепленной от
                // панели анимации бросает, и это ровно то, чего мы и добивались.
                try { animation?.Stop(); } catch { /* уже кончилась — цель достигнута */ }
                return;
            }
            run.Animation = animation;
        }

        private static void CompleteLater(VisualElement el, AnimationRun run,
                                          int generation, int ms, Action done)
        {
            if (done == null || !Owns(run, generation)) return;
            var completion = el.schedule.Execute(() =>
            {
                if (!Owns(run, generation)) return;
                run.Animation = null;
                run.Completion = null;
                done();
            });
            run.Completion = completion;
            completion.ExecuteLater(ms + 1);
        }

        /// <summary>Имя вида из языка. Неизвестное — None: молча не двигаемся,
        /// а не падаем посреди главы.</summary>
        public static LvnAppearKind Parse(string name)
        {
            if (string.IsNullOrEmpty(name)) return LvnAppearKind.None;
            switch (name.Trim().ToLowerInvariant())
            {
                case "fade": return LvnAppearKind.Fade;
                case "rise": return LvnAppearKind.Rise;
                case "pop": return LvnAppearKind.Pop;
                case "slide_up": case "up": return LvnAppearKind.SlideUp;
                case "slide_down": case "down": return LvnAppearKind.SlideDown;
                case "slide_left": case "left": return LvnAppearKind.SlideLeft;
                case "slide_right": case "right": return LvnAppearKind.SlideRight;
                case "drop": return LvnAppearKind.Drop;
                case "unfold": return LvnAppearKind.Unfold;
            }
            return LvnAppearKind.None;
        }

        /// <summary>Сыграть появление (<paramref name="appearing"/>) или уход.
        /// <paramref name="ms"/> = 0 берёт длительность темы.</summary>
        public static void Play(VisualElement el, LvnAppearKind kind, bool appearing = true,
                                int ms = 0, Action done = null)
        {
            if (el == null) { done?.Invoke(); return; }
            if (kind == LvnAppearKind.None) { Reset(el); done?.Invoke(); return; }
            var t = LvnTheme.Current;
            // Уход короче прихода: приход рассказывает, уход убирает. Равные
            // длительности читаются как задержка отклика.
            if (ms <= 0)
                ms = Mathf.Max(1, Mathf.RoundToInt(
                    (appearing ? t.AppearMs : t.DisappearMs) * VnTheme.MotionDurationScale));

            float shift = t.AppearShift;
            float scale = t.AppearScale;
            var (run, generation) = Begin(el);

            switch (kind)
            {
                case LvnAppearKind.Fade:
                    Anim(el, ms, appearing, (e, k) => e.style.opacity = k);
                    break;

                case LvnAppearKind.Rise:
                    // Из-под стекла: масштаб идёт от AppearScale к единице,
                    // прозрачность — следом. Ключ к ощущению глубины в том, что
                    // масштаб меняется МЕНЬШЕ, чем ждёшь: 0.94, а не 0.7.
                    Anim(el, ms, appearing, (e, k) =>
                    {
                        e.style.opacity = k;
                        float s = Mathf.Lerp(scale, 1f, k);
                        e.style.scale = new Scale(new Vector2(s, s));
                    });
                    break;

                case LvnAppearKind.Pop:
                    Anim(el, ms, appearing, (e, k) =>
                    {
                        e.style.opacity = Mathf.Clamp01(k * 1.6f);
                        // Перелёт за единицу и возврат — упругость без пружины.
                        float s = k < 0.7f ? Mathf.Lerp(scale, 1.06f, k / 0.7f)
                                           : Mathf.Lerp(1.06f, 1f, (k - 0.7f) / 0.3f);
                        e.style.scale = new Scale(new Vector2(s, s));
                    });
                    break;

                case LvnAppearKind.SlideUp:
                    Anim(el, ms, appearing, (e, k) =>
                    {
                        e.style.opacity = k;
                        e.style.translate = new Translate(0, Mathf.Lerp(shift, 0f, k));
                    });
                    break;

                case LvnAppearKind.SlideDown:
                    Anim(el, ms, appearing, (e, k) =>
                    {
                        e.style.opacity = k;
                        e.style.translate = new Translate(0, Mathf.Lerp(-shift, 0f, k));
                    });
                    break;

                case LvnAppearKind.SlideLeft:
                    Anim(el, ms, appearing, (e, k) =>
                    {
                        e.style.opacity = k;
                        e.style.translate = new Translate(Mathf.Lerp(shift * 2f, 0f, k), 0);
                    });
                    break;

                case LvnAppearKind.SlideRight:
                    Anim(el, ms, appearing, (e, k) =>
                    {
                        e.style.opacity = k;
                        e.style.translate = new Translate(Mathf.Lerp(-shift * 2f, 0f, k), 0);
                    });
                    break;

                case LvnAppearKind.Drop:
                    // Падает сверху и осаживается ниже цели, потом встаёт.
                    Anim(el, ms, appearing, (e, k) =>
                    {
                        e.style.opacity = Mathf.Clamp01(k * 2f);
                        float y = k < 0.72f ? Mathf.Lerp(-shift * 2.4f, shift * 0.22f, k / 0.72f)
                                            : Mathf.Lerp(shift * 0.22f, 0f, (k - 0.72f) / 0.28f);
                        e.style.translate = new Translate(0, y);
                    });
                    break;

                case LvnAppearKind.Unfold:
                    Anim(el, ms, appearing, (e, k) =>
                    {
                        e.style.opacity = Mathf.Clamp01(k * 1.4f);
                        e.style.scale = new Scale(new Vector2(1f, Mathf.Lerp(0.5f, 1f, k)));
                    });
                    break;
            }

            CompleteLater(el, run, generation, ms, done);
        }

        /// <summary>Убрать следы анимации — иначе элемент остаётся
        /// подмасштабированным или сдвинутым после переиспользования.</summary>
        public static void Reset(VisualElement el)
        {
            if (el == null) return;
            Begin(el); // invalidate ticks and completion from the previous owner
            el.style.opacity = 1f;
            el.style.scale = new Scale(Vector2.one);
            el.style.translate = new Translate(0, 0);
        }

        /// <summary>Release a card from the screen, then let it fall away. The
        /// fullscreen host carries the translation so a free-positioned card
        /// keeps its authored anchor; the visible card only tilts and recedes.</summary>
        public static void DetachDrop(VisualElement fadeHost, VisualElement card,
                                      int ms, Action done = null)
        {
            if (fadeHost == null || card == null)
            {
                done?.Invoke();
                return;
            }
            ms = Mathf.Max(1, ms);
            var (run, generation) = Begin(fadeHost);
            float fall = Mathf.Max(48f, LvnTheme.Current.AppearShift * 2.8f);
            fadeHost.style.opacity = 1f;
            fadeHost.style.translate = new Translate(0, 0);
            card.style.scale = new Scale(Vector2.one);
            card.style.rotate = new Rotate(new Angle(0f, AngleUnit.Degree));
            card.style.transformOrigin = new TransformOrigin(Length.Percent(50), Length.Percent(8));
            var animation = fadeHost.experimental.animation
                .Start(0f, 1f, ms, (e, p) =>
                {
                    if (!Owns(run, generation)) return;
                    const float releaseEnd = 0.24f;
                    if (p <= releaseEnd)
                    {
                        // A short held beat: the lower edge pulls loose while the
                        // top still feels attached to the glass.
                        float release = p / releaseEnd;
                        float smooth = release * release * (3f - 2f * release);
                        e.style.opacity = 1f;
                        e.style.translate = new Translate(0, Mathf.Lerp(0f, -2f, smooth));
                        float s = Mathf.Lerp(1f, 0.985f, smooth);
                        card.style.scale = new Scale(new Vector2(s, s));
                        card.style.rotate = new Rotate(new Angle(
                            Mathf.Lerp(0f, -0.55f, smooth), AngleUnit.Degree));
                        return;
                    }

                    // Once released, gravity accelerates the card down. Opacity
                    // stays long enough to make the direction readable, then
                    // clears before the next card enters.
                    float fallProgress = (p - releaseEnd) / (1f - releaseEnd);
                    float gravity = fallProgress * fallProgress;
                    float fade = Mathf.Clamp01((fallProgress - 0.08f) / 0.92f);
                    e.style.opacity = 1f - fade * fade;
                    e.style.translate = new Translate(0, Mathf.Lerp(-2f, fall, gravity));
                    float scale = Mathf.Lerp(0.985f, 0.94f, fallProgress);
                    card.style.scale = new Scale(new Vector2(scale, scale));
                    card.style.rotate = new Rotate(new Angle(
                        Mathf.Lerp(-0.55f, 2.2f, fallProgress), AngleUnit.Degree));
                });
            Keep(run, generation, animation);
            CompleteLater(fadeHost, run, generation, ms, done);
        }

        /// <summary>Bring the replacement card up from below and let it settle
        /// onto the screen. This is the matching entrance for DetachDrop.</summary>
        public static void CardArrive(VisualElement fadeHost, VisualElement card,
                                      int ms, Action done = null)
        {
            if (fadeHost == null || card == null)
            {
                done?.Invoke();
                return;
            }
            ms = Mathf.Max(1, ms);
            var (run, generation) = Begin(fadeHost);
            float travel = Mathf.Max(24f, LvnTheme.Current.AppearShift * 1.45f);
            fadeHost.style.opacity = 0f;
            fadeHost.style.translate = new Translate(0, travel);
            card.style.scale = new Scale(new Vector2(0.975f, 0.975f));
            card.style.rotate = new Rotate(new Angle(-0.7f, AngleUnit.Degree));
            card.style.transformOrigin = new TransformOrigin(Length.Percent(50), Length.Percent(8));
            var animation = fadeHost.experimental.animation
                .Start(0f, 1f, ms, (e, p) =>
                {
                    if (!Owns(run, generation)) return;
                    float settle = 1f - Mathf.Pow(1f - p, 3f);
                    e.style.opacity = Mathf.Clamp01(p * 1.65f);
                    e.style.translate = new Translate(0, Mathf.Lerp(travel, 0f, settle));
                    float scale = Mathf.Lerp(0.975f, 1f, settle);
                    card.style.scale = new Scale(new Vector2(scale, scale));
                    card.style.rotate = new Rotate(new Angle(
                        Mathf.Lerp(-0.7f, 0f, settle), AngleUnit.Degree));
                });
            Keep(run, generation, animation);
            CompleteLater(fadeHost, run, generation, ms, done);
        }

        // Общий двигатель: k идёт 0→1 при появлении и 1→0 при уходе. Кривые
        // разные — приход тормозит у цели, уход разгоняется прочь.
        private static void Anim(VisualElement el, int ms, bool appearing, Action<VisualElement, float> set)
        {
            if (!Runs.TryGetValue(el, out var run))
                throw new InvalidOperationException("LvnAppear animation has no owner");
            int generation = run.Generation;
            float from = appearing ? 0f : 1f, to = appearing ? 1f : 0f;
            set(el, from);
            var animation = el.experimental.animation
              .Start(0f, 1f, ms, (e, p) =>
              {
                  if (!Owns(run, generation)) return;
                  float eased = appearing ? 1f - Mathf.Pow(1f - p, 3f)   // OutCubic
                                          : p * p;                       // InQuad
                  set(e, Mathf.Lerp(from, to, eased));
              });   // своей Ease нет: кривые разные для входа и выхода, они внутри
            Keep(run, generation, animation);
        }
    }
}
