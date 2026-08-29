using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>Адрес ресурса: <c>Bare</c> — это КЛЮЧ КЭША, а расширение считается
    /// по адресу без запроса, иначе «.png?v=3» перестаёт быть картинкой.</summary>
    public class UrlTests
    {
        [Test]
        public void ПустоеОстаётсяПустым()
        {
            Assert.AreEqual("", LvnUrl.Bare(null));
            Assert.AreEqual("", LvnUrl.Bare(""));
            Assert.AreEqual("", LvnUrl.Extension(null), "разбор адреса не бросает на пустом");
            Assert.AreEqual("", LvnUrl.Extension(""));
        }

        [Test]
        public void ЗапросОтрезаетсяЦеликом()
        {
            Assert.AreEqual("https://x/a.png", LvnUrl.Bare("https://x/a.png?v=3"));
            Assert.AreEqual("https://x/a.png", LvnUrl.Bare("https://x/a.png?v=3&w=512"),
                "второй знак вопроса внутри запроса — часть запроса, а не новый адрес");
        }

        [Test]
        public void АдресБезЗапросаНеТрогают()
        {
            Assert.AreEqual("https://x/a.png", LvnUrl.Bare("https://x/a.png"),
                "иначе один и тот же файл считается то скачанным, то нет");
        }

        [Test]
        public void ВерсияВЗапросеНеМеняетКлючКэша()
        {
            // Ради этого правило и живёт в одном доме: разойдись оно на символ —
            // офлайн начнёт вести себя по-разному в разных экранах.
            Assert.AreEqual(LvnUrl.Bare("bg/room.png"), LvnUrl.Bare("bg/room.png?v=7"));
        }

        [Test]
        public void РасширениеСчитаетсяПоАдресуБезЗапроса()
        {
            Assert.AreEqual("png", LvnUrl.Extension("https://x/a.png?v=3"));
            Assert.AreEqual("png", LvnUrl.Extension("https://x/a.png?fallback=.jpg"),
                "точка внутри запроса не должна подменять расширение файла");
        }

        [Test]
        public void РасширениеБезТочкиИВНижнемРегистре()
        {
            Assert.AreEqual("ktx2", LvnUrl.Extension("a.KTX2"));
            Assert.AreEqual("png", LvnUrl.Extension("A.PNG"));
        }

        [Test]
        public void ТочкаВПапкеНеСтановитсяРасширением()
        {
            // «assets.v2/room» — файл без расширения, а не файл «.v2/room».
            Assert.AreEqual("", LvnUrl.Extension("https://x/assets.v2/room"));
            Assert.AreEqual("", LvnUrl.Extension("https://x/noext"));
        }

        [Test]
        public void ТочкаВПапкеНеМешаетРасширениюФайла()
        {
            Assert.AreEqual("webp", LvnUrl.Extension("https://x/assets.v2/room.webp"));
        }
    }
}
