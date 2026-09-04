using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ЧИСЛА ИНДИКАТОРА ЗАГРУЗКИ СХОДЯТСЯ МЕЖДУ СОБОЙ.
    ///
    /// <para>Живые снимки, с которых началось: кольцо почти полное на первых
    /// процентах, «Скачано 296 МБ из 298 МБ» рядом с «Осталось ≈114 МБ» и
    /// скорость 21,9 МБ/с на вставшей загрузке. Все три — про одно: карточка
    /// собирала числа из разных мест.</para>
    ///
    /// <para>Полный замер (обе формулы на наборе из 161 файла) живёт в
    /// qa/download-progress-check.sh и Unity не требует. Здесь — те же правила
    /// внутри редактора, чтобы прогон тестов ловил их наравне со всем
    /// остальным.</para>
    /// </summary>
    public class DownloadTallyTests
    {
        [Test]
        public void ДоляСчитаетсяПланом()
        {
            var t = new DownloadTally(25_000_000, 100_000_000, 40, 161, 1e6f, DownloadTally.Phase.Running);
            Assert.AreEqual(0.25f, t.Fraction, 0.001f, "доля разошлась с планом");
            Assert.AreEqual(75_000_000, t.LeftBytes, "«осталось» считается не планом");
        }

        [Test]
        public void БезПланаКольцоКрутится()
        {
            var t = new DownloadTally(5_000_000, 0, 3, 10, 1e6f, DownloadTally.Phase.Running);
            Assert.Less(t.Fraction, 0f, "без плана доля притворилась известной — кольцо встанет полным");
            Assert.AreEqual(0, t.LeftBytes, "без плана «осталось» выдумано");
        }

        [Test]
        public void ПереполнениеПланаНеБольшеЕдиницы()
        {
            var t = new DownloadTally(12_000_000, 10_000_000, 10, 10, 0f, DownloadTally.Phase.Running);
            Assert.AreEqual(1f, t.Fraction, 0.0001f);
            Assert.AreEqual(0, t.LeftBytes);
        }

        [Test]
        public void ВставшаяЗагрузкаНазываетсяВставшей()
        {
            Assert.AreEqual(DownloadTally.Phase.Running, DownloadTally.PhaseOf(true, false, 0, 0.5f));
            Assert.AreEqual(DownloadTally.Phase.Stalled, DownloadTally.PhaseOf(true, false, 0, 6f));
            Assert.AreEqual(DownloadTally.Phase.Offline, DownloadTally.PhaseOf(true, true, 0, 0.1f));
            Assert.AreEqual(DownloadTally.Phase.Syncing, DownloadTally.PhaseOf(false, false, 3, 0f));
            Assert.AreEqual(DownloadTally.Phase.Idle, DownloadTally.PhaseOf(false, false, 0, 0f));
        }

        [Test]
        public void НаВставшейЗагрузкеВремяНеОбещают()
        {
            var stalled = new DownloadTally(1, 100, 0, 5, 5e6f, DownloadTally.Phase.Stalled);
            Assert.Less(stalled.EtaSeconds, 0f, "вставшей загрузке обещано время дожития");
            var running = new DownloadTally(0, 10_000_000, 0, 5, 1e6f, DownloadTally.Phase.Running);
            Assert.AreEqual(10f, running.EtaSeconds, 0.1f);
        }
    }
}
