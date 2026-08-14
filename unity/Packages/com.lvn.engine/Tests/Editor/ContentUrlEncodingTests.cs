using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// Имена файлов приходят от художника, а не от программиста: пробелы,
    /// скобки, кириллица. На проде таких шесть из трёхсот сорока девяти, и
    /// отдавались они только закодированными — сырой адрес сервер не понимает.
    /// Промах выглядит как «пропала картинка», и ищут его в контенте, хотя
    /// сломан транспорт.
    /// </summary>
    public class ContentUrlEncodingTests
    {
        [Test]
        public void Encodes_SpacesBracketsAndCyrillic()
        {
            Assert.AreEqual("/content/art/cover%20%281%29.jpg",
                ContentLoader.EncodeUrlPath("/content/art/cover (1).jpg"));
            Assert.AreEqual("/content/art/%D0%A1%D0%BD%D0%B8%D0%BC%D0%BE%D0%BA.png",
                ContentLoader.EncodeUrlPath("/content/art/Снимок.png"));
        }

        [Test]
        public void KeepsSlashesAsSeparators()
        {
            var got = ContentLoader.EncodeUrlPath("/content/bg/ночь/дом 2.jpg");
            StringAssert.StartsWith("/content/bg/", got);
            Assert.AreEqual(5, got.Split('/').Length, "разделители пути обязаны остаться разделителями");
        }

        [Test]
        public void DoesNotDoubleEncode()
        {
            // Второй проход по уже закодированному превратил бы %20 в %2520 и
            // сломал ровно то, что работало.
            const string encoded = "/content/art/cover%20(1).jpg";
            Assert.AreEqual(encoded, ContentLoader.EncodeUrlPath(encoded));
        }

        [Test]
        public void LeavesSchemeHostAndQueryAlone()
        {
            Assert.AreEqual("https://x.test/content/a%20b.png?v=abc",
                ContentLoader.EncodeUrlPath("https://x.test/content/a b.png?v=abc"));
            Assert.AreEqual("https://x.test", ContentLoader.EncodeUrlPath("https://x.test"));
        }

        [Test]
        public void PlainAsciiPathIsUntouched()
        {
            const string plain = "/content/scripts/cold-ch01.lvn";
            Assert.AreEqual(plain, ContentLoader.EncodeUrlPath(plain));
        }
    }
}
