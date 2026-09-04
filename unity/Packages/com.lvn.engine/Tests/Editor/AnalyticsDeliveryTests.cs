using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lvn.Services;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Lvn.Tests
{
    /// <summary>
    /// СОБЫТИЕ, СЛУЧИВШЕЕСЯ БЕЗ СЕТИ, ДОЕЗЖАЕТ ДО СЕРВЕРА.
    ///
    /// <para>На событиях стоит весь продуктовый разбор: воронка, где бросают
    /// главу, за что платят. Игрок при этом читает в метро — то есть значимая
    /// часть событий рождается ОФЛАЙН. Потерянное событие не видно никому:
    /// отчёт просто показывает меньшее число, и разговор о продукте идёт по
    /// заниженным данным.</para>
    ///
    /// <para>Соседние проверки (<c>OutboxTests</c>) разбирают саму очередь на
    /// подставном отправителе — порядок, потолок, переживание перезапуска. Ни
    /// одна не отвечает на вопрос, ради которого очередь заведена: доедет ли
    /// событие ДО НАСТОЯЩЕГО СЕРВЕРА, когда сеть вернётся. Здесь сервер
    /// настоящий, и проверяется его файл событий.</para>
    /// </summary>
    public class AnalyticsDeliveryTests
    {
        private const string QueueKey = "lvn.svc.analytics.queue";

        private static string RepoRoot => Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", "..", ".."));

        private static string FindServerBin()
        {
            var env = Environment.GetEnvironmentVariable("LVN_SERVER_BIN");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
            var built = Path.Combine(RepoRoot, "qa", "bin", "lvnserver-test");
            return File.Exists(built) ? built : null;
        }

        private static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private string _keptQueue, _keptBase;
        private bool _wasOffline;

        [SetUp]
        public void SetUp()
        {
            _keptQueue = PlayerPrefs.GetString(QueueKey, null);
            _keptBase = LvnBackend.BaseUrl;
            _wasOffline = LvnNetworkStatus.ForceOffline;
            PlayerPrefs.DeleteKey(QueueKey);
            LvnAnalytics.Forget();
        }

        [TearDown]
        public void TearDown()
        {
            LvnAnalytics.Forget();
            LvnBackend.BaseUrl = _keptBase;
            LvnNetworkStatus.ForceOffline = _wasOffline;
            if (string.IsNullOrEmpty(_keptQueue)) PlayerPrefs.DeleteKey(QueueKey);
            else PlayerPrefs.SetString(QueueKey, _keptQueue);
            PlayerPrefs.Save();
        }

        private static IEnumerator Await(Task t)
        {
            while (!t.IsCompleted) yield return null;
            if (t.IsFaulted) throw t.Exception;
        }

        /// <summary>Всё, что сервер записал за прогон, одной строкой.</summary>
        private static string ServerLog(string content)
        {
            var dir = Path.Combine(content, "services", "analytics");
            if (!Directory.Exists(dir)) return "";
            return string.Join("\n", Directory.GetFiles(dir, "*.jsonl").Select(File.ReadAllText));
        }

        [UnityTest]
        public IEnumerator СобытиеБезСетиДоезжаетКогдаСетьВернулась()
        {
            var bin = FindServerBin();
            if (bin == null)
                Assert.Ignore("qa/bin/lvnserver-test не собран (его кладёт qa/run-all.sh) — проверка пропущена");

            var stand = Path.Combine(Path.GetTempPath(), "lvn-analytics-" + Guid.NewGuid().ToString("N"));
            var content = Path.Combine(stand, "content");
            Directory.CreateDirectory(content);
            File.WriteAllText(Path.Combine(content, "manifest.json"), "{\"titles\":[]}");

            var port = FreePort();
            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = bin,
                Arguments = $"-addr 127.0.0.1:{port} -content \"{content}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            try
            {
                var healthDeadline = Time.realtimeSinceStartup + 10f;
                var healthy = false;
                while (!healthy && Time.realtimeSinceStartup < healthDeadline)
                {
                    using (var probe = UnityEngine.Networking.UnityWebRequest.Get($"http://127.0.0.1:{port}/healthz"))
                    {
                        probe.timeout = 2;
                        yield return probe.SendWebRequest();
                        healthy = probe.result == UnityEngine.Networking.UnityWebRequest.Result.Success;
                    }
                }
                Assert.IsTrue(healthy, "локальный сервер не ответил на /healthz за 10с");

                LvnBackend.BaseUrl = $"http://127.0.0.1:{port}";
                var mark = "stand_" + Guid.NewGuid().ToString("N").Substring(0, 8);

                // СЕРВЕР ГАСИТСЯ ПО-НАСТОЯЩЕМУ, а не помечается флагом.
                //
                // Первая редакция стенда просто ставила ForceOffline — и
                // событие всё равно уехало: флаг сторожит загрузку контента, а
                // очередь событий ходит своим путём. Проверялась бы пометка, а
                // не поведение; настоящий офлайн — это когда отвечать некому.
                try { if (!proc.HasExited) proc.Kill(); } catch { /* уже умер */ }
                yield return null;

                LvnAnalytics.Track(mark, ("where", "offline"));
                yield return Await(LvnAnalytics.FlushAsync());   // отправить некуда

                Assert.IsFalse(ServerLog(content).Contains(mark),
                    "событие записалось на погашенном сервере — стенд не воспроизводит офлайн");
                StringAssert.Contains(mark, PlayerPrefs.GetString(QueueKey, ""),
                    "событие, случившееся без связи, не легло в очередь — оно уже потеряно");

                // Связь вернулась: тот же порт, тот же каталог.
                proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = bin,
                    Arguments = $"-addr 127.0.0.1:{port} -content \"{content}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                var backDeadline = Time.realtimeSinceStartup + 10f;
                var back = false;
                while (!back && Time.realtimeSinceStartup < backDeadline)
                {
                    using (var probe = UnityEngine.Networking.UnityWebRequest.Get($"http://127.0.0.1:{port}/healthz"))
                    {
                        probe.timeout = 2;
                        yield return probe.SendWebRequest();
                        back = probe.result == UnityEngine.Networking.UnityWebRequest.Result.Success;
                    }
                }
                Assert.IsTrue(back, "сервер не поднялся обратно за 10с");
                LvnNetworkStatus.MarkOnline("сервер вернулся");
                yield return Await(LvnAnalytics.FlushAsync());

                StringAssert.Contains(mark, ServerLog(content),
                    "событие, случившееся без сети, до сервера не доехало — отчёты будут считать по заниженным данным");
                Assert.IsFalse(PlayerPrefs.GetString(QueueKey, "").Contains(mark),
                    "доставленное событие осталось в очереди — при следующей отправке оно задвоится");
            }
            finally
            {
                try { if (!proc.HasExited) proc.Kill(); } catch { /* уже умер */ }
                try { Directory.Delete(stand, true); } catch { /* временный каталог */ }
            }
        }
    }
}
