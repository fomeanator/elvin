using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ГРАФИК СКОРОСТИ — последняя минута приёма и отдачи, посекундно.
    ///
    /// <para>Число «4,2 МБ/с» отвечает на «сколько сейчас», график — на «а
    /// идёт ли вообще»: ровная полка читается как «качает», обрыв до нуля —
    /// как «встало», зубцы — как «сеть дёргается». Игрок, который ждёт
    /// гигабайт, смотрит именно на форму, а не на цифру (просьба партнёра
    /// 08.09: «график скорости, приём и отдача»).</para>
    ///
    /// <para>Приём — площадь акцентом (это главное, что происходит), отдача —
    /// тонкая линия золотом поверх (синк событий — байты малые, но их видно).
    /// Шкала общая и плавающая: от пика за минуту, не меньше 64 КБ/с, иначе
    /// холостой шум рисовался бы горами.</para>
    ///
    /// <para>Секунды копятся из тиков любой частоты: снимки приходят раз в
    /// 300 мс, а ведро — секунда; пропущенные секунды (сон, фон) заполняются
    /// нулями, чтобы ось времени не врала.</para>
    /// </summary>
    internal sealed class TrafficChart : VisualElement
    {
        public const int Seconds = 60;

        private readonly float[] _down = new float[Seconds];
        private readonly float[] _up = new float[Seconds];
        private int _newest = Seconds - 1;
        private double _bucketStart = double.NaN;
        private float _bucketDown, _bucketUp;

        /// <summary>Байт в секунду за последнюю ПОЛНУЮ секунду.</summary>
        public float LastDown { get; private set; }
        public float LastUp { get; private set; }

        /// <summary>Пик за окно — для подписи и для шкалы.</summary>
        public float PeakDown { get; private set; }
        public float PeakUp { get; private set; }

        public TrafficChart()
        {
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
            schedule.Execute(MarkDirtyRepaint).Every(250);
        }

        /// <summary>Досыпать байты в текущую секунду; при переходе секунды
        /// ведро закрывается и уходит в историю.</summary>
        public void Add(double now, float downBytes, float upBytes)
        {
            if (double.IsNaN(_bucketStart)) _bucketStart = now;
            double elapsed = now - _bucketStart;
            if (elapsed >= 1.0)
            {
                int whole = (int)elapsed;
                Push(_bucketDown, _bucketUp);
                for (int i = 1; i < whole && i < Seconds; i++) Push(0f, 0f); // тишина — тоже история
                _bucketStart += whole;
                _bucketDown = 0f; _bucketUp = 0f;
            }
            _bucketDown += Mathf.Max(0f, downBytes);
            _bucketUp += Mathf.Max(0f, upBytes);
            MarkDirtyRepaint();
        }

        private void Push(float down, float up)
        {
            _newest = (_newest + 1) % Seconds;
            _down[_newest] = down; _up[_newest] = up;
            LastDown = down; LastUp = up;
            PeakDown = 0f; PeakUp = 0f;
            for (int i = 0; i < Seconds; i++)
            {
                if (_down[i] > PeakDown) PeakDown = _down[i];
                if (_up[i] > PeakUp) PeakUp = _up[i];
            }
        }

        /// <summary>Новый пакет — новая минута: старые горы к нему не относятся.</summary>
        public void Reset()
        {
            for (int i = 0; i < Seconds; i++) { _down[i] = 0f; _up[i] = 0f; }
            _bucketStart = double.NaN; _bucketDown = 0f; _bucketUp = 0f;
            LastDown = LastUp = PeakDown = PeakUp = 0f;
            MarkDirtyRepaint();
        }

        /// <summary>
        /// СРЕДНЯЯ СКОРОСТЬ ПРИЁМА за последние <paramref name="window"/> секунд —
        /// для оценки «осталось». Мгновенная скорость проваливается на мелких
        /// файлах (текст глав идёт по 60 КБ/с между мегабайтами арта), и
        /// «≈1 мин» на секунду превращалось в «≈13 мин» (ролик 09.09). Считает
        /// по целым секундам плюс прожитую часть текущей.
        /// </summary>
        public float AvgDown(float window)
        {
            if (double.IsNaN(_bucketStart)) return 0f;
            float lived = Mathf.Clamp((float)(Lvn.LvnClock.Wall() - _bucketStart), 0f, 1f);
            float sum = _bucketDown, time = lived;
            int whole = Mathf.Clamp(Mathf.CeilToInt(window - lived), 0, Seconds);
            for (int i = 0; i < whole; i++) { sum += _down[(_newest - i + Seconds) % Seconds]; time += 1f; }
            return time > 0.5f ? sum / time : 0f;
        }

        private const float FloorScale = 64f * 1024f;   // ниже пика не опускаемся: шум не гора
        private const float Inset = 6f;

        private void Draw(MeshGenerationContext mgc)
        {
            var p = mgc.painter2D;
            float w = resolvedStyle.width, h = resolvedStyle.height;
            if (w <= 2f || h <= 2f) return;
            float top = Inset, bottom = h - Inset, span = bottom - top;
            float scale = Mathf.Max(FloorScale, PeakDown, PeakUp);

            // Сетка: три полки, чтобы глаз мерил высоту гор.
            p.lineWidth = 1f;
            p.strokeColor = LvnTokens.Faint;
            for (int g = 1; g <= 3; g++)
            {
                float y = bottom - span * g / 4f;
                p.BeginPath(); p.MoveTo(new Vector2(0f, y)); p.LineTo(new Vector2(w, y)); p.Stroke();
            }
            // Дно.
            p.strokeColor = LvnTokens.Track;
            p.BeginPath(); p.MoveTo(new Vector2(0f, bottom)); p.LineTo(new Vector2(w, bottom)); p.Stroke();

            // Последняя точка — ТЕКУЩАЯ секунда, спроецированная на полную:
            // иначе кривая отстаёт на секунду и в момент открытия листа стоит
            // на нуле при живой скорости в подписи.
            double lived = double.IsNaN(_bucketStart) ? 0.0 : Mathf.Clamp((float)(Lvn.LvnClock.Wall() - _bucketStart), 0.25f, 1f);
            float liveDown = lived > 0.0 ? _bucketDown / (float)lived : 0f;
            float liveUp = lived > 0.0 ? _bucketUp / (float)lived : 0f;
            scale = Mathf.Max(scale, liveDown, liveUp);

            float step = w / Seconds;
            Vector2 At(int i, float[] series, float live)
            {
                float raw = i == Seconds ? live : series[(_newest + 1 + i) % Seconds]; // слева старое, справа свежее
                float v = Mathf.Clamp01(raw / scale);
                return new Vector2(i * step, bottom - v * span);
            }

            // Приём: площадь под кривой.
            var accent = LvnTokens.Accent;
            p.fillColor = UiColor.WithAlpha(accent, 0.22f);
            p.BeginPath();
            p.MoveTo(new Vector2(0f, bottom));
            for (int i = 0; i <= Seconds; i++) p.LineTo(At(i, _down, liveDown));
            p.LineTo(new Vector2(w, bottom));
            p.ClosePath();
            p.Fill();
            p.lineWidth = 2.5f;
            p.lineJoin = LineJoin.Round;
            p.strokeColor = accent;
            p.BeginPath();
            p.MoveTo(At(0, _down, liveDown));
            for (int i = 1; i <= Seconds; i++) p.LineTo(At(i, _down, liveDown));
            p.Stroke();

            // Отдача: линия золотом.
            p.lineWidth = 2f;
            p.strokeColor = LvnTokens.Gold;
            p.BeginPath();
            p.MoveTo(At(0, _up, liveUp));
            for (int i = 1; i <= Seconds; i++) p.LineTo(At(i, _up, liveUp));
            p.Stroke();

            // Точка «сейчас» на приёме.
            var last = At(Seconds, _down, liveDown);
            p.fillColor = accent;
            p.BeginPath(); p.Arc(last, 4f, 0f, 360f); p.Fill();
        }
    }
}
