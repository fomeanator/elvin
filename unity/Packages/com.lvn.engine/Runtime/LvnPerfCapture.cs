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

        internal string SlowReport()
        {
            if (_worstMs <= 0) return null;
            var b = new StringBuilder(1024);
            b.Append("[lvn-perf] slow frame=").Append(_worstFrame);
            Number(b, "frame_ms", _worstMs); Number(b, "budget_ms", _worstBudget);
            b.Append(" context=").Append(_worstContext ?? "unknown");
            for (int i = 0; i < _metricNames.Length; i++) Number(b, _metricNames[i], _worstMetrics[i]);
            Costs(b, _worstCosts);
            _worstMs = 0;
            return b.ToString();
        }

        internal string Summary()
        {
            if (_count == 0) return null;
            int samples = Math.Min(_count, _durations.Length);
            Array.Sort(_durations, 0, samples);
            var b = new StringBuilder(1600);
            b.Append("[lvn-perf] window frames=").Append(_count);
            b.Append(" first_frame=").Append(_firstFrame).Append(" last_frame=").Append(_lastFrame);
            b.Append(" seconds=").Append((_elapsed / 1000).ToString("F6", CultureInfo.InvariantCulture));
            Number(b, "fps", _count * 1000 / _elapsed);
            Number(b, "p50_ms", Percentile(samples, .50));
            Number(b, "p95_ms", Percentile(samples, .95));
            Number(b, "p99_ms", Percentile(samples, .99)); Number(b, "max_ms", _max);
            b.Append(" percentile_samples=").Append(samples).Append(" over_budget=").Append(_overBudget)
                .Append(" over50=").Append(_over50).Append(" over100=").Append(_over100)
                .Append(" gc_collections=").Append(_collections).Append(" invalid_spans=").Append(InvalidSpans);
            for (int i = 0; i < _metricNames.Length; i++)
            {
                Number(b, _metricNames[i] + "_avg", _metricCounts[i] > 0 ? _metricSums[i] / _metricCounts[i] : -1);
                Number(b, _metricNames[i] + "_max", _metricCounts[i] > 0 ? _metricMax[i] : -1);
            }
            Costs(b, _window);
            ResetWindow();
            return b.ToString();
        }

        private float Percentile(int samples, double p) => _durations[Math.Max(0, (int)Math.Ceiling(samples * p) - 1)];

        private void Costs(StringBuilder b, Cost[] costs)
        {
            Array.Clear(_picked, 0, _picked.Length);
            b.Append(" top_self_ms=[");
            for (int n = 0; n < 6; n++)
            {
                int best = -1;
                for (int i = 0; i < costs.Length; i++)
                    if (!_picked[i] && costs[i].Calls > 0 && (best < 0 || costs[i].Self > costs[best].Self)) best = i;
                if (best < 0) break;
                _picked[best] = true;
                if (n > 0) b.Append(';');
                var c = costs[best];
                b.Append((LvnPerf.Part)best).Append(':').Append(c.Self.ToString("F2", CultureInfo.InvariantCulture))
                    .Append("/total=").Append(c.Total.ToString("F2", CultureInfo.InvariantCulture))
                    .Append("/max=").Append(c.Max.ToString("F2", CultureInfo.InvariantCulture))
                    .Append("/calls=").Append(c.Calls).Append("/alloc_B=").Append(c.UnknownAlloc ? "na" : c.Bytes.ToString(CultureInfo.InvariantCulture));
            }
            b.Append("] top_alloc_B=[");
            Array.Clear(_picked, 0, _picked.Length);
            for (int n = 0; n < 3; n++)
            {
                int best = -1;
                for (int i = 0; i < costs.Length; i++)
                    if (!_picked[i] && !costs[i].UnknownAlloc && costs[i].Bytes > 0 && (best < 0 || costs[i].Bytes > costs[best].Bytes)) best = i;
                if (best < 0) break;
                _picked[best] = true;
                if (n > 0) b.Append(';');
                b.Append((LvnPerf.Part)best).Append(':').Append(costs[best].Bytes);
            }
            b.Append(']');
        }

        private static void Number(StringBuilder b, string key, double value)
            => b.Append(' ').Append(key).Append('=').Append(value < 0 ? "na" : value.ToString("F2", CultureInfo.InvariantCulture));

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
