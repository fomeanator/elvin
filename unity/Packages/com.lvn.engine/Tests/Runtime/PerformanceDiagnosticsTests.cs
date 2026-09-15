using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Unity.Profiling.LowLevel.Unsafe;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.TestTools;
using Lvn.UI;
using Debug = UnityEngine.Debug;

namespace Lvn.Tests
{
    public class PerformanceDiagnosticsTests
    {
        [TearDown] public void TearDown() { LvnPerf.Stop(); LvnPerf.Context = null; }

        [UnityTest] public IEnumerator ARealBlockedFrameIsAttributedAndPauseIsExcluded()
        {
            _ = LvnPanel.Shared;
            LvnPerf.Start(); LvnPerf.Context = "perf-test";
            yield return null; yield return null; yield return null;
            int blocked = Time.frameCount;
            using (LvnPerf.Measure(LvnPerf.Part.ActorBuild))
            {
                var sw = Stopwatch.StartNew();
                using (LvnPerf.Measure(LvnPerf.Part.FontBuild))
                    while (sw.ElapsedMilliseconds < 30) Thread.SpinWait(100);
                while (sw.ElapsedMilliseconds < 85) Thread.SpinWait(100);
            }
            yield return null; yield return null;
            LvnPerf.Flush();
            var slow = LvnPerf.Tail().Split('\n').Last(l => l.Contains("[lvn-perf] S "));
            StringAssert.Contains("f=" + blocked + " ", slow);
            StringAssert.Contains("ActorBuild:", slow);
            var costs = System.Text.RegularExpressions.Regex.Match(slow, @"ActorBuild:([0-9.]+)/([0-9.]+)/");
            double self = double.Parse(costs.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            double total = double.Parse(costs.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Greater(total - self, 20, "nested work must be excluded from self time");
            StringAssert.Contains("FontBuild:", slow);
            StringAssert.Contains("ctx=perf-test", slow);
            // run/build живут в строке session, а не в каждой строке кадра (TR-86)
            StringAssert.Contains("run=" + LvnMark.Run + " ", LvnPerf.Tail());
            StringAssert.Contains("build=" + Uri.EscapeDataString(Application.version) + " ", LvnPerf.Tail());
            LvnPerf.Pause(true);
            yield return null;
            LvnPerf.Pause(false);
            // A synthetic background-sized delta in the resume grace period
            // must not appear as a minute-long gameplay freeze.
            LvnPerf.Frame(60, Time.frameCount + 1);
            LvnPerf.Flush();
            StringAssert.DoesNotContain("ms=60000", LvnPerf.Tail());

            var handles = new System.Collections.Generic.List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            var names = handles.Select(ProfilerRecorderHandle.GetDescription)
                .Where(d => d.Name.StartsWith("UIElements") || d.Name.Contains("WaitForTargetFPS"))
                .Select(d => d.Category.Name + ":" + d.Name);
            Debug.Log("[perf-test] available=" + string.Join(",", names));
        }

        [UnityTest] public IEnumerator EnabledScopesAllocateNothingAndIgnoreWorkerThreads()
        {
            LvnPerf.Start(); LvnPerf.Context = "perf-test";
            // Warm JIT and the native marker before measuring managed bytes.
            using (LvnPerf.Measure(LvnPerf.Part.ActorUpdate)) { }
            using var allocations = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC.Alloc", 8192,
                ProfilerRecorderOptions.StartImmediately | ProfilerRecorderOptions.CollectOnlyOnCurrentThread
                | ProfilerRecorderOptions.WrapAroundWhenCapacityReached);
            int control = allocations.Count;
            var allocatedControl = new byte[4096];
            int afterControl = allocations.Count;
            Debug.Log("[perf-test] allocation-counter valid=" + allocations.Valid + " unit=" + allocations.UnitType
                + " before=" + control + " after=" + afterControl + " current=" + allocations.CurrentValue);
            Assert.Greater(afterControl, control, "positive control: a zero-returning API proves nothing");
            GC.KeepAlive(allocatedControl);
            var capture = new LvnPerfCapture(Array.Empty<string>());
            var metrics = Array.Empty<double>();
            capture.EndFrame(0, 16, 16.67, 0, metrics);
            int before = allocations.Count;
            for (int i = 0; i < 1000; i++)
            {
                using (LvnPerf.Measure(LvnPerf.Part.ActorUpdate))
                    using (LvnPerf.Measure(LvnPerf.Part.SpriteFx)) { }
                capture.Span(i + 1, LvnPerf.Part.ActorUpdate, 1, 1, 0);
                capture.EndFrame(i + 1, 16, 16.67, 0, metrics);
            }
            int count = allocations.Count - before;
            Assert.AreEqual(0, count, "hot scopes must not become a source of GC stutter");
            var task = System.Threading.Tasks.Task.Run(() =>
            {
                using (LvnPerf.Measure(LvnPerf.Part.TextureDecode)) { }
            });
            while (!task.IsCompleted) yield return null;
            Assert.IsFalse(task.IsFaulted, task.Exception?.ToString());
            yield return null;
        }
    }
}
