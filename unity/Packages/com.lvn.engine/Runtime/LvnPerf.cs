using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using Unity.Profiling;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Lvn
{
    /// <summary>
    /// Field performance diagnostics, independent of verbose logging. Measure
    /// synchronous main-thread work only: never hold a scope across an await.
    /// Self time excludes nested instrumented work; total includes it. Neither
    /// is CPU utilization: a synchronous disk/GPU wait also blocks this thread.
    /// </summary>
    public static class LvnPerf
    {
        public enum Part
        {
            ActorUpdate, ActorBuild, ActorComposite, SpriteFx, StageUpdate, StageReconcile,
            SpineBuild, SpineFit, PosterRender, TextureDecode, TextureResize, SpriteCreate,
            UiRedress, FontBuild, FontGlyphs, LocaleApply, SaveSerialize, PrefsSave,
            OutboxPersist, LogEnqueue, LogSerialize, Diagnostics,
            PosterVisibility, StageInput, StageHealer, StageDriftCheck, Count
        }

        private struct OpenScope
        {
            internal long Token, Started, Allocated, ChildTicks, ChildBytes;
            internal int Frame;
            internal Part Part;
        }

        public readonly struct Scope : IDisposable
        {
            private readonly long _token;
            internal Scope(long token) => _token = token;
            public void Dispose() { if (_token != 0) End(_token); }
        }

        private sealed class Metric
        {
            internal readonly string Key, Code, Name;   // Code — короткий ключ в строках S/W (docs/client-logs.md)
            internal readonly ProfilerCategory Category;
            internal readonly double Scale;
            internal ProfilerRecorder Recorder;
            internal Metric(string key, string code, ProfilerCategory category, string name, double scale = 1)
            { Key = key; Code = code; Category = category; Name = name; Scale = scale; }
        }

        private static readonly Metric[] _metrics =
        {
            new Metric("main_ms", "m", ProfilerCategory.Internal, "Main Thread", 1e-6),
            new Metric("render_ms", "r", ProfilerCategory.Internal, "Render Thread", 1e-6),
            new Metric("present_wait_ms", "pw", ProfilerCategory.Render, "Gfx.WaitForPresentOnGfxThread", 1e-6),
            new Metric("fps_wait_ms", "fw", ProfilerCategory.Internal, "WaitForTargetFPS", 1e-6),
            new Metric("ui_panels_ms", "up", new ProfilerCategory("PlayerLoop"), "PreLateUpdate.UIElementsUpdatePanels", 1e-6),
            new Metric("ui_layout_ms", "ul", ProfilerCategory.Scripts, "UIElements.UpdateLayout", 1e-6),
            new Metric("ui_render_ms", "ur", new ProfilerCategory("PlayerLoop"), "PostLateUpdate.UIElementsRepaintPanels", 1e-6),
            new Metric("ui_style_ms", "us", ProfilerCategory.Scripts, "UIElements.UpdateStyle", 1e-6),
            new Metric("ui_geometry_ms", "ug", ProfilerCategory.Scripts, "UIElements.UpdateRenderData", 1e-6),
            new Metric("text_prepare_ms", "tp", ProfilerCategory.Scripts, "TextJob.PrepareMainThread", 1e-6),
            new Metric("glyph_ms", "gl", ProfilerCategory.Scripts, "FontAsset.TryAddGlyph", 1e-6),
            new Metric("gc_alloc_B", "ga", ProfilerCategory.Memory, "GC Allocated In Frame"),
            new Metric("gc_used_MB", "gcm", ProfilerCategory.Memory, "GC Used Memory", 1.0 / (1024 * 1024)),
            new Metric("total_used_MB", "mem", ProfilerCategory.Memory, "Total Used Memory", 1.0 / (1024 * 1024)),
            new Metric("draw_calls", "dc", ProfilerCategory.Render, "Draw Calls Count"),
            new Metric("batches", "bt", ProfilerCategory.Render, "Batches Count"),
            new Metric("triangles", "tri", ProfilerCategory.Render, "Triangles Count")
        };
        private static readonly double[] _values = new double[_metrics.Length];
        private static readonly OpenScope[] _stack = new OpenScope[64];
        private static readonly string[] _tail = new string[128];
        private static readonly object[] _logArgs = new object[1];
        private static readonly ProfilerMarker[] _markers = MakeMarkers();
        private static LvnPerfCapture _capture;
        private static int _thread, _depth, _lastFrame = -1, _skip, _gc, _tailIndex, _tailCount;
        private static long _token;
        private static double _nextSlow, _nextSummary;
        private static bool _paused, _focused = true;
        private static bool _retryCounters;
        private static double _retryAt;
        private static ProfilerRecorder _allocationRecorder;
        private static string _allocationSource = "na";
        private static string _identity;
        public static bool Enabled { get; private set; }

        /// <summary>Пол строки «медленный кадр» (TR-86): кадры до 100 мс — обычные
        /// запинки интерфейса, их считает окно (o50/o100/max); строку с контекстом
        /// получает только заметный. Живой лог 16.09: при поле 50 строки S весили
        /// две трети сессии, половина из них — 50–100 мс на простое.</summary>
        public const double SlowFloorMs = 100;
        public static string SessionInfo { get; private set; }

        /// <summary>Main-thread context supplied by the host at the end of its frame. No UI callback in the core.</summary>
        public static string Context
        {
            get => _context;
            set
            {
                _context = value;
                if (Enabled) _capture.Context(Time.frameCount, value);
            }
        }
        private static string _context;

        private static ProfilerMarker[] MakeMarkers()
        {
            var result = new ProfilerMarker[(int)Part.Count];
            for (int i = 0; i < result.Length; i++) result[i] = new ProfilerMarker("LVN." + (Part)i);
            return result;
        }

        /// <summary>Enable once on the main thread, before the first boot panel.</summary>
        public static void Start()
        {
            if (Enabled) return;
            _thread = Thread.CurrentThread.ManagedThreadId;
            PrepareAllocationCounter();
            var names = new string[_metrics.Length];
            var available = new StringBuilder();
            for (int i = 0; i < _metrics.Length; i++)
            {
                var m = _metrics[i]; names[i] = m.Code;
                Bind(m);
                if (i > 0) available.Append(',');
                available.Append(m.Key).Append('(').Append(m.Code).Append("):").Append(m.Recorder.Valid ? "yes" : "na");
            }
            _capture = new LvnPerfCapture(names);
            _depth = 0; _lastFrame = -1; _skip = 2; _paused = false; _focused = true;
            _gc = Collections();
            _nextSlow = Time.realtimeSinceStartupAsDouble + 2;
            _nextSummary = Time.realtimeSinceStartupAsDouble + 10;
            _retryCounters = true; _retryAt = Time.realtimeSinceStartupAsDouble + 0.25;
            Enabled = true;
            // Persist the producing run/build in the message: an offline queue
            // can be shipped by a later launch, even after an APK update.
            _identity = " run=" + LvnMark.Run + " build=" + Uri.EscapeDataString(Application.version);
            SessionInfo = "[lvn-perf] session v=1 app=" + Application.version
                + " commit=" + LvnBuildInfo.Commit + " channel=" + LvnBuildInfo.Channel
                + " unity=" + Application.unityVersion
                + " device=" + LvnDeviceProfile.Model + " gpu=" + LvnDeviceProfile.Gpu
                + " screen=" + Screen.width + "x" + Screen.height + " target_fps=" + Application.targetFrameRate
                + " refresh_hz=" + LvnDeviceProfile.RefreshHz.ToString("F1", CultureInfo.InvariantCulture)
                + " vsync=" + QualitySettings.vSyncCount + " verbose=" + LvnLog.Verbose
                + " counters=" + available + " gpu_time=not_sampled timings=main_thread_wall self=exclusive"
                + " scope_alloc=" + _allocationSource + " slow_interval_s=2 window_s=10"
                + " slow_floor_ms=" + SlowFloorMs + " format=v2";
            Emit(SessionInfo);
        }

        public static void Stop()
        {
            if (!Enabled) return;
            Flush();
            Enabled = false;
            foreach (var m in _metrics) { if (m.Recorder.Valid) m.Recorder.Dispose(); m.Recorder = default; }
            if (_allocationRecorder.Valid) _allocationRecorder.Dispose();
            _allocationRecorder = default;
            ClearScopes();
        }

        public static Scope Measure(Part part)
        {
            if (!Enabled || _paused || !_focused || Thread.CurrentThread.ManagedThreadId != _thread
                || (uint)part >= (uint)Part.Count) return default;
            if (_depth == _stack.Length) { _capture.InvalidSpans++; return default; }
            int frame = Time.frameCount;
            _capture.Context(frame, Context);
            var token = ++_token;
            _markers[(int)part].Begin();
            _stack[_depth++] = new OpenScope
            {
                Token = token, Part = part, Frame = frame, Started = Stopwatch.GetTimestamp(),
                Allocated = AllocatedBytes
            };
            return new Scope(token);
        }

        private static void End(long token)
        {
            if (!Enabled || Thread.CurrentThread.ManagedThreadId != _thread) return;
            // A stale copy or an await-spanning scope cannot corrupt later scopes.
            if (_depth == 0 || _stack[_depth - 1].Token != token) return;
            var s = _stack[--_depth];
            long ticks = Stopwatch.GetTimestamp() - s.Started;
            long bytes = _allocationSource == "na" ? -1 : AllocatedBytes - s.Allocated;
            _markers[(int)s.Part].End();
            if (s.Frame != Time.frameCount) { _capture.InvalidSpans++; return; }
            _capture.Span(s.Frame, s.Part, ticks * (1000.0 / Stopwatch.Frequency),
                (ticks - s.ChildTicks) * (1000.0 / Stopwatch.Frequency), bytes < 0 ? -1 : bytes - s.ChildBytes);
            if (_depth > 0)
            {
                _stack[_depth - 1].ChildTicks += ticks;
                if (bytes >= 0) _stack[_depth - 1].ChildBytes += bytes;
            }
        }

        /// <summary>One call per Update. The delta and recorders describe the completed preceding frame.</summary>
        public static void Frame(float delta, int frame)
        {
            if (!Enabled || frame == _lastFrame) return;
            if (_depth > 0 && _stack[0].Frame != frame)
            { _capture.InvalidSpans += _depth; ClearScopes(); }
            _lastFrame = frame;
            _capture.Context(frame, Context);
            int gc = Collections(), collected = Math.Max(0, gc - _gc); _gc = gc;
            if (_paused || !_focused) return;
            if (_skip > 0) { _skip--; return; }
            using var timing = Measure(Part.Diagnostics);
            for (int i = 0; i < _metrics.Length; i++)
            {
                var m = _metrics[i];
                _values[i] = m.Recorder.Valid && m.Recorder.Count > 0 ? m.Recorder.LastValue * m.Scale : -1;
            }
            _capture.EndFrame(frame - 1, delta * 1000, BudgetMs(), collected, _values);
            double now = Time.realtimeSinceStartupAsDouble;
            // Several UITK markers are registered only when the first panel
            // is rendered. A one-time retry binds them without polling names.
            if (_retryCounters && now >= _retryAt)
            {
                _retryCounters = false;
                var available = new StringBuilder();
                foreach (var m in _metrics)
                {
                    if (!m.Recorder.Valid) Bind(m);
                    if (available.Length > 0) available.Append(',');
                    available.Append(m.Key).Append(':').Append(m.Recorder.Valid ? "yes" : "na");
                }
                Event("counters-ready", available.ToString());
            }
            if (now >= _nextSlow) { _nextSlow = now + 2; Emit(_capture.SlowReport(SlowFloorMs)); }
            if (now >= _nextSummary) { _nextSummary = now + 10; Emit(_capture.Summary()); }
        }

        internal static double BudgetMs()
        {
            double refresh = LvnDeviceProfile.RefreshHz;
            if (refresh <= 0) refresh = 60;
            int target = Application.targetFrameRate;
            if (Application.isMobilePlatform)
            {
                if (target <= 0) target = 30;
                return 1000 / (refresh / Math.Max(1, Math.Ceiling(refresh / target)));
            }
            if (QualitySettings.vSyncCount > 0) return 1000 * QualitySettings.vSyncCount / refresh;
            return target > 0 ? 1000.0 / target : 1000 / refresh;
        }

        public static void Pause(bool paused) { _paused = paused; SuspendBoundary(); }
        public static void Focus(bool focused) { _focused = focused; SuspendBoundary(); }

        private static void SuspendBoundary()
        {
            if (!Enabled) return;
            Flush(); _capture.Reset(); _skip = 2; _gc = Collections();
            _nextSlow = Time.realtimeSinceStartupAsDouble + 2;
            _nextSummary = Time.realtimeSinceStartupAsDouble + 10;
        }

        private static void ClearScopes()
        {
            while (_depth > 0) _markers[(int)_stack[--_depth].Part].End();
        }

        private static int Collections() => GC.CollectionCount(0);

        private static void Bind(Metric m)
        {
            try
            {
                m.Recorder = ProfilerRecorder.StartNew(m.Category, m.Name, 1,
                    ProfilerRecorderOptions.Default | ProfilerRecorderOptions.SumAllSamplesInFrame);
            }
            catch { m.Recorder = default; }
        }

        // Some Unity Mono versions expose the managed API but always return
        // zero. Prove the counter with two real allocations before trusting it.
        internal static long AllocatedBytes
        {
            get
            {
#if !ENABLE_IL2CPP
                if (_allocationSource == "managed") return GC.GetAllocatedBytesForCurrentThread();
#endif
                return _allocationSource == "profiler" ? _allocationRecorder.CurrentValue : -1;
            }
        }

        private static void PrepareAllocationCounter()
        {
#if !ENABLE_IL2CPP
            // Unity IL2CPP's GC.cpp marks this icall NOT_IMPLEMENTED; debug
            // native players assert, so never probe it there, even in a try.
            _allocationSource = "managed";
            if (AllocationCounterWorks()) return;
#endif
            _allocationSource = "profiler";
            try
            {
                _allocationRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC.Alloc", 1,
                    ProfilerRecorderOptions.Default | ProfilerRecorderOptions.SumAllSamplesInFrame
                    | ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
                if (_allocationRecorder.Valid
                    && _allocationRecorder.UnitType == ProfilerMarkerDataUnit.Bytes
                    && AllocationCounterWorks()) return;
            }
            catch { /* Unsupported optional counter: report na, never disrupt gameplay. */ }
            if (_allocationRecorder.Valid) _allocationRecorder.Dispose();
            _allocationRecorder = default; _allocationSource = "na";
        }

        private static bool AllocationCounterWorks()
        {
            long before = AllocatedBytes;
            var first = new byte[1024];
            long middle = AllocatedBytes;
            var second = new byte[2048];
            long after = AllocatedBytes;
            GC.KeepAlive(first); GC.KeepAlive(second);
            return middle - before >= 1024 && after - middle >= 2048;
        }

        /// <summary>Lifecycle/action marks only, never per asset or per frame. Do not include user text or URLs.</summary>
        public static void Event(string action, string detail)
        {
            if (Enabled) Emit("[lvn-perf] event frame=" + Time.frameCount + " action=" + action + " " + detail);
        }

        public static void Flush()
        {
            if (!Enabled) return;
            Emit(_capture.SlowReport(SlowFloorMs)); Emit(_capture.Summary());
        }

        private static void Emit(string line)
        {
            if (line == null) return;
            using var timing = Measure(Part.Diagnostics);
            // run/build — только у редких строк (session, event): у строк S/W они
            // повторяли заголовок пачки и весили больше самих чисел (TR-86).
            if (!line.StartsWith("[lvn-perf] S ", StringComparison.Ordinal) && !line.StartsWith("[lvn-perf] W ", StringComparison.Ordinal))
                line = line.Insert(line.IndexOf(' ', "[lvn-perf] ".Length), _identity);
            _tail[_tailIndex] = line; _tailIndex = (_tailIndex + 1) % _tail.Length;
            _tailCount = Math.Min(_tail.Length, _tailCount + 1);
            // Per-message NoStacktrace: error/exception diagnostics retain their stacks.
            // The existing LvnLogShip picks up this prefix without enabling Trace.
            _logArgs[0] = line;
            Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}", _logArgs);
            _logArgs[0] = null;
        }

        /// <summary>Bounded local tail also survives a successful log-server flush.</summary>
        public static string Tail()
        {
            var b = new StringBuilder();
            for (int i = 0; i < _tailCount; i++)
                b.AppendLine(_tail[(_tailIndex - _tailCount + i + _tail.Length) % _tail.Length]);
            return b.ToString();
        }
    }
}
