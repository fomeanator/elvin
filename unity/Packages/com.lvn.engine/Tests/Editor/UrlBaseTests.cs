using NUnit.Framework;
using Lvn;

namespace Lvn.Tests
{
    /// <summary>
    /// БАЗОВЫЙ АДРЕС — <see cref="LvnUrl.Base"/>.
    ///
    /// <para>Правило в одну строку, и потому оно стояло девятью написаниями в
    /// четырёх кварталах. Забывший его получает <c>host//v1/…</c>: одни серверы
    /// отвечают, другие дают 404, и разницу видно только на чужом хосте — то
    /// есть не у того, кто писал.</para>
    /// </summary>
    public class UrlBaseTests
    {
        [Test]
        public void Хвостовая_косая_снимается()
        {
            Assert.AreEqual("https://x.example", LvnUrl.Base("https://x.example/"));
            Assert.AreEqual("https://x.example", LvnUrl.Base("https://x.example"));
        }

        [Test]
        public void Пустое_и_ничто_дают_пустую_строку()
        {
            Assert.AreEqual("", LvnUrl.Base(null), "склейка с null дала бы «null/v1/…»");
            Assert.AreEqual("", LvnUrl.Base(""));
        }

        [Test]
        public void Несколько_косых_снимаются_все()
        {
            Assert.AreEqual("https://x.example", LvnUrl.Base("https://x.example///"),
                "иначе одна из копий правила чинила бы только одну косую");
        }

        [Test]
        public void Склейка_с_путём_даёт_одну_косую()
        {
            Assert.AreEqual("https://x.example/v1/me", LvnUrl.Base("https://x.example/") + "/v1/me",
                "ради этого всё и затевалось: двойная косая — 404 на части серверов");
        }
        // ── Где кончается имя и начинается расширение ────────────────────────

        [Test]
        public void ТочкаВИмениПапкиРасширениемНеСчитается()
        {
            // Ради этого правило и переехало в дом: рукописная копия делила
            // «content/v1.2/cover» по точке в «v1.2» и дальше рассуждала про
            // расширение «.2/cover».
            var (stem, ext) = LvnUrl.SplitExtension("content/v1.2/cover");
            Assert.AreEqual("content/v1.2/cover", stem);
            Assert.AreEqual("", ext, "у адреса без расширения его и нет");
            Assert.AreEqual("", LvnUrl.Extension("content/v1.2/cover"));
        }

        [Test]
        public void ПоловинкиСклеиваютсяОбратно()
        {
            // Расширение отдаётся С ТОЧКОЙ намеренно: склейка не должна
            // догадываться, кто её потерял.
            foreach (var адрес in new[] { "a/b/c.png", "cover.jpg", "x/y.ktx2" })
            {
                var (stem, ext) = LvnUrl.SplitExtension(адрес);
                Assert.AreEqual(адрес, stem + ext, $"склейка не вернула адрес: {адрес}");
            }
        }

        [Test]
        public void ХвостАдресаНеПопадаетВРасширение()
        {
            // Запрос и якорь отбрасывает Bare — иначе «cover.png?v=3» дало бы
            // расширение «.png?v=3».
            Assert.AreEqual("png", LvnUrl.Extension("cover.png?v=3"));
            var (stem, ext) = LvnUrl.SplitExtension("cover.png#top");
            Assert.AreEqual("cover", stem);
            Assert.AreEqual(".png", ext);
        }
    }
}
