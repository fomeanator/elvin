using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using Lvn;
using Lvn.Services;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Lvn.Tests
{
    /// <summary>
    /// «УДАЛИТЬ АККАУНТ» ЗАБЫВАЕТ ИГРОКА И НА УСТРОЙСТВЕ.
    ///
    /// <para>Кнопка удаления — требование обоих магазинов. Серверную половину
    /// мы уже проверили и починили (учётка, кошелёк, журнал, рекорды). Но
    /// половина данных игрока живёт НА ТЕЛЕФОНЕ: сохранения, прогресс глав,
    /// галерея, «прочитано». Если после удаления они остаются, то «забудьте
    /// меня» выполнено наполовину — и следующий человек с этим телефоном
    /// открывает чужую историю.</para>
    /// </summary>
    public class AccountDeleteLocalTests
    {
        private const string Новелла = "стенд-удаление-аккаунта";

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
            LvnSaveStore.DeleteAll(Новелла);
            LvnReadStore.Clear(Новелла);
        }

        [TearDown]
        public void TearDown()
        {
            LvnSaveStore.DeleteAll(Новелла);
            LvnReadStore.Clear(Новелла);
            LvnBackend.BaseUrl = _keptBase;
            LvnKeep.NoteOwner("");
        }

        private static IEnumerator Await(Task t)
        {
            while (!t.IsCompleted) yield return null;
            if (t.IsFaulted) throw t.Exception;
        }

        [UnityTest]
        public IEnumerator УдалениеАккаунтаСтираетДанныеНаУстройстве()
        {
            var bin = FindServerBin();
            if (bin == null)
                Assert.Ignore("qa/bin/lvnserver-test не собран (его кладёт qa/run-all.sh) — проверка пропущена");

            var stand = Path.Combine(Path.GetTempPath(), "lvn-delete-" + Guid.NewGuid().ToString("N"));
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

                var reg = LvnBackend.EnsureRegisteredAsync();
                yield return Await(reg);
                Assert.IsTrue(LvnBackend.SignedIn, "стенд: игрок не завёлся");

                LvnSaveStore.Put(Новелла, "1", new LvnSaveSlot
                {
                    ChapterId = "глава-1", Preview = "личная реплика игрока", SavedAtUnixMs = 1,
                });
                LvnReadStore.MarkRead(Новелла, "Герой", "личная реплика игрока");
                LvnReadStore.FlushNow();
                Assert.IsNotNull(LvnSaveStore.Get(Новелла, "1"), "стенд: сейв не записался");

                var del = LvnBackend.DeleteAccountAsync();
                yield return Await(del);
                Assert.IsTrue(del.Result, "сервер не принял удаление аккаунта");

                var сейв = LvnSaveStore.Get(Новелла, "1");
                bool прочитано = LvnReadStore.IsRead(Новелла, "Герой", "личная реплика игрока");
                TestContext.WriteLine($"после «удалить аккаунт»: сейв {(сейв == null ? "стёрт" : "ОСТАЛСЯ")}, "
                                    + $"«прочитано» {(прочитано ? "ОСТАЛОСЬ" : "стёрто")}, "
                                    + $"вход {(LvnBackend.SignedIn ? "остался" : "сброшен")}");

                Assert.IsFalse(LvnBackend.SignedIn, "после удаления игрок всё ещё считается вошедшим");
                Assert.IsNull(сейв,
                    "сохранение пережило удаление аккаунта — «забудьте меня» выполнено наполовину");
                Assert.IsFalse(прочитано,
                    "отметки прочитанного пережили удаление аккаунта");
            }
            finally
            {
                try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
                try { Directory.Delete(stand, true); } catch { }
            }
        }
    }
}
