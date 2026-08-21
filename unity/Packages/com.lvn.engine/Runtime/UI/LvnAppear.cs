using System;
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
            if (el == null || kind == LvnAppearKind.None) { done?.Invoke(); return; }
            var t = LvnTheme.Current;
            // Уход короче прихода: приход рассказывает, уход убирает. Равные
            // длительности читаются как задержка отклика.
            if (ms <= 0) ms = appearing ? t.AppearMs : t.DisappearMs;

            float shift = t.AppearShift;
            float scale = t.AppearScale;

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

            if (done != null) el.schedule.Execute(() => done()).ExecuteLater(ms + 1);
        }

        /// <summary>Убрать следы анимации — иначе элемент остаётся
        /// подмасштабированным или сдвинутым после переиспользования.</summary>
        public static void Reset(VisualElement el)
        {
            if (el == null) return;
            el.style.opacity = 1f;
            el.style.scale = new Scale(Vector2.one);
            el.style.translate = new Translate(0, 0);
        }

        // Общий двигатель: k идёт 0→1 при появлении и 1→0 при уходе. Кривые
        // разные — приход тормозит у цели, уход разгоняется прочь.
        private static void Anim(VisualElement el, int ms, bool appearing, Action<VisualElement, float> set)
        {
            float from = appearing ? 0f : 1f, to = appearing ? 1f : 0f;
            set(el, from);
            el.experimental.animation
              .Start(0f, 1f, ms, (e, p) =>
              {
                  float eased = appearing ? 1f - Mathf.Pow(1f - p, 3f)   // OutCubic
                                          : p * p;                       // InQuad
                  set(e, Mathf.Lerp(from, to, eased));
              });   // своей Ease нет: кривые разные для входа и выхода, они внутри
        }
    }
}
