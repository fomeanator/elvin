using System;
using System.Globalization;
using System.Text;

namespace Lvn
{
    /// <summary>Fixed-size, main-thread frame accumulator. No allocation until a report is requested.</summary>
    internal sealed class LvnPerfCapture
    {
        internal struct Cost
        {
            internal double Total, Self, Max;
            internal long Bytes;
            internal int Calls;
            internal bool UnknownAlloc;

            internal void Add(double total, double self, long bytes)
            {
                Total += total; Self += self; Max = Math.Max(Max, total);
                if (bytes < 0) UnknownAlloc = true; else Bytes += bytes;
                Calls++;
            }

            internal void Add(Cost other)
            {
                Total += other.Total; Self += other.Self; Max = Math.Max(Max, other.Max);
                Bytes += other.Bytes; Calls += other.Calls;
                UnknownAlloc |= other.UnknownAlloc;
            }
        }

        private sealed class Frame
        {
            internal int Id = -1;
            internal string Context;
            internal readonly Cost[] Costs = new Cost[(int)LvnPerf.Part.Count];
        }

        private readonly Frame[] _frames = { new Frame(), new Frame(), new Frame(), new Frame() };
        private readonly Cost[] _window = new Cost[(int)LvnPerf.Part.Count];
        private readonly Cost[] _worstCosts = new Cost[(int)LvnPerf.Part.Count];
        private readonly double[] _worstMetrics;
        private readonly string[] _metricNames;
        private readonly double[] _metricSums, _metricMax;
        private readonly int[] _metricCounts;
        private readonly float[] _durations = new float[8192];
        private readonly bool[] _picked = new bool[(int)LvnPerf.Part.Count];
        private int _count, _overBudget, _over50, _over100, _collections, _worstFrame, _firstFrame, _lastFrame;
        private double _elapsed, _max, _worstMs, _worstBudget;
        private string _worstContext;
        internal int Count => _count;
        internal int InvalidSpans;

        internal LvnPerfCapture(string[] metricNames)
        {
            _metricNames = metricNames;
            _worstMetrics = new double[metricNames.Length];
            _metricSums = new double[metricNames.Length];
            _metricMax = new double[metricNames.Length];
            _metricCounts = new int[metricNames.Length];
        }

        private Frame For(int id)
        {
            var f = _frames[(id & int.MaxValue) % _frames.Length];
            if (f.Id == id) return f;
            f.Id = id; f.Context = null;
            Array.Clear(f.Costs, 0, f.Costs.Length);
            return f;
        }

        internal void Context(int frame, string context) => For(frame).Context = context;

        internal void Span(int frame, LvnPerf.Part part, double total, double self, long bytes)
            => For(frame).Costs[(int)part].Add(total, Math.Max(0, self), bytes);

        // Called at Update N for completed frame N-1. Scopes already recorded
        // in N by earlier scripts live in another slot and are never erased.
        internal void EndFrame(int frame, float ms, double budget, int collections, double[] metrics)
        {
            var f = For(frame);
            if (ms <= 0 || float.IsNaN(ms) || float.IsInfinity(ms)) return;
            if (_count == 0) _firstFrame = frame;
            _lastFrame = frame;
            _durations[_count % _durations.Length] = ms;
            _count++; _elapsed += ms; _max = Math.Max(_max, ms);
            _collections += Math.Max(0, collections);
            bool slow = ms > budget * 1.25;
            if (slow) _overBudget++;
            if (ms > 50) _over50++;
            if (ms > 100) _over100++;
            for (int i = 0; i < _window.Length; i++) _window[i].Add(f.Costs[i]);
            for (int i = 0; i < metrics.Length; i++)
                if (metrics[i] >= 0)
                {
                    _metricSums[i] += metrics[i]; _metricCounts[i]++;
                    _metricMax[i] = Math.Max(_metricMax[i], metrics[i]);
                }
            if (slow && ms > _worstMs)
            {
                _worstMs = ms; _worstFrame = frame; _worstBudget = budget;
                _worstContext = f.Context;
                Array.Copy(f.Costs, _worstCosts, f.Costs.Length);
                Array.Copy(metrics, _worstMetrics, metrics.Length);
            }
            f.Id = -1;
        }

        // КОДЫ ВМЕСТО ТЕКСТА (TR-86, Илья 15.09: «на логах экономить — вместо
        // текста коды»). Строка «slow» весила ~900 байт и уходила раз в 2 с:
        // повторённые run/build, семнадцать метрик с «na», шесть частей с
        // четырьмя полями каждая. 90 % дневного объёма логов на сервере были
        // этими строками. Теперь: короткие ключи (таблица — docs/client-logs.md),
        // неизвестные метрики не пишутся вовсе, части — «имя:своё/всего/вызовов».
        /// <summary>Худший медленный кадр с прошлого отчёта; <paramref name="minMs"/>
        /// — пол: кадры короче не стоят строки (окно их и так считает).</summary>
        internal string SlowReport(double minMs = 0)
        {
            if (_worstMs <= 0) return null;
            if (_worstMs < minMs) { _worstMs = 0; return null; }
            var b = new StringBuilder(320);
            b.Append("[lvn-perf] S f=").Append(_worstFrame);
            Number(b, "ms", _worstMs); Number(b, "b", _worstBudget);
            b.Append(" ctx=").Append(_worstContext ?? "unknown");
            for (int i = 0; i < _metricNames.Length; i++) Number(b, _metricNames[i], _worstMetrics[i]);
            Costs(b, _worstCosts, 3);
            _worstMs = 0;
            return b.ToString();
        }

        internal string Summary()
        {
            if (_count == 0) return null;
            int samples = Math.Min(_count, _durations.Length);
            Array.Sort(_durations, 0, samples);
            var b = new StringBuilder(512);
            b.Append("[lvn-perf] W n=").Append(_count);
            b.Append(" f0=").Append(_firstFrame).Append(" f1=").Append(_lastFrame);
            Number(b, "s", _elapsed / 1000);
            Number(b, "fps", _count * 1000 / _elapsed);
            Number(b, "p50", Percentile(samples, .50));
            Number(b, "p95", Percentile(samples, .95));
            Number(b, "p99", Percentile(samples, .99)); Number(b, "max", _max);
            b.Append(" ps=").Append(samples).Append(" ob=").Append(_overBudget)
                .Append(" o50=").Append(_over50).Append(" o100=").Append(_over100)
                .Append(" gc=").Append(_collections).Append(" inv=").Append(InvalidSpans);
            for (int i = 0; i < _metricNames.Length; i++)
                if (_metricCounts[i] > 0)
                    b.Append(' ').Append(_metricNames[i]).Append('=')
                     .Append(F1(_metricSums[i] / _metricCounts[i])).Append('/').Append(F1(_metricMax[i]));
            Costs(b, _window, 4);
            ResetWindow();
            return b.ToString();
        }

        private float Percentile(int samples, double p) => _durations[Math.Max(0, (int)Math.Ceiling(samples * p) - 1)];

        private void Costs(StringBuilder b, Cost[] costs, int limit)
        {
            Array.Clear(_picked, 0, _picked.Length);
            b.Append(" top=");
            for (int n = 0; n < limit; n++)
            {
                int best = -1;
                for (int i = 0; i < costs.Length; i++)
                    if (!_picked[i] && costs[i].Calls > 0 && (best < 0 || costs[i].Self > costs[best].Self)) best = i;
                if (best < 0) break;
                _picked[best] = true;
                if (n > 0) b.Append(';');
                var c = costs[best];
                b.Append((LvnPerf.Part)best).Append(':').Append(F1(c.Self)).Append('/').Append(F1(c.Total)).Append('/').Append(c.Calls);
                if (!c.UnknownAlloc && c.Bytes > 0) b.Append('/').Append(c.Bytes.ToString(CultureInfo.InvariantCulture)).Append('B');
            }
        }

        private static string F1(double value) => value.ToString("F1", CultureInfo.InvariantCulture);

        private static void Number(StringBuilder b, string key, double value)
        {
            if (value < 0) return;   // неизвестная метрика — не «na», а ничего
            b.Append(' ').Append(key).Append('=').Append(F1(value));
        }

        private void ResetWindow()
        {
            _count = _overBudget = _over50 = _over100 = _collections = InvalidSpans = 0;
            _elapsed = _max = 0;
            Array.Clear(_window, 0, _window.Length);
            Array.Clear(_metricSums, 0, _metricSums.Length);
            Array.Clear(_metricCounts, 0, _metricCounts.Length);
            Array.Clear(_metricMax, 0, _metricMax.Length);
        }

        internal void Reset()
        {
            ResetWindow(); _worstMs = 0;
            foreach (var f in _frames) f.Id = -1;
        }
    }
}
