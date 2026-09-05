using System.Collections;
using System.IO;
using Lvn;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Lvn.Tests
{
    /// <summary>
    /// ПОДМЕНЁННЫЙ ОТВЕТ НЕ ОТНИМАЕТ БИБЛИОТЕКУ.
    ///
    /// <para>Мобильная сеть отвечает не только тем, что мы просили. Оператор
    /// подставляет страницу «пополните счёт», вайфай в кафе — форму входа,
    /// прокси — свой JSON с ошибкой, а оборванное соединение отдаёт половину
    /// файла. Всё это приходит с кодом 200, и для клиента выглядит как каталог.
    /// Игрок при этом уже играл: у него есть офлайновая копия, и потерять её
    /// он не должен ни в одном из этих случаев.</para>
    ///
    /// <para>Соседняя проверка (CatalogGuardTests) разбирает ЧЕСТНЫЙ пустой
    /// ответ сервера — «игр нет». Здесь ответ нечестный: он вообще не наш.</para>
    /// </summary>
    public class HijackedManifestTests
    {
        private const string CacheKey = "lvn_manifest_cache";

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

        private string _keptCache;

        [SetUp]
        public void SetUp() => _keptCache = LvnKeep.Get(CacheKey, "");

        [TearDown]
        public void TearDown()
        {
            if (string.IsNullOrEmpty(_keptCache)) LvnKeep.Drop(CacheKey);
            else LvnKeep.Put(CacheKey, _keptCache);
        }

        [UnityTest]
        public IEnumerator ЧужойJsonНеОтнимаетБиблиотеку()
        {
            yield return Подмена("{\"error\":\"unauthorized\"}");
        }

        /// Страница входа в вайфай: HTML с кодом 200 вместо каталога.
        [UnityTest]
        public IEnumerator СтраницаВходаВСетьНеОтнимаетБиблиотеку()
        {
            yield return Подмена("<html><body>Sign in to continue</body></html>");
        }

        /// Оборванное соединение: половина файла.
        [UnityTest]
        public IEnumerator ОбрезанныйОтветНеОтнимаетБиблиотеку()
        {
            yield return Подмена("{\"titles\":[{\"id\":\"сво");
        }

        private IEnumerator Подмена(string ответСервера)
        {
            var bin = FindServerBin();
            if (bin == null)
                Assert.Ignore("qa/bin/lvnserver-test не собран (его кладёт qa/run-all.sh) — проверка пропущена");

            // Игрок уже играл: в кэше лежит его библиотека.
            LvnKeep.Put(CacheKey,
                "{\"titles\":[{\"id\":\"своя\",\"name\":\"Своя новелла\",\"seasons\":[{\"chapters\":[]}]}]}");

            // Сервер отдаёт ВАЛИДНЫЙ JSON, но не каталог — так выглядит ответ
            // прокси оператора, страницы входа в вайфай или сервера, у которого
            // сменился API.
            var stand = Path.Combine(Path.GetTempPath(), "lvn-hijack-" + System.Guid.NewGuid().ToString("N"));
            var content = Path.Combine(stand, "content");
            Directory.CreateDirectory(content);
            File.WriteAllText(Path.Combine(content, "manifest.json"), ответСервера);

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

            var go = new GameObject("NovelApp-hijacked");
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
                Assert.IsTrue(healthy, "локальный сервер не ответил");

                var app = go.AddComponent<Lvn.UI.Screens.NovelApp>();
                app.ServerUrl = $"http://127.0.0.1:{port}";
                app.SyncInterval = 0f;

                // Ждём ровно столько, сколько нужно приложению, чтобы принять
                // решение о каталоге: три такие проверки подряд с двенадцатью
                // секундами каждая растянули общий прогон настолько, что
                // соседний PlayMode-тест не дождался своей кнопки и покраснел
                // на перегруженной машине.
                yield return new WaitForSecondsRealtime(7f);

                // Что показано игроку: живой каталог приложения (то, из чего
                // строится витрина).
                var поле = typeof(Lvn.UI.Screens.NovelApp).GetField("_manifest",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var живой = поле?.GetValue(app) as Lvn.Content.LvnManifest;
                int новелл = живой?.titles?.Count ?? -1;
                TestContext.WriteLine($"витрина показывает новелл: {новелл} (в офлайновой копии 1)");
                Assert.AreEqual(1, новелл,
                    "подменённый ответ стал витриной — игрок остался без своей библиотеки");

                string кэш = LvnKeep.Get(CacheKey, "");
                bool кэшЦел = кэш.Contains("Своя новелла");
                TestContext.WriteLine($"после подменённого ответа: офлайновая копия "
                                    + $"{(кэшЦел ? "цела" : "ЗАТЁРТА")}; в кэше {кэш.Length} байт");
                Assert.IsTrue(кэшЦел,
                    "подменённый ответ затёр офлайновую копию — без сети игра станет пустой");
            }
            finally
            {
                Object.Destroy(go);
                try { if (!proc.HasExited) proc.Kill(); } catch { }
                try { Directory.Delete(stand, true); } catch { }
            }
        }
    }
}
