using System.Diagnostics;
using Lvn;
using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ОТМЕТКА «ПРОЧИТАНО» НЕ ДОРОЖАЕТ ПО МЕРЕ ЧТЕНИЯ.
    ///
    /// <para>Каждая показанная реплика метится прочитанной — на этом стоит
    /// пропуск только знакомого текста. Набор отметок один на НОВЕЛЛУ, а не на
    /// главу: у большой новеллы это десятки тысяч записей, и растёт он всю
    /// игру.</para>
    ///
    /// <para>Замер 05.09 показал, что строка со всеми отметками собиралась
    /// заново на каждой реплике: 1000 реплик — 1206 мс, 3000 — 4070 мс,
    /// 9000 — 17756 мс, то есть от 1,2 до 2,0 мс на КАЖДЫЙ тап, и цена росла
    /// по ходу чтения. Теперь хэш дописывается в хвост, а в записную книжку
    /// строка уходит вместе с фиксацией — раз в несколько реплик.</para>
    /// </summary>
    public class ReadMarkCostTests
    {
        private static long ЗамерМс(string title, int реплик)
        {
            LvnReadStore.Clear(title);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < реплик; i++)
                LvnReadStore.MarkRead(title, "Герой", "Реплика номер " + i + " какого-то разумного размера.");
            sw.Stop();
            LvnReadStore.Clear(title);
            return sw.ElapsedMilliseconds;
        }

        [Test]
        public void ЦенаОтметкиНеРастётСПрочитанным()
        {
            long малая = ЗамерМс("замер-малая", 1000);
            long большая = ЗамерМс("замер-большая", 9000);

            double наРепликуМалая = (double)малая / 1000;
            double наРепликуБольшая = (double)большая / 9000;
            TestContext.WriteLine($"на реплику: при 1000 прочитанных {наРепликуМалая * 1000:F0} мкс, "
                                + $"при 9000 — {наРепликуБольшая * 1000:F0} мкс "
                                + $"(отношение {наРепликуБольшая / System.Math.Max(0.0001, наРепликуМалая):F2})");

            Assert.Less(наРепликуБольшая / System.Math.Max(0.0001, наРепликуМалая), 1.6,
                "чем больше прочитано, тем дороже каждая следующая реплика — учёт собирается заново");
        }

        /// Остаток цены — сама фиксация настроек. Число печатается, чтобы
        /// вердикт можно было перечитать через месяц и не гадать.
        [Test]
        public void ЦенаФиксацииНастроекНазвана()
        {
            UnityEngine.PlayerPrefs.SetString("замер.фиксации", new string('x', 32));
            UnityEngine.PlayerPrefs.Save();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 50; i++)
            {
                UnityEngine.PlayerPrefs.SetString("замер.фиксации", "значение-" + i);
                UnityEngine.PlayerPrefs.Save();
            }
            sw.Stop();
            TestContext.WriteLine($"одна фиксация настроек: {(double)sw.ElapsedMilliseconds / 50:F2} мс — "
                                + "при фиксации раз в 10 реплик это и есть весь остаток цены отметки");
            UnityEngine.PlayerPrefs.DeleteKey("замер.фиксации");
            UnityEngine.PlayerPrefs.Save();
            Assert.Pass();
        }

        /// Экономия на горячем пути не смеет стоить потерянных отметок:
        /// накопленное уезжает в книжку при уходе в фон и при выходе.
        [Test]
        public void НакопленноеУезжаетВКнижкуПриУходеВФон()
        {
            const string title = "замер-фон";
            LvnReadStore.Clear(title);
            for (int i = 0; i < 3; i++)   // меньше, чем между фиксациями
                LvnReadStore.MarkRead(title, "Герой", "Строка " + i);

            LvnReadStore.FlushNow();

            string сохранено = LvnKeep.Get(LvnKeep.Scoped("lvn.read.", title), "");
            int отметок = сохранено.Length == 0 ? 0 : сохранено.Split(',').Length;
            Assert.AreEqual(3, отметок,
                $"в книжке {отметок} отметок вместо трёх — прочитанное теряется между фиксациями");

            LvnReadStore.Clear(title);
        }

        /// И само знание не должно портиться: что записано, то и читается.
        [Test]
        public void ЗаписанноеЧитаетсяОбратно()
        {
            const string title = "замер-обратно";
            LvnReadStore.Clear(title);
            for (int i = 0; i < 25; i++)
                LvnReadStore.MarkRead(title, "Герой", "Строка " + i);
            LvnReadStore.FlushNow();

            Assert.AreEqual(25, LvnReadStore.ReadCount(title), "часть отметок не дошла до набора");
            Assert.IsTrue(LvnReadStore.IsRead(title, "Герой", "Строка 7"), "прочитанная строка считается новой");
            Assert.IsFalse(LvnReadStore.IsRead(title, "Герой", "Строка 99"), "непрочитанная строка считается знакомой");
            Assert.IsFalse(LvnReadStore.MarkRead(title, "Герой", "Строка 7"), "повторная отметка объявлена новой");

            LvnReadStore.Clear(title);
        }
    }
}
