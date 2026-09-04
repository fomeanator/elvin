using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Lvn.Tests
{
    /// <summary>
    /// ПУСТОЙ ОТВЕТ СЕРВЕРА НЕ СТИРАЕТ БИБЛИОТЕКУ ИГРОКА.
    ///
    /// <para>Манифест — единственная точка правды о том, какие новеллы есть.
    /// Сервер, у которого файл манифеста пропал или не читается, отвечает не
    /// ошибкой, а <c>200 {"titles":[]}</c> — замерено живым сервером
    /// (qa/empty-manifest-check.sh): «свежая установка ещё ничего не
    /// опубликовала» и «выкладка сломалась» выглядят с провода одинаково.</para>
    ///
    /// <para>Для клиента разница огромна. Принять пустой каталог значит не
    /// только показать игроку пустую витрину, но и ЗАТЕРЕТЬ офлайновую копию —
    /// после чего пустой станет и игра без сети. Условие проверки поэтому:
    /// игра уже знает каталог, сервер начал отдавать пустоту — библиотека
    /// обязана остаться.</para>
    /// </summary>
    public class CatalogGuardTests
    {
        private const string CacheKey = "lvn_manifest_cache";
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

        private string _keptCache;
        private bool _wasOffline;

        [SetUp]
        public void SetUp()
        {
            _keptCache = PlayerPrefs.GetString(CacheKey, null);
            _wasOffline = LvnNetworkStatus.ForceOffline;
            LvnNetworkStatus.ForceOffline = false;
            LvnNetworkStatus.MarkOnline("проверка каталога");
        }

        [TearDown]
        public void TearDown()
        {
            LvnNetworkStatus.ForceOffline = _wasOffline;
            if (string.IsNullOrEmpty(_keptCache)) PlayerPrefs.DeleteKey(CacheKey);
            else PlayerPrefs.SetString(CacheKey, _keptCache);
            PlayerPrefs.Save();
        }

        [UnityTest]
        public IEnumerator ПустойКаталогНеСтираетБиблиотекуИгрока()
        {
            var bin = FindServerBin();
            if (bin == null)
                Assert.Ignore("qa/bin/lvnserver-test не собран (его кладёт qa/run-all.sh) — проверка пропущена");

            var stand = Path.Combine(Path.GetTempPath(), "lvn-catalog-" + Guid.NewGuid().ToString("N"));
            var content = Path.Combine(stand, "content");
            Directory.CreateDirectory(Path.Combine(content, "scripts"));
            var manifestPath = Path.Combine(content, "manifest.json");
            File.WriteAllText(manifestPath,
                "{\"titles\":[{\"id\":\"probe\",\"name\":\"Проба\",\"chapters\":[]}]}");

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

            var go = new GameObject("NovelApp-catalog-test");
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

                PlayerPrefs.DeleteKey(CacheKey);   // чистый старт: каталога ещё не знаем
                PlayerPrefs.Save();

                var app = go.AddComponent<Lvn.UI.Screens.NovelApp>();
                app.ServerUrl = $"http://127.0.0.1:{port}";
                app.SyncInterval = 0.5f;           // живое обновление — часто, тест ждёт секунды

                // Каталог доехал и лёг в офлайновую копию.
                var deadline = Time.realtimeSinceStartup + 45f;
                while (Time.realtimeSinceStartup < deadline
                       && !PlayerPrefs.GetString(CacheKey, "").Contains("probe"))
                    yield return null;
                StringAssert.Contains("probe", PlayerPrefs.GetString(CacheKey, ""),
                    "каталог не лёг в офлайновую копию за 45с — стенду не на чем стоять");

                // ВЫКЛАДКА СЛОМАЛАСЬ: манифест на месте, но ПУСТОЙ.
                //
                // Сначала стенд удалял файл — и тест был зелёным впустую:
                // пропажа манифеста попадает в дельте в «удалённые», а
                // «каталог сменился» смотрит только на изменившиеся, и клиент
                // за манифестом вовсе не шёл (в журнале прогона не было ни
                // одной приёмки). Пустой файл — тот же случай кривой выкладки,
                // но он МЕНЯЕТ версию, то есть доходит до клиента по-настоящему.
                File.WriteAllText(manifestPath, "{\"titles\":[]}");

                // Ждём столько, чтобы живое обновление успело сходить не раз.
                var watch = Time.realtimeSinceStartup + 12f;
                while (Time.realtimeSinceStartup < watch) yield return null;

                StringAssert.Contains("probe", PlayerPrefs.GetString(CacheKey, ""),
                    "пустой ответ сервера затёр офлайновую копию каталога: игрок остался без библиотеки "
                    + "и в сети, и без неё — до следующей удачной выкладки");
            }
            finally
            {
                UnityEngine.Object.Destroy(go);
                try { if (!proc.HasExited) proc.Kill(); } catch { /* уже умер */ }
                try { Directory.Delete(stand, true); } catch { /* временный каталог */ }
                // ЗА СОБОЙ ГАСИМ И СЕТЕВОЙ СТАТУС. Задачи снесённого NovelApp
                // доживают свой круг уже за пределами теста, упираются в
                // погашенный сервер и помечают сеть офлайном — глобально. У
                // соседа это выглядело как «offline (global status)» на ровном
                // месте, и исход решал порядок тестов.
                LvnNetworkStatus.MarkOnline("конец проверки каталога");
            }
        }
    }
}
