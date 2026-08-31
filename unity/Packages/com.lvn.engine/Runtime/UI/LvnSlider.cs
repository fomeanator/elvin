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
        // Высота дорожки и размер кружка — числа, из которых считается всё
        // остальное. Пока они стояли по месту, центрирование приходилось
        // выводить заново в каждой формуле, и одна из них ошибалась.
        private const float TrackHeight = 8f;
        private const float KnobSize = 28f;

        public static Slider Make(float min, float max, float value,
                                  Action<float> onApply, Action<float> onPreview = null,
                                  Color? accent = null, Color? track = null)
        {
            var s = new Slider(min, max) { value = value };
            var acc = accent ?? LvnTokens.Accent;
            s.style.height = 40;
            s.style.marginTop = LvnTokens.Space1;

            VisualElement fill = null;
            var tracker = s.Q("unity-tracker");
            if (tracker != null)
            {
                tracker.style.height = TrackHeight;
                tracker.style.marginTop = LvnTokens.Space3;
                tracker.style.backgroundColor = track ?? LvnTokens.Track;
                // Кружок вчетверо выше дорожки и торчит за её края — это
                // нормально и должно быть видно: обрезка превратила бы его в
                // полоску той же высоты, что дорожка.
                tracker.style.overflow = Overflow.Visible;
                LvnChrome.Round(tracker, LvnTokens.RadiusXs);
                LvnChrome.ClearBorder(tracker);
                fill = new VisualElement { pickingMode = PickingMode.Ignore };
                fill.style.position = Position.Absolute;
                fill.style.left = 0; fill.style.top = 0; fill.style.bottom = 0;
                fill.style.backgroundColor = acc;
                LvnChrome.Round(fill, LvnTokens.RadiusXs);
                tracker.Add(fill);
            }
            // БЕГУНОК И ЗАЛИВКА ЖИВУТ В ОДНОЙ СИСТЕМЕ КООРДИНАТ.
            //
            // Раньше их было две: заливку мы считали процентом от дорожки, а
            // бегунок двигал сам UI Toolkit — по своей формуле, где доля
            // умножается на ширину МИНУС ширина бегунка (иначе он вылезал бы за
            // край). Формулы сходятся ровно в одной точке — посередине, — а по
            // краям расходятся на половину бегунка: кружок стоял правее конца
            // заполненной части, а на максимуме заезжал за саму дорожку (живой
            // скрин Ильи 29.08, «кружочки съезжают»).
            //
            // Спорить с чужой формулой бессмысленно — она сработает снова при
            // следующем пересчёте. Поэтому штатный бегунок остаётся тем, чем он
            // и нужен, — областью захвата пальца, — но не рисуется; а видимый
            // кружок живёт ВНУТРИ дорожки и позиционируется тем же процентом,
            // что и заливка. Совпасть они теперь не могут иначе как точно.
            var dragger = s.Q("unity-dragger");
            if (dragger != null)
            {
                // Крупнее стандартного: палец не мышь, и промах по бегунку
                // ощущается как «ползунок не слушается». Размер остаётся —
                // это область захвата; прозрачность скрывает только вид.
                dragger.style.width = KnobSize; dragger.style.height = KnobSize;
                dragger.style.opacity = 0f;
                LvnChrome.ClearBorder(dragger);
            }

            VisualElement knob = null;
            if (tracker != null)
            {
                knob = new VisualElement { pickingMode = PickingMode.Ignore };
                knob.style.position = Position.Absolute;
                knob.style.width = KnobSize; knob.style.height = KnobSize;
                knob.style.backgroundColor = acc;
                // ЦЕНТР КРУЖКА — НА ДОРОЖКЕ, И СЧИТАЕТСЯ В ПИКСЕЛЯХ.
                //
                // Сначала центрирование было записано процентами (top 50% плюс
                // translate −50%), и кружок уехал ВНИЗ: доля от высоты дорожки
                // легла, а обратное смещение на свою половину — нет. Проценты
                // тут вообще лишние: обе высоты известны числом прямо здесь,
                // и разница между ними — это и есть весь сдвиг.
                //
                // Отрицательные отступы законны: кружок вчетверо выше дорожки
                // и обязан выходить за неё сверху и снизу поровну.
                knob.style.top = -(KnobSize - TrackHeight) * 0.5f;
                knob.style.marginLeft = -KnobSize * 0.5f;   // процент задаёт центр, а не левый край
                LvnChrome.Round(knob, KnobSize * 0.5f);
                LvnChrome.ClearBorder(knob);
                tracker.Add(knob);
            }

            void Paint(float v)
            {
                float t = Mathf.Clamp01(Mathf.Approximately(max, min)
                    ? 0f : (v - min) / (max - min));
                if (fill != null) fill.style.width = Length.Percent(t * 100f);
                if (knob != null) knob.style.left = Length.Percent(t * 100f);
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
                if (knob != null) LvnMotion.FadeIn(knob, delayMs: 0, ms: LvnMotion.Quick);
            }
            // Отпускание ловим и на самом ползунке, и на потере захвата: палец
            // часто уходит за пределы дорожки, и события отпускания там уже нет.
            s.RegisterCallback<PointerUpEvent>(_ => Apply());
            s.RegisterCallback<PointerCaptureOutEvent>(_ => Apply());
            return s;
        }
    }
}
