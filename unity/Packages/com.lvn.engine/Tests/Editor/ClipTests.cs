using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>Обрезка длинного текста: обрезок обязан остаться текстом.</summary>
    public class ClipTests
    {
        [Test]
        public void ShortTextIsLeftAlone()
        {
            Assert.AreEqual("Привет", LvnClip.Text("Привет", 10));
            Assert.AreEqual("Ровно", LvnClip.Text("Ровно", 5), "ровно по пределу — не повод резать");
        }

        [Test]
        public void ResultFitsTheLimitWithTheEllipsis()
        {
            var s = LvnClip.Text("Двенадцать слов подряд и ещё немного сверху", 10);
            Assert.LessOrEqual(s.Length, 10, "обрезок с многоточием обязан поместиться в предел");
            Assert.IsTrue(s.EndsWith("…"));
        }

        [Test]
        public void TrailingPunctuationGoesWithTheCut()
        {
            // «Привет, …» читается как опечатка, а не как сокращение.
            Assert.AreEqual("Привет…", LvnClip.Text("Привет, мир и все остальные", 8));
        }

        [Test]
        public void SurrogatePairIsNeverSplit()
        {
            // Эмодзи — два символа; разрез между ними рисуется как «□».
            var s = LvnClip.Text("аб🎭вгдежзийклмн", 6);
            Assert.IsFalse(char.IsHighSurrogate(s[s.Length - 2]),
                "половина суррогатной пары перед многоточием — знак, которого в тексте не было");
        }

        [Test]
        public void EmptyAndNullSurvive()
        {
            Assert.AreEqual("", LvnClip.Text(null, 5));
            Assert.AreEqual("", LvnClip.Text("", 5));
        }

        [Test]
        public void IdKeepsEnoughToBeUseful()
        {
            var id = LvnClip.Id("u_e25fc02ed2b94a7f8c");
            Assert.IsTrue(id.StartsWith("u_e25fc02ed2"), "по короткому id игрок называет себя в поддержке");
            Assert.IsTrue(id.EndsWith("…"));
        }
    }
}
