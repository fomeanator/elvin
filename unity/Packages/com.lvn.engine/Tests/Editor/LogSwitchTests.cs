using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Lvn;

namespace Lvn.Tests
{
    /// <summary>
    /// Рубильник журнала: <see cref="LvnLog.Trace"/> обязан МОЛЧАТЬ, когда
    /// подробности выключены.
    ///
    /// <para>Проверять это стоит потому, что молчание — единственное, ради чего
    /// дом существует, и сломать его можно, не сломав ничего видимого: строка
    /// продолжит печататься, тесты продолжат зеленеть, а заметит это только тот,
    /// кто откроет консоль живой сборки.</para>
    /// </summary>
    public class LogSwitchTests
    {
        private bool _было;

        [SetUp] public void Setup() => _было = LvnLog.Verbose;
        [TearDown] public void Teardown() => LvnLog.Verbose = _было;

        [Test]
        public void Подробности_выключены_след_молчит()
        {
            LvnLog.Verbose = false;
            LvnLog.Trace("[тест] этой строки быть не должно");
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Подробности_включены_след_слышен()
        {
            LvnLog.Verbose = true;
            LvnLog.Trace("[тест] слышно");
            LogAssert.Expect(LogType.Log, "[тест] слышно");
        }

        [Test]
        public void Веха_звучит_и_при_выключенных_подробностях()
        {
            LvnLog.Verbose = false;
            LvnLog.Info("[тест] веха");
            LogAssert.Expect(LogType.Log, "[тест] веха");
        }

        [Test]
        public void Происшествие_рубильнику_не_подчиняется()
        {
            LvnLog.Verbose = false;
            LvnLog.Warn("[тест] происшествие");
            LogAssert.Expect(LogType.Warning, "[тест] происшествие");
        }
    }
}
