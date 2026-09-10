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
            schedule.Execute(() =>
            {
                if (!Live) return;
                Glide(Lvn.LvnClock.Wall());
                MarkDirtyRepaint();
            }).Every(33);
        }

        /// <summary>Досыпать байты в текущую секунду; при переходе секунды
        /// ведро закрывается и уходит в историю.</summary>
        public void Add(double now, float downBytes, float upBytes)
        {
            // Отдача — своей EMA: хост даёт байты за тик, край кривой ждёт скорость.
            float dtAdd = double.IsNaN(_lastAddAt) ? 0f : (float)(now - _lastAddAt);
            _lastAddAt = now;
            if (dtAdd > 0.05f)
            {
                float upRate = Mathf.Max(0f, upBytes) / dtAdd;
                _liveUp = _liveUp <= 0f ? upRate : Mathf.Lerp(_liveUp, upRate, 0.35f);
            }
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
            if (Live) MarkDirtyRepaint();
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
            for (int i = 0; i < Samples; i++) { _sd[i] = 0f; _su[i] = 0f; }
            _sampleAt = double.NaN; _shownDown = _shownUp = 0f; _liveUp = 0f;
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
        private float _live;          // скорость «сейчас» — сглаженная, от хоста
        private float _scaleShown;    // шкала, к которой график подходит плавно

        // ВЫБОРКИ ДЛЯ РИСОВАНИЯ — не секундные корзины. Корзина закрывается
        // раз в секунду и приносит на край новое число одним шагом, а данные
        // приходят тиками раз в 300 мс — кривая у правого края прыгала до
        // 34 px за кадр («дёргается» — Илья 10.09). Теперь каждые 250 мс в
        // кольцо ложится СГЛАЖЕННАЯ скорость (EMA), между выборками кривая
        // скользит, а край подходит к свежему значению за несколько кадров.
        private const int Samples = 240;             // минута по 250 мс
        private const float SampleDt = 0.25f;
        private readonly float[] _sd = new float[Samples];
        private readonly float[] _su = new float[Samples];
        private int _sNewest = Samples - 1;
        private double _sampleAt = double.NaN;
        private float _shownDown, _shownUp;          // что нарисовано на краю сейчас
        private float _liveUp;                       // EMA отдачи — считается здесь
        private double _lastAddAt = double.NaN;
        private double _lastFrameAt = double.NaN;

        /// <summary>Шаг кадра: выборки по расписанию, край и шкала — к цели
        /// плавно. Зовётся перед каждой перерисовкой.</summary>
        private void Glide(double now)
        {
            float dt = double.IsNaN(_lastFrameAt) ? 0.033f : Mathf.Clamp((float)(now - _lastFrameAt), 0.001f, 0.25f);
            _lastFrameAt = now;
            float k = 1f - Mathf.Exp(-dt / 0.22f);     // постоянная времени ~220 мс
            _shownDown = Mathf.Lerp(_shownDown, _live, k);
            _shownUp = Mathf.Lerp(_shownUp, _liveUp, k);
            if (double.IsNaN(_sampleAt)) _sampleAt = now;
            while (now - _sampleAt >= SampleDt)
            {
                _sNewest = (_sNewest + 1) % Samples;
                _sd[_sNewest] = _shownDown; _su[_sNewest] = _shownUp;
                _sampleAt += SampleDt;
            }
        }

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
            float target = Mathf.Max(FloorScale, PeakDown, PeakUp, _shownDown, _shownUp);
            _scaleShown = _scaleShown <= 0f ? target : Mathf.Lerp(_scaleShown, target, 0.06f);
            float scale = Mathf.Max(_scaleShown, _shownDown, _shownUp);

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

            // ВРЕМЯ ТЕЧЁТ, А НЕ ШАГАЕТ: кривая сдвигается влево на прожитую
            // долю интервала выборки; край — сглаженное значение этого кадра.
            double now = Lvn.LvnClock.Wall();
            float lived = double.IsNaN(_sampleAt) ? 0f : Mathf.Clamp01((float)(now - _sampleAt) / SampleDt);
            float step = w / Samples;
            float shift = lived * step;
            // Точка i = 0 — самая старая выборка, i = Samples — край «сейчас».
            float Raw(float[] ring, float edge, int i) => i >= Samples ? edge : ring[(_sNewest + 1 + i) % Samples];
            float Smooth(float[] ring, float edge, int i)
            {
                float a = i > 0 ? Raw(ring, edge, i - 1) : Raw(ring, edge, i);
                float b = Raw(ring, edge, i);
                float c = Raw(ring, edge, i + 1);
                return (a + 2f * b + c) / 4f;
            }
            Vector2 At(int i, float[] ring, float edge)
                => new Vector2(i * step - shift, bottom - Mathf.Clamp01(Smooth(ring, edge, i) / scale) * span);

            // Приём: площадь под кривой и линия акцента.
            var accent = LvnTokens.Accent;
            p.fillColor = UiColor.WithAlpha(accent, 0.22f);
            p.BeginPath();
            p.MoveTo(new Vector2(-shift, bottom));
            for (int i = 0; i <= Samples; i++) p.LineTo(At(i, _sd, _shownDown));
            p.LineTo(new Vector2(w, At(Samples, _sd, _shownDown).y));
            p.LineTo(new Vector2(w, bottom));
            p.ClosePath();
            p.Fill();
            p.lineWidth = 2.5f;
            p.lineJoin = LineJoin.Round;
            p.strokeColor = accent;
            p.BeginPath();
            p.MoveTo(At(0, _sd, _shownDown));
            for (int i = 1; i <= Samples; i++) p.LineTo(At(i, _sd, _shownDown));
            p.LineTo(new Vector2(w, At(Samples, _sd, _shownDown).y));
            p.Stroke();

            // Отдача: линия золотом, как в торрент-клиентах.
            p.lineWidth = 2f;
            p.strokeColor = LvnTokens.Gold;
            p.BeginPath();
            p.MoveTo(At(0, _su, _shownUp));
            for (int i = 1; i <= Samples; i++) p.LineTo(At(i, _su, _shownUp));
            p.LineTo(new Vector2(w, At(Samples, _su, _shownUp).y));
            p.Stroke();

            // Точка «сейчас» на приёме — у правого края.
            p.fillColor = accent;
            p.BeginPath(); p.Arc(new Vector2(w - 3f, At(Samples, _sd, _shownDown).y), 3.5f, 0f, 360f); p.Fill();
        }
    }
}
