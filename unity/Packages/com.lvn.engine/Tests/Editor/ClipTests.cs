using Lvn;
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

        [Test]
        public void ShortIdIsNotTouched()
        {
            Assert.AreEqual("u_1234", LvnClip.Id("u_1234"), "короткий id и так читается целиком");
        }

        [Test]
        public void CompositeCharacterIsNeverSplit()
        {
            // «é» разложенной формой — буква плюс отдельный акут. Записано
            // КОДАМИ: буквой любой редактор пересобрал бы её в один знак, и
            // тест ловил бы пустоту.
            const string decomposed = "abcd\u0065\u0301fghij";
            Assert.AreEqual(11, decomposed.Length, "фикстура собрана — тест ничего не ловит");

            var s = LvnClip.Text(decomposed, 6);
            Assert.IsFalse(s.Contains("\u0301"),
                "разрез внутри составного символа рисуется отдельной палочкой над многоточием");
        }

        [Test]
        public void OneCharacterOverTheLimitIsStillCut()
        {
            // Правило простое и проверяемое: длиннее предела — режем, ровно по
            // пределу — нет.
            Assert.AreEqual("Ровно", LvnClip.Text("Ровно", 5));
            Assert.AreEqual("Ровн…", LvnClip.Text("Ровно!", 5));
        }

        [Test]
        public void NoRoomForAnythingButTheEllipsis()
        {
            Assert.AreEqual("…", LvnClip.Text("Длинная строка", 1));
        }

        [Test]
        public void HeadOfOnlyPunctuationBecomesJustTheEllipsis()
        {
            // «— …» читается как мусор; оставляем один знак сокращения.
            Assert.AreEqual("…", LvnClip.Text("— — — — реплика", 5));
        }

        [Test]
        public void NoLimitMeansNoCut()
        {
            // Ноль и отрицательное — «предел не задан», а не «оставить ничего».
            Assert.AreEqual("Привет", LvnClip.Text("Привет", 0));
            Assert.AreEqual("Привет", LvnClip.Text("Привет", -5));
        }

        [Test]
        public void TheEllipsisIsOneCharacter()
        {
            // Три точки подряд выглядят как опечатка и занимают втрое больше места.
            Assert.AreEqual(1, LvnClip.Ellipsis.Length);
        }

        [Test]
        public void OnePreviewLengthForEveryScreen()
        {
            // Превью одной и той же записи, обрезанное по-разному, читается как
            // разные сохранения: 40 в карусели против 46 в меню сцены.
            var line = new string('я', 100);
            var preview = LvnClip.Text(line, LvnClip.PreviewMax);
            Assert.LessOrEqual(preview.Length, LvnClip.PreviewMax,
                "превью обязано влезать в строку списка вместе с многоточием");
            Assert.Greater(preview.Length, LvnClip.PreviewMax / 2,
                "и не быть куцым: обрезок должен оставаться узнаваемой репликой");
        }
    
        [Test]
        public void Жёсткий_предел_не_рвёт_пару()
        {
            // Эмодзи — ДВЕ единицы UTF-16. Предел ровно между ними обязан
            // отступить, а не оставить половину: половина суррогата не текст.
            const string s = "abcd\U0001F600";           // 4 + 2 = 6 единиц
            var cut = LvnClip.Head(s, 5);
            Assert.AreEqual("abcd", cut, "предел пришёлся на середину пары — надо отступить");
            Assert.IsFalse(char.IsHighSurrogate(cut[cut.Length - 1]), "хвост остался половиной пары");
        }

        [Test]
        public void Жёсткий_предел_не_приписывает_многоточия()
        {
            Assert.AreEqual("абв", LvnClip.Head("абвгд", 3),
                "это провод, а не подпись: многоточие здесь было бы данными, которых игрок не писал");
        }

        [Test]
        public void Первая_буква_а_не_первая_единица()
        {
            Assert.AreEqual("\U0001F600", LvnClip.FirstLetter("\U0001F600Аня"),
                "Substring(0,1) вернул бы половину эмодзи — в кружке аватара это «□»");
            Assert.AreEqual("А", LvnClip.FirstLetter("Аня"));
            Assert.AreEqual("?", LvnClip.FirstLetter(""), "пустое имя — знак вопроса, а не падение");
            Assert.AreEqual("?", LvnClip.FirstLetter(null));
        }
}
}
