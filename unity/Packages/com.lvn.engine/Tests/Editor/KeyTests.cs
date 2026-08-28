using System.Text;
using Lvn;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// КЛЮЧ ИМЕНИ — «Майя» из манифеста и «Майя» из пути на диске обязаны
    /// сойтись.
    ///
    /// <para>Проверяется именно то, из-за чего расхождение было незаметным:
    /// строки выглядят ОДИНАКОВО, а состоят из разных знаков. Тест пишет их
    /// разложенной формой явно, иначе редактор молча сохранил бы обе в одной.</para>
    /// </summary>
    public class KeyTests
    {
        // «Майя» и «Алёна» разложенной формой, ЗАПИСАННЫЕ КОДАМИ: «й» — это «и»
        // (U+0438) плюс краткая (U+0306), «ё» — «е» (U+0435) плюс диерезис
        // (U+0308). Кодами, а не буквами, потому что любой редактор по дороге
        // молча пересобрал бы их — и тест сравнивал бы строку саму с собой.
        private const string MayaDecomposed = "\u041c\u0430\u0438\u0306\u044f";
        private const string AlenaDecomposed = "\u0410\u043b\u0435\u0308\u043d\u0430";

        [Test]
        public void ComposedAndDecomposed_ShareOneKey()
        {
            Assert.AreEqual(LvnKey.Normalize("\u041c\u0430\u0439\u044f"), LvnKey.Normalize(MayaDecomposed),
                "«й» разложенной формой теряла надстрочный знак: «майя» против «маия»");
            Assert.AreEqual(LvnKey.Normalize("\u0410\u043b\u0451\u043d\u0430"), LvnKey.Normalize(AlenaDecomposed),
                "«ё» разложенной формой теряла точки: «алёна» против «алена»");
        }

        [Test]
        public void TheDecomposedFormIsReallyDifferent()
        {
            // Страховка от тщетного теста: если фикстура вдруг окажется уже
            // собранной, проверка выше пройдёт, ничего не проверив.
            Assert.AreNotEqual("\u041c\u0430\u0439\u044f", MayaDecomposed, "фикстура собрана — тест ничего не ловит");
            Assert.AreEqual("\u041c\u0430\u0439\u044f", MayaDecomposed.Normalize(NormalizationForm.FormC));
        }

        [Test]
        public void SeparatorsAndCase_StillDoNotMatter()
        {
            Assert.AreEqual(LvnKey.Normalize("Guard post"), LvnKey.Normalize("guard_post"));
            Assert.AreEqual(LvnKey.Normalize("House-Platon"), LvnKey.Normalize("houseplaton"));
            Assert.AreEqual("", LvnKey.Normalize(null));
        }
    }
}
