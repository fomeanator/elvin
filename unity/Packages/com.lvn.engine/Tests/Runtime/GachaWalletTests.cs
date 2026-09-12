using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using Lvn;
using Lvn.Services;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Lvn.Tests
{
    /// <summary>
    /// КРУТКА МЕНЯЕТ КОШЕЛЁК СРАЗУ — И ВЫИГРЫШ, И СПИСАНИЕ.
    ///
    /// <para>Сервер начисляет приз и списывает цену прокрута сам; телефон
    /// держит зеркало кошелька и обновляет его, когда его об этом просят.
    /// После крутки просили «при случае» (<see cref="LvnWallet.NudgeAsync"/>) —
    /// а «при случае» значит не чаще раза в 15 секунд, и хаб только что
    /// спрашивал. Кристаллы уходили на сервере, а в шапке лежали прежние до
    /// перезапуска («крутка не отнимает кристаллы» — тестировщик 12.09).
    /// Крутка — действие игрока, и кошелёк после неё спрашивают сразу.</para>
    ///
    /// <para>Живой сервер, как у смены аккаунта: бесплатная крутка дня
    /// начисляет валюту, вторая, платная, списывает цену.</para>
    /// </summary>
    public class GachaWalletTests
    {
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

        private string _keptBase;

        [SetUp]
        public void SetUp()
        {
            _keptBase = LvnBackend.BaseUrl;
            LvnWallet.Forget();
        }

        [TearDown]
        public void TearDown()
        {
            LvnWallet.Forget();
            LvnBackend.BaseUrl = _keptBase;
            LvnKeep.NoteOwner("");
        }

        private static IEnumerator Await(Task t)
        {
            while (!t.IsCompleted) yield return null;
            if (t.IsFaulted) throw t.Exception;
        }

        [UnityTest]
        public IEnumerator ПослеКруткиКошелёкОбновлёнСразу_ДажеЕслиЕгоТолькоЧтоСпрашивали()
        {
            var bin = FindServerBin();
            if (bin == null)
                Assert.Ignore("qa/bin/lvnserver-test не собран (его кладёт qa/run-all.sh) — проверка пропущена");

            var stand = Path.Combine(Path.GetTempPath(), "lvn-gacha-" + Guid.NewGuid().ToString("N"));
            var content = Path.Combine(stand, "content");
            Directory.CreateDirectory(content);
            File.WriteAllText(Path.Combine(content, "manifest.json"), "{\"titles\":[]}");
            // Одна валютная ячейка: жеребьёвка предрешена, выигрыш известен.
            File.WriteAllText(Path.Combine(content, "gacha.json"),
                "{\"sectors\":[{\"id\":\"seven\",\"kind\":\"currency\",\"currency\":\"crystals\",\"amount\":7,\"weight\":1}],"
              + "\"prizes\":[],\"spin_currency\":\"crystals\",\"spin_price\":3}");

            var port = FreePort();
            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = bin,
                Arguments = $"-addr 127.0.0.1:{port} -content \"{content}\" -auth-dev",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            try
            {
                var deadline = Time.realtimeSinceStartup + 10f;
                var healthy = false;
                while (!healthy && Time.realtimeSinceStartup < deadline)
                {
                    using (var probe = UnityEngine.Networking.UnityWebRequest.Get($"http://127.0.0.1:{port}/healthz"))
                    {
                        probe.timeout = 2;
                        yield return probe.SendWebRequest();
                        healthy = probe.result == UnityEngine.Networking.UnityWebRequest.Result.Success;
                    }
                }
                Assert.IsTrue(healthy, "локальный сервер не ответил на /healthz за 10 с");
                LvnBackend.BaseUrl = $"http://127.0.0.1:{port}";

                yield return Await(LvnBackend.EnsureRegisteredAsync());

                // Хаб только что спрашивал кошелёк: «при случае» теперь молчит 15 с.
                yield return Await(LvnWallet.NudgeAsync());
                Assert.AreEqual(0L, LvnWallet.Balance("crystals"), "стенд: у нового игрока кристаллов нет");

                // Бесплатная крутка дня — сервер начислил семь.
                var first = LvnGacha.SpinAsync();
                yield return Await(first);
                Assert.IsTrue(string.IsNullOrEmpty(first.Result.Error), "стенд: первая крутка не прошла: " + first.Result.Error);
                Assert.AreEqual(7L, first.Result.Amount, "стенд: выигрыш не тот");
                Assert.AreEqual(7L, LvnWallet.Balance("crystals"),
                    "выигрыш крутки не дошёл до кошелька сразу — в шапке лежат прежние кристаллы");

                // Вторая крутка платная: минус три, плюс семь.
                var second = LvnGacha.SpinAsync();
                yield return Await(second);
                Assert.IsTrue(string.IsNullOrEmpty(second.Result.Error), "стенд: вторая крутка не прошла: " + second.Result.Error);
                Assert.AreEqual(11L, LvnWallet.Balance("crystals"),
                    "цена крутки не списалась в кошельке сразу — она уйдёт только после перезапуска");
            }
            finally
            {
                try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
                try { Directory.Delete(stand, true); } catch { }
            }
        }
    }
}
