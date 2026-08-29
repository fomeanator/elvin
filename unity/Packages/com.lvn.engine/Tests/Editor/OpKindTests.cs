using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>К чему относится команда: один ответ вместо перечислений по месту.
    /// Списки уже начинали расходиться — цена расхождения тихая («иногда мигает»).</summary>
    public class OpKindTests
    {
        [Test]
        public void ВсеПеленыКадраСчитаютсяВуалью()
        {
            // Пропусти здесь новый эффект — и он либо не вернётся после
            // «увести и вернуть», либо перестанет вытеснять предыдущую пелену.
            foreach (var op in new[] { "fade", "dim", "flash", "tint", "blur", "fx" })
            {
                Assert.AreEqual(LvnOpSubject.Veil, LvnOpKind.Of(op), op);
                Assert.IsTrue(LvnOpKind.IsVeil(op), op);
                Assert.IsFalse(LvnOpKind.IsBackground(op), op);
            }
        }

        [Test]
        public void ЗадникПлоскийИТрёхмерныйОдинПредмет()
        {
            foreach (var op in new[] { "bg", "bg3d" })
            {
                Assert.AreEqual(LvnOpSubject.Background, LvnOpKind.Of(op), op);
                Assert.IsTrue(LvnOpKind.IsBackground(op), op);
                Assert.IsFalse(LvnOpKind.IsVeil(op), op);
            }
        }

        [Test]
        public void КтоТоНаСценеЭтоАктёрПредметИГрим()
        {
            foreach (var op in new[] { "actor", "obj", "sfx" })
                Assert.AreEqual(LvnOpSubject.Actor, LvnOpKind.Of(op), op);
        }

        [Test]
        public void НезнакомаяКомандаСамаСебеПредмет()
        {
            foreach (var op in new[] { "say", "choice", "wait", "music", "jump" })
                Assert.AreEqual(LvnOpSubject.Other, LvnOpKind.Of(op), op);
        }

        [Test]
        public void ПустаяКомандаНеБросает()
        {
            Assert.AreEqual(LvnOpSubject.Other, LvnOpKind.Of(null));
            Assert.AreEqual(LvnOpSubject.Other, LvnOpKind.Of(""));
            Assert.IsFalse(LvnOpKind.IsVeil(null));
            Assert.IsFalse(LvnOpKind.IsBackground(null));
        }

        [Test]
        public void ПредметУКомандыРовноОдин()
        {
            // Вуаль и фон не пересекаются: спор за предмет иначе разрешался бы
            // дважды и по-разному.
            foreach (var op in new[] { "fade", "dim", "flash", "tint", "blur", "fx",
                                       "bg", "bg3d", "actor", "obj", "sfx", "say" })
            {
                int hits = (LvnOpKind.IsVeil(op) ? 1 : 0) + (LvnOpKind.IsBackground(op) ? 1 : 0);
                Assert.LessOrEqual(hits, 1, op);
            }
        }
    }
}
