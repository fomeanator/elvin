using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// ЩИПОК — приблизить картинку двумя пальцами и подвинуть её одним.
    ///
    /// <para>Заведено для разворота катсцены: кадр разглядывают, а разглядывать
    /// без увеличения на телефоне нечем («надо чтобы мультитачем можно было
    /// уменьшать и увеличивать» — Илья 09.09). Дом общий, потому что то же
    /// движение просит CG-галерея и любой полноэкранный арт.</para>
    ///
    /// <para>Масштаб и сдвиг наносятся на СОДЕРЖИМОЕ, а рамкой служит окно:
    /// так увеличенная картинка подрезается окном, а не лезет на соседей.
    /// Сдвиг ограничен полем, которое реально ушло за край, — иначе кадр
    /// уезжает в пустоту и игрок теряет его вовсе.</para>
    /// </summary>
    public static class LvnPinch
    {
        /// <summary>Дальше этого не приближаем: на телефоне сверх четырёх крат
        /// видно уже не картинку, а её пиксели.</summary>
        public const float MaxScale = 4f;

        public static void Attach(VisualElement window, VisualElement content, float max = MaxScale)
        {
            if (window == null || content == null) return;
            var touches = new Dictionary<int, Vector2>();
            float scale = 1f, startScale = 1f, startSpan = 0f;
            Vector2 shift = Vector2.zero, startShift = Vector2.zero, startMid = Vector2.zero;

            void Paint()
            {
                var box = window.contentRect;
                // Ушедшее за край поле: половина прироста по каждой стороне.
                float roomX = Mathf.Max(0f, (box.width * scale - box.width) * 0.5f);
                float roomY = Mathf.Max(0f, (box.height * scale - box.height) * 0.5f);
                shift.x = Mathf.Clamp(shift.x, -roomX, roomX);
                shift.y = Mathf.Clamp(shift.y, -roomY, roomY);
                content.style.scale = new Scale(new Vector2(scale, scale));
                content.style.translate = new Translate(shift.x, shift.y);
            }

            Vector2 Mid()
            {
                var sum = Vector2.zero;
                foreach (var p in touches.Values) sum += p;
                return touches.Count > 0 ? sum / touches.Count : Vector2.zero;
            }

            float Span()
            {
                if (touches.Count < 2) return 0f;
                Vector2 a = Vector2.zero, b = Vector2.zero;
                int i = 0;
                foreach (var p in touches.Values) { if (i++ == 0) a = p; else { b = p; break; } }
                return Vector2.Distance(a, b);
            }

            void Rebase()
            {
                startScale = scale;
                startShift = shift;
                startMid = Mid();
                startSpan = Span();
            }

            window.RegisterCallback<PointerDownEvent>(e =>
            {
                touches[e.pointerId] = e.position;
                Rebase();
                // Ловим указатель только когда картинка увеличена: иначе
                // одиночный тап по кадру перестал бы доходить до кнопок.
                if (touches.Count > 1 || scale > 1f)
                {
                    window.CapturePointer(e.pointerId);
                    e.StopPropagation();
                }
            });

            window.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (!touches.ContainsKey(e.pointerId)) return;
                touches[e.pointerId] = e.position;
                if (touches.Count >= 2 && startSpan > 1f)
                {
                    scale = Mathf.Clamp(startScale * (Span() / startSpan), 1f, max);
                    shift = startShift + (Mid() - startMid);
                }
                else if (scale > 1f)
                {
                    shift = startShift + (Mid() - startMid);
                }
                else return;
                Paint();
                e.StopPropagation();
            });

            void Release(int id)
            {
                if (!touches.Remove(id)) return;
                if (window.HasPointerCapture(id)) window.ReleasePointer(id);
                // Отпустили один палец из двух — считаем заново от того, что
                // осталось, иначе кадр прыгает на середину оставшегося пальца.
                Rebase();
                if (scale <= 1.01f)
                {
                    scale = 1f;
                    shift = Vector2.zero;
                    Paint();
                }
            }

            window.RegisterCallback<PointerUpEvent>(e => Release(e.pointerId));
            window.RegisterCallback<PointerCancelEvent>(e => Release(e.pointerId));
            window.RegisterCallback<PointerLeaveEvent>(e =>
            {
                if (!window.HasPointerCapture(e.pointerId)) Release(e.pointerId);
            });

            // Окно поменяло размер — прежний сдвиг описывает уже не тот кадр.
            window.RegisterCallback<GeometryChangedEvent>(_ => Paint());
        }
    }
}
