using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Lvn.Tests
{
    /// <summary>
    /// Ladder C1+C2 seed: the PlayMode integration smoke. A real NovelApp
    /// boots inside the player loop against a REAL local Go server (spawned by
    /// the test on a free port) and must reach "shell built" — the moment an
    /// interactive shell exists. This is the layer EditMode can't see: the
    /// async boot pipeline, the veil, UIToolkit panel construction, the HTTP
    /// stack — wired together exactly as on a device.
    ///
    /// The server binary is built by qa/run-all.sh (qa/bin/lvnserver-test) or
    /// pointed at via LVN_SERVER_BIN; without it the test skips so clean CI
    /// clones stay green.
    /// </summary>
    public class BootSmokeTests
    {
        private static string RepoRoot => Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", "..", ".."));

        private static string FindServerBin()
        {
            var env = System.Environment.GetEnvironmentVariable("LVN_SERVER_BIN");
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

        [UnityTest]
        public IEnumerator Boot_ReachesShellBuilt_AgainstALiveLocalServer()
        {
            var bin = FindServerBin();
            if (bin == null)
                Assert.Ignore("qa/bin/lvnserver-test не собран (его кладёт qa/run-all.sh) — PlayMode-смоук пропущен");

            var content = Path.Combine(RepoRoot, "server", "content");
            if (!File.Exists(Path.Combine(content, "manifest.json")))
                Assert.Ignore("server/content/manifest.json отсутствует — PlayMode-смоук пропущен");

            var port = FreePort();
            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = bin,
                Arguments = $"-addr 127.0.0.1:{port} -content \"{content}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            // Unread redirected pipes eventually block the server on a full
            // buffer (it logs every request) — drain them in the background.
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // The server needs ~100ms to listen; NovelApp's connectivity probe
            // fires instantly and a refused first probe sends boot into the
            // (legal, but slow) 5s retry loop — wait for healthz first.
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

            string shellBuilt = null;
            void OnLog(string cond, string stack, LogType type)
            {
                if (cond.Contains("shell built")) shellBuilt = cond;
            }
            Application.logMessageReceived += OnLog;
            var go = new GameObject("NovelApp-under-test");
            try
            {
                Assert.IsTrue(healthy, "локальный lvnserver не ответил на /healthz за 10с");
                var app = go.AddComponent<Lvn.UI.Screens.NovelApp>();
                app.ServerUrl = $"http://127.0.0.1:{port}";
                app.SyncInterval = 0f;

                var deadline = Time.realtimeSinceStartup + 45f;
                while (shellBuilt == null && Time.realtimeSinceStartup < deadline)
                    yield return null;

                Assert.IsNotNull(shellBuilt,
                    "бут не дошёл до 'shell built' за 45с против живого локального сервера");
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
                Object.Destroy(go);
                try { if (!proc.HasExited) proc.Kill(); } catch { /* уже умер */ }
            }
        }
    }
}
