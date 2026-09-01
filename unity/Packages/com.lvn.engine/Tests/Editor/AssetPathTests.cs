using NUnit.Framework;
using Lvn;

namespace Lvn.Tests
{
    /// <summary>
    /// Дом путей: приставка контента — СЛОВО, а не написание.
    ///
    /// <para>Проверять тут стоит ровно то, на чём копии и разошлись: слэшную
    /// форму срезал поставщик, бесслэшную — индекс версий загрузчика, и пока
    /// каждый резал по-своему, никто не был неправ.</para>
    /// </summary>
    public class AssetPathTests
    {
        [Test]
        public void Оба_написания_приставки_дают_один_путь()
        {
            Assert.AreEqual("bg/room.png", LvnAssetPath.Relative("/content/bg/room.png"));
            Assert.AreEqual("bg/room.png", LvnAssetPath.Relative("content/bg/room.png"),
                "бесслэшной формой написан ключ индекса версий — срез обязан узнать и её");
        }

        [Test]
        public void Адрес_без_приставки_остаётся_собой()
        {
            Assert.AreEqual("bg/room.png", LvnAssetPath.Relative("bg/room.png"));
            Assert.AreEqual("bg/room.png", LvnAssetPath.Relative("/bg/room.png"),
                "ведущая косая — знак препинания, а не часть имени");
        }

        [Test]
        public void Похожее_слово_не_считается_приставкой()
        {
            Assert.AreEqual("contentious/x.png", LvnAssetPath.Relative("contentious/x.png"),
                "срез по слову, а не по буквам: папка со схожим именем не приставка");
        }

        [Test]
        public void Пустой_адрес_остаётся_пустым()
        {
            Assert.IsNull(LvnAssetPath.Relative(null));
            Assert.IsNull(LvnAssetPath.Relative(""));
            Assert.IsNull(LvnAssetPath.Under(null));
        }

        [Test]
        public void Своя_приставка_поставщика_уважается()
        {
            Assert.AreEqual("x.png", LvnAssetPath.Relative("/assets/x.png", "/assets"));
            Assert.AreEqual("x.png", LvnAssetPath.Relative("assets/x.png", "/assets"));
            Assert.AreEqual("/content/x.png", LvnAssetPath.Relative("/content/x.png", ""),
                "пустая приставка значит «не срезать», а не «срезать умолчание»");
        }

        [Test]
        public void Обратная_дорога_возвращает_туда_же()
        {
            Assert.AreEqual("/content/ui/words.ru.json", LvnAssetPath.Under("ui/words.ru.json"));
            Assert.AreEqual("/content/ui/words.ru.json", LvnAssetPath.Under("/ui/words.ru.json"),
                "лишняя косая у зовущего не должна удваиваться в адресе");
            const string rel = "bg/room.png";
            Assert.AreEqual(rel, LvnAssetPath.Relative(LvnAssetPath.Under(rel)),
                "туда и обратно — тождество; иначе стороны разъедутся молча");
        }
    }
}
