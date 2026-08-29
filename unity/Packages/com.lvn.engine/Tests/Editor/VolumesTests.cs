using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>Громкость канала = ползунок канала × общий тумблер. Одна формула на
    /// все каналы: озвучка когда-то бралась прямо с ползунка и звучала при
    /// выключенном звуке.</summary>
    public class VolumesTests
    {
        [SetUp]
        public void Setup()
        {
            LvnPrefs.SoundOn = true;
            LvnPrefs.VolMusic = 1f;
            LvnPrefs.VolAmbient = 1f;
            LvnPrefs.VolSfx = 1f;
            LvnPrefs.VolVoice = 1f;
        }

        [TearDown]
        public void Clean() => Setup();

        [Test]
        public void КаждыйКаналБерётСвойПолзунок()
        {
            LvnPrefs.VolMusic = 0.2f;
            LvnPrefs.VolAmbient = 0.4f;
            LvnPrefs.VolSfx = 0.6f;
            LvnPrefs.VolVoice = 0.8f;

            Assert.AreEqual(0.2f, LvnVolumes.Of(LvnVolumes.Music), 0.001f);
            Assert.AreEqual(0.4f, LvnVolumes.Of(LvnVolumes.Ambient), 0.001f);
            Assert.AreEqual(0.6f, LvnVolumes.Of(LvnVolumes.Sfx), 0.001f);
            Assert.AreEqual(0.8f, LvnVolumes.Of(LvnVolumes.Voice), 0.001f);
        }

        [Test]
        public void ТумблерГаситВСЕКаналыВключаяОзвучку()
        {
            // Живой дефект: реплика начинала звучать в полную громкость при
            // выключенном тумблере — канал озвучки в таблице просто забыли.
            LvnPrefs.SoundOn = false;
            foreach (var ch in new[] { LvnVolumes.Music, LvnVolumes.Ambient,
                                       LvnVolumes.Sfx, LvnVolumes.Voice, LvnVolumes.Ui })
                Assert.AreEqual(0f, LvnVolumes.Of(ch), 0.0001f, ch);
            Assert.AreEqual(0f, LvnVolumes.Master, 0.0001f);
        }

        [Test]
        public void ТумблерНеСтираетПоложениеПолзунков()
        {
            LvnPrefs.VolMusic = 0.3f;
            LvnPrefs.SoundOn = false;
            Assert.AreEqual(0f, LvnVolumes.Of(LvnVolumes.Music), 0.0001f);

            LvnPrefs.SoundOn = true;
            Assert.AreEqual(0.3f, LvnVolumes.Of(LvnVolumes.Music), 0.001f,
                "тумблер — множитель, а не «ползунки в ноль»: игрок вернётся к своему уровню");
        }

        [Test]
        public void НезнакомыйКаналСчитаетсяЭффектомАНеПолнойГромкостью()
        {
            // Новая команда звука не должна звучать МИМО настроек только потому,
            // что её забыли внести в таблицу.
            LvnPrefs.VolSfx = 0.25f;
            Assert.AreEqual(0.25f, LvnVolumes.Of("вздох"), 0.001f);
            Assert.AreEqual(0.25f, LvnVolumes.Of(null), 0.001f, "пустое имя канала — тоже эффект");
            Assert.AreEqual(0.25f, LvnVolumes.Of(LvnVolumes.Ui), 0.001f);
        }

        [Test]
        public void ИменаКаналовТеЖеЧтоВАвторскихКомандах()
        {
            Assert.AreEqual("music", LvnVolumes.Music);
            Assert.AreEqual("ambient", LvnVolumes.Ambient);
            Assert.AreEqual("sfx", LvnVolumes.Sfx);
            Assert.AreEqual("voice", LvnVolumes.Voice);
        }
    }
}
