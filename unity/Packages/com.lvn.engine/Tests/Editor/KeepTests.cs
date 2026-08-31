using Lvn;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// <summary>
    /// ЗАПИСНАЯ КНИЖКА — хранит и решает вопрос фиксации явно.
    ///
    /// <para>Проверить «переживёт ли краш» в EditMode нельзя — фиксация уходит
    /// в операционную систему. Проверяется то, что от книжки зависит: значения
    /// читаются обратно, стирание стирает, пачка не рассыпается, а карандаш
    /// остаётся карандашом до <c>Flush</c>. Само правило «набело или в
    /// карандаше — но всегда сказано вслух» живёт в именах глаголов.</para>
    /// </summary>
    public class KeepTests
    {
        private const string K = "lvn_test_keep_";

        [TearDown]
        public void Teardown()
        {
            foreach (var k in new[] { "a", "b", "c", "n", "f" })
                PlayerPrefs.DeleteKey(K + k);
            PlayerPrefs.Save();
        }

        [Test]
        public void ЗаписьИЧтение_ТрёхВидов()
        {
            LvnKeep.Put(K + "a", "слово");
            LvnKeep.Put(K + "n", 42);
            LvnKeep.Put(K + "f", 1.5f);

            Assert.AreEqual("слово", LvnKeep.Get(K + "a", ""));
            Assert.AreEqual(42, LvnKeep.Get(K + "n", 0));
            Assert.AreEqual(1.5f, LvnKeep.Get(K + "f", 0f), 1e-5f);
        }

        [Test]
        public void НичегоНеЗаписано_ЭтоЗапасноеЗначение()
        {
            Assert.AreEqual("пусто", LvnKeep.Get(K + "нет такого", "пусто"));
            Assert.AreEqual(7, LvnKeep.Get(K + "нет такого", 7));
            Assert.IsFalse(LvnKeep.Has(K + "нет такого"));
        }

        [Test]
        public void Стирание_СтираетИВидноПоHas()
        {
            LvnKeep.Put(K + "a", "есть");
            Assert.IsTrue(LvnKeep.Has(K + "a"));

            LvnKeep.Drop(K + "a");

            Assert.IsFalse(LvnKeep.Has(K + "a"), "стёртое не «пустая строка», а отсутствие");
            Assert.AreEqual("запас", LvnKeep.Get(K + "a", "запас"));
        }

        [Test]
        public void ПачкаНеТеряетЗаписей()
        {
            using (LvnKeep.Batch())
            {
                LvnKeep.Put(K + "a", "раз");
                LvnKeep.Put(K + "b", "два");
                LvnKeep.Put(K + "n", 3);
            }

            Assert.AreEqual("раз", LvnKeep.Get(K + "a", ""));
            Assert.AreEqual("два", LvnKeep.Get(K + "b", ""));
            Assert.AreEqual(3, LvnKeep.Get(K + "n", 0));
        }

        [Test]
        public void ВложеннаяПачка_ФиксируетсяОдинРазВКонце()
        {
            // Вложенность бывает не по злому умыслу: пачка снаружи, а внутри
            // вызывается чужой метод, который тоже пишет пачкой.
            using (LvnKeep.Batch())
            {
                LvnKeep.Put(K + "a", "внешняя");
                using (LvnKeep.Batch())
                {
                    LvnKeep.Put(K + "b", "внутренняя");
                }
                LvnKeep.Put(K + "c", "после внутренней");
            }

            Assert.AreEqual("внешняя", LvnKeep.Get(K + "a", ""));
            Assert.AreEqual("внутренняя", LvnKeep.Get(K + "b", ""));
            Assert.AreEqual("после внутренней", LvnKeep.Get(K + "c", ""));
        }

        [Test]
        public void КарандашПишетСразу_АФиксируетсяПотом()
        {
            // Значение доступно немедленно — карандаш про фиксацию на диск, а
            // не про видимость: горячий путь читает то, что сам записал.
            LvnKeep.Jot(K + "a", "начерно");
            Assert.AreEqual("начерно", LvnKeep.Get(K + "a", ""));

            LvnKeep.Flush(); // здесь и ложится на диск
            Assert.AreEqual("начерно", LvnKeep.Get(K + "a", ""));
        }

        [Test]
        public void КарандашноеСтирание_ТожеВидноСразу()
        {
            LvnKeep.Put(K + "a", "было");
            LvnKeep.JotDrop(K + "a");

            Assert.IsFalse(LvnKeep.Has(K + "a"));
            LvnKeep.Flush();
            Assert.IsFalse(LvnKeep.Has(K + "a"));
        }

        [Test]
        public void ПустойКлюч_НеПишетНичего()
        {
            Assert.DoesNotThrow(() => LvnKeep.Put(null, "x"));
            Assert.DoesNotThrow(() => LvnKeep.Put("", 1));
            Assert.DoesNotThrow(() => LvnKeep.Drop(null));
            Assert.DoesNotThrow(() => LvnKeep.Jot("", "x"));
            Assert.AreEqual("запас", LvnKeep.Get(null, "запас"));
            Assert.IsFalse(LvnKeep.Has(""));
        }

        [Test]
        public void ЛишнийFlush_НичегоНеЛомает()
        {
            LvnKeep.Flush();
            LvnKeep.Flush();
            LvnKeep.Put(K + "a", "цел");
            Assert.AreEqual("цел", LvnKeep.Get(K + "a", ""));
        }

        // КЛЮЧ ВЕЩИ, ПРИВЯЗАННОЙ К НОВЕЛЛЕ. Приставки у хранилищ разные и
        // остаются такими (сменить — потерять чужие сохранения), а вот «а если
        // новеллы нет» имело ТРИ ответа: «default», пустая строка и ключ с
        // точкой на конце. Из-за последнего пустое имя и отсутствующее уезжали
        // в РАЗНЫЕ ящики: сохранение, сделанное до выбора новеллы, потом не
        // находилось.
        [Test]
        public void ОтсутствиеНовеллыИмеетОдинОтвет()
        {
            Assert.AreEqual(LvnKeep.Scoped("lvn_save_", null),
                            LvnKeep.Scoped("lvn_save_", ""),
                "пустое имя новеллы и отсутствующее дают разные ящики — "
                + "записанное до выбора новеллы потом не найдётся");
            Assert.IsFalse(LvnKeep.Scoped("lvn_save_", null).EndsWith("_"),
                "ключ кончается приставкой без имени — такой ящик легко занять второй раз");
        }

        // Разные новеллы — разные ящики, одна и та же — один и тот же. Иначе
        // прогресс одной истории затирал бы прогресс соседней.
        [Test]
        public void РазныеНовеллыНеДелятЯщик()
        {
            Assert.AreNotEqual(LvnKeep.Scoped("lvn_save_", "cold"),
                               LvnKeep.Scoped("lvn_save_", "hill"));
            Assert.AreEqual(LvnKeep.Scoped("lvn_save_", "cold"),
                            LvnKeep.Scoped("lvn_save_", "cold"),
                "один и тот же вопрос дал два разных ключа");
            Assert.AreNotEqual(LvnKeep.Scoped("lvn_save_", "cold"),
                               LvnKeep.Scoped("lvn_read_", "cold"),
                "разные хранилища слились в один ящик");
        }
    }
}
