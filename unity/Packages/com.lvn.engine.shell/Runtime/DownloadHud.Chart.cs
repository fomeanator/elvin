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
            // Перерисовка — ТОЛЬКО ПОКА ЛИСТ ОТКРЫТ. Расписание живёт всегда,
            // а тесселяция трёх кривых десять раз в секунду под свёрнутым
            // кружком — работа впустую, на телефоне заметная.
            schedule.Execute(() => { if (Live) MarkDirtyRepaint(); }).Every(100);
        }

        /// <summary>
        /// ПОТОК ИЛИ ВЕЛИЧИНА — две разные вещи в одной картинке.
        ///
        /// <para>Скорость КОПИТСЯ: сколько байт пришло за секунду, столько и
        /// высота. Кадры и память — величина: их за секунду не «набирается»,
        /// они просто есть. Складывая их, график рисовал шестьсот кадров при
        /// шестидесяти живых, и шкала уезжала в небо.</para>
        ///
        /// <para>Поэтому у величины бакет УСРЕДНЯЕТСЯ по числу замеров за
        /// секунду: столбик — среднее этой секунды, шаг ровно секунда.</para>
        /// </summary>
        public bool Averaging;

        /// <summary>Досыпать байты в текущую секунду; при переходе секунды
        /// ведро закрывается и уходит в историю.</summary>
        public void Add(double now, float downBytes, float upBytes)
        {
            if (double.IsNaN(_bucketStart)) _bucketStart = now;
            double elapsed = now - _bucketStart;
            if (elapsed >= 1.0)
            {
                int whole = (int)elapsed;
                float down = Averaging && _samples > 0 ? _bucketDown / _samples : _bucketDown;
                float up = Averaging && _samples > 0 ? _bucketUp / _samples : _bucketUp;
                Push(down, up);
                // Тишина — тоже история, НО не у величины: пропущенная секунда
                // не значит «ноль кадров», она значит «замера не было».
                for (int i = 1; i < whole && i < Seconds; i++) Push(Averaging ? down : 0f, Averaging ? up : 0f);
                _bucketStart += whole;
                _bucketDown = 0f; _bucketUp = 0f; _samples = 0;
            }
            _bucketDown += Mathf.Max(0f, downBytes);
            _bucketUp += Mathf.Max(0f, upBytes);
            _samples++;
            if (Live) MarkDirtyRepaint();
        }

        private int _samples;

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

        private const float DefaultFloor = 64f * 1024f;   // ниже пика не опускаемся: шум не гора

        /// <summary>ПОЛ ШКАЛЫ — чем меряем. У скорости это 64 КБ/с, у кадров —
        /// 60, у памяти — сотни мегабайт: без своего пола кривая кадров легла
        /// бы по нулю, потому что шестьдесят против килобайт неразличимо.</summary>
        public float Floor = DefaultFloor;
        private const float Inset = 6f;
        private float _live;          // скорость «сейчас» — сглаженная, от хоста
        private float _scaleShown;    // шкала, к которой график подходит плавно

        /// <summary>Лист открыт — график виден и обязан двигаться; свёрнут —
        /// копит секунды молча, без перерисовок.</summary>
        public bool Live { get; set; }

        /// <summary>Скорость «сейчас» для правого края кривой — сглаженная
        /// хостом (EMA), а не проекция долей секунды: проекция дёргалась от
        /// нуля до пика по десять раз в секунду.</summary>
        public void SetLive(float bytesPerSec) => _live = Mathf.Max(0f, bytesPerSec);

        private void Draw(MeshGenerationContext mgc)
        {
            var p = mgc.painter2D;
            float w = resolvedStyle.width, h = resolvedStyle.height;
            if (w <= 2f || h <= 2f) return;
            float top = Inset, bottom = h - Inset, span = bottom - top;

            // ШКАЛА ПОДХОДИТ К ЦЕЛИ ПЛАВНО. Пик ушёл за минуту — цель упала, и
            // кривая перескакивала вдвое вверх одним кадром. Теперь шкала идёт
            // к цели долей за перерисовку: гора растёт и оседает, а не прыгает.
            float target = Mathf.Max(Floor, PeakDown, PeakUp, _live);
            _scaleShown = _scaleShown <= 0f ? target : Mathf.Lerp(_scaleShown, target, 0.12f);
            float scale = Mathf.Max(_scaleShown, _live * 0.98f);

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

            // ВРЕМЯ ТЕЧЁТ, А НЕ ШАГАЕТ. Кривая сдвигается влево на прожитую долю
            // текущей секунды: раньше вся история прыгала на шаг раз в секунду,
            // и график «дёргался» даже при ровной скорости.
            float lived = double.IsNaN(_bucketStart) ? 0f : Mathf.Clamp01((float)(Lvn.LvnClock.Wall() - _bucketStart));
            float step = w / Seconds;
            float shift = lived * step;
            // Точка i = 0 — самая старая секунда, i = Seconds — «сейчас».
            float Raw(float[] series, int i)
                => i >= Seconds ? (ReferenceEquals(series, _down) ? _live : LastUp)
                   : series[(_newest + 1 + i) % Seconds];
            // Сглаживание по трём соседям: сырые секунды — частокол, а
            // скорость сети глазу нужна как гора.
            float Smooth(float[] series, int i)
            {
                float a = i > 0 ? Raw(series, i - 1) : Raw(series, i);
                float b = Raw(series, i);
                float c = i < Seconds ? Raw(series, i + 1) : b;
                return (a + 2f * b + c) / 4f;
            }
            Vector2 At(int i, float[] series)
                => new Vector2(i * step - shift, bottom - Mathf.Clamp01(Smooth(series, i) / scale) * span);

            // Приём: площадь под кривой и линия акцента.
            var accent = LvnTokens.Accent;
            p.fillColor = UiColor.WithAlpha(accent, 0.22f);
            p.BeginPath();
            p.MoveTo(new Vector2(-shift, bottom));
            for (int i = 0; i <= Seconds; i++) p.LineTo(At(i, _down));
            p.LineTo(new Vector2(w, At(Seconds, _down).y));
            p.LineTo(new Vector2(w, bottom));
            p.ClosePath();
            p.Fill();
            p.lineWidth = 2.5f;
            p.lineJoin = LineJoin.Round;
            p.strokeColor = accent;
            p.BeginPath();
            p.MoveTo(At(0, _down));
            for (int i = 1; i <= Seconds; i++) p.LineTo(At(i, _down));
            p.LineTo(new Vector2(w, At(Seconds, _down).y));
            p.Stroke();

            // Отдача: линия золотом, как в торрент-клиентах.
            p.lineWidth = 2f;
            p.strokeColor = LvnTokens.Gold;
            p.BeginPath();
            p.MoveTo(At(0, _up));
            for (int i = 1; i <= Seconds; i++) p.LineTo(At(i, _up));
            p.LineTo(new Vector2(w, At(Seconds, _up).y));
            p.Stroke();

            // Точка «сейчас» на приёме — у правого края.
            p.fillColor = accent;
            p.BeginPath(); p.Arc(new Vector2(w - 3f, At(Seconds, _down).y), 3.5f, 0f, 360f); p.Fill();
        }
    }
}
