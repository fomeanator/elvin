using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// ПОЛЗУНОК — вид и правило «когда значение считается выбранным».
    ///
    /// <para>Вид собирался в двух местах (настройки оболочки и меню в главе) и
    /// расходился: стандартный ползунок UI Toolkit на тёмной панели выглядит
    /// элементом из другой программы, и каждый экран правил его по-своему.</para>
    ///
    /// <para>Важнее вида — МОМЕНТ ПРИМЕНЕНИЯ. Значение применялось на каждое
    /// движение пальца: громкость дёргалась десятки раз за перетаскивание, а
    /// настройки, за которыми стоит пересборка экрана, успевали перестроить его
    /// прямо под пальцем — отсюда «ползунки ненадёжные и реагируют плохо»
    /// (Илья, 28.08).</para>
    ///
    /// <para>Поэтому здесь два разных ответа. ЗАЛИВКА идёт за пальцем сразу —
    /// это обратная связь, без неё ползунок кажется мёртвым. А ПРИМЕНЕНИЕ
    /// случается при отпускании: игрок выбрал значение тогда, когда отпустил, а
    /// не когда провёл через него.</para>
    ///
    /// <para>Предпросмотр (<paramref name="onPreview"/>) — для того, что обязано
    /// меняться на ходу: громкость слышна только вживую, и ждать отпускания
    /// значит выбирать её вслепую.</para>
    /// </summary>
    public static class LvnSlider
    {
        public static Slider Make(float min, float max, float value,
                                  Action<float> onApply, Action<float> onPreview = null,
                                  Color? accent = null, Color? track = null)
        {
            var s = new Slider(min, max) { value = value };
            var acc = accent ?? LvnTokens.Accent;
            s.style.height = 40;
            s.style.marginTop = 6;

            VisualElement fill = null;
            var tracker = s.Q("unity-tracker");
            if (tracker != null)
            {
                tracker.style.height = 8;
                tracker.style.marginTop = 16;
                tracker.style.backgroundColor = track ?? LvnTokens.Track;
                LvnChrome.Round(tracker, 4f);
                LvnChrome.ClearBorder(tracker);
                fill = new VisualElement { pickingMode = PickingMode.Ignore };
                fill.style.position = Position.Absolute;
                fill.style.left = 0; fill.style.top = 0; fill.style.bottom = 0;
                fill.style.backgroundColor = acc;
                LvnChrome.Round(fill, 4f);
                tracker.Add(fill);
            }
            var dragger = s.Q("unity-dragger");
            if (dragger != null)
            {
                // Крупнее стандартного: палец не мышь, и промах по бегунку
                // ощущается как «ползунок не слушается».
                dragger.style.width = 28; dragger.style.height = 28;
                dragger.style.backgroundColor = acc;
                LvnChrome.Round(dragger, 14f);
                LvnChrome.ClearBorder(dragger);
            }

            // БЕГУНОК ДЕРЖИТСЯ НА ДОРОЖКЕ. Раньше его вертикаль задавалась
            // числом, подобранным под одну высоту строки: в другом ряду (или
            // при другом кегле) он съезжал выше или ниже дорожки. Теперь центр
            // считается по фактической геометрии — на любой высоте и после
            // любого пересчёта раскладки.
            void CenterDragger()
            {
                if (dragger == null || tracker == null || dragger.parent == null) return;
                float trackMid = dragger.parent.WorldToLocal(tracker.worldBound.center).y;
                float h = dragger.resolvedStyle.height > 0f ? dragger.resolvedStyle.height : 28f;
                dragger.style.top = trackMid - h * 0.5f;
            }
            s.RegisterCallback<GeometryChangedEvent>(_ => CenterDragger());

            void Paint(float v)
            {
                if (fill != null)
                    fill.style.width = Length.Percent(
                        Mathf.Clamp01(Mathf.Approximately(max, min) ? 0f : (v - min) / (max - min)) * 100f);
            }
            Paint(value);

            float pending = value;
            s.RegisterValueChangedCallback(e =>
            {
                pending = e.newValue;
                Paint(e.newValue);
                onPreview?.Invoke(e.newValue);   // то, что слышно/видно только вживую
            });

            void Apply()
            {
                onApply?.Invoke(pending);
                // Отклик на отпускание: бегунок коротко проступает — «дошло».
                // Без него игрок не понимает, засчиталось ли, и дёргает ползунок
                // ещё раз.
                if (dragger != null) LvnMotion.FadeIn(dragger, delayMs: 0, ms: LvnMotion.Quick);
            }
            // Отпускание ловим и на самом ползунке, и на потере захвата: палец
            // часто уходит за пределы дорожки, и события отпускания там уже нет.
            s.RegisterCallback<PointerUpEvent>(_ => Apply());
            s.RegisterCallback<PointerCaptureOutEvent>(_ => Apply());
            return s;
        }
    }
}
