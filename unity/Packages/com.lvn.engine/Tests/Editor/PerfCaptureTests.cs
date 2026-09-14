using System;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class PerfCaptureTests
    {
        private static readonly double[] NoMetrics = Array.Empty<double>();

        [Test] public void AnEarlierUpdateInTheNewFrameDoesNotOverwriteTheHitch()
        {
            var c = new LvnPerfCapture(Array.Empty<string>());
            c.Context(100, "settings");
            c.Span(100, LvnPerf.Part.UiRedress, 70, 60, 2048);
            c.Span(101, LvnPerf.Part.ActorBuild, 1, 1, 0);
            c.EndFrame(100, 80, 16.67, 0, NoMetrics);
            var report = c.SlowReport();
            StringAssert.Contains("frame=100", report);
            StringAssert.Contains("context=settings", report);
            StringAssert.Contains("UiRedress:60.00/total=70.00", report);
            StringAssert.DoesNotContain("ActorBuild", report);
            c.EndFrame(101, 30, 16.67, 0, NoMetrics);
            StringAssert.Contains("ActorBuild:1.00", c.SlowReport());
        }

        [Test] public void ReportsWorstFrameWithoutDroppingSustainedJankFromSummary()
        {
            var c = new LvnPerfCapture(Array.Empty<string>());
            for (int i = 0; i < 120; i++) c.EndFrame(i, i == 40 ? 200 : 40, 16.67, 0, NoMetrics);
            StringAssert.Contains("frame=40", c.SlowReport());
            Assert.IsNull(c.SlowReport(), "no duplicate log until another slow frame");
            var report = c.Summary();
            StringAssert.Contains("frames=120", report);
            StringAssert.Contains("over_budget=120", report);
            StringAssert.Contains("max_ms=200.00", report);
            Assert.IsNull(c.Summary());
        }

        [Test] public void PercentilesAndCountsIncludeFastAndSlowFrames()
        {
            var c = new LvnPerfCapture(Array.Empty<string>());
            for (int i = 0; i < 20; i++) c.EndFrame(i, i == 19 ? 200 : 16, 16.67, i == 19 ? 1 : 0, NoMetrics);
            var report = c.Summary();
            StringAssert.Contains("p50_ms=16.00", report);
            StringAssert.Contains("p95_ms=16.00", report);
            StringAssert.Contains("p99_ms=200.00", report);
            StringAssert.Contains("over_budget=1 over50=1 over100=1 gc_collections=1", report);
        }

        [Test] public void UnknownCounterIsNotMisreportedAsZeroAndMetricsMatchWorstFrame()
        {
            var c = new LvnPerfCapture(new[] { "main_ms", "render_ms" });
            c.EndFrame(1, 60, 16.67, 0, new[] { 50.0, -1 });
            c.EndFrame(2, 16, 16.67, 0, new[] { 10.0, -1 });
            StringAssert.Contains("main_ms=50.00 render_ms=na", c.SlowReport());
            var report = c.Summary();
            StringAssert.Contains("main_ms_avg=30.00 main_ms_max=50.00", report);
            StringAssert.Contains("render_ms_avg=na render_ms_max=na", report);
        }

        [Test] public void ResumeResetDiscardsOldScopesAndPausedWindow()
        {
            var c = new LvnPerfCapture(Array.Empty<string>());
            c.Span(1, LvnPerf.Part.TextureDecode, 5000, 5000, 0);
            c.EndFrame(1, 5000, 16.67, 0, NoMetrics);
            c.Reset();
            Assert.IsNull(c.SlowReport()); Assert.IsNull(c.Summary());
            c.EndFrame(1, 40, 16.67, 0, NoMetrics);
            StringAssert.DoesNotContain("TextureDecode", c.SlowReport());
        }

        [Test] public void AccumulationHasBoundedStorage()
        {
            var c = new LvnPerfCapture(Array.Empty<string>());
            c.Span(0, LvnPerf.Part.ActorUpdate, 1, 1, 0);
            c.EndFrame(0, 16, 16.67, 0, NoMetrics);
            for (int i = 1; i <= 10000; i++)
            {
                c.Span(i, LvnPerf.Part.ActorUpdate, 1, 1, 0);
                c.EndFrame(i, 16, 16.67, 0, NoMetrics);
            }
            var report = c.Summary();
            StringAssert.Contains("frames=10001", report);
            StringAssert.Contains("percentile_samples=8192", report);
        }
    }
}
