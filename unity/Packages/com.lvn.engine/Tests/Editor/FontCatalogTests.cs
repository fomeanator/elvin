using Lvn.UI;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// <summary>Каталог гарнитур: настройка, которую нельзя проверить взглядом,
    /// ощущается сломанной — значит каждый вариант обязан существовать и
    /// отличаться от соседей.</summary>
    public class FontCatalogTests
    {
        private string _saved;

        [SetUp]
        public void Save() => _saved = LvnPrefs.FontFamily;

        [TearDown]
        public void Restore() => LvnPrefs.FontFamily = _saved;

        [Test]
        public void EveryFamilyLoadsFromResources()
        {
            foreach (var f in LvnFonts.Families)
            {
                Assert.IsNotNull(Resources.Load<Font>(f.Path), $"нет файла шрифта: {f.Title} ({f.Path})");
                Assert.IsNotNull(Resources.Load<Font>(f.Display), $"нет заголовочного: {f.Title} ({f.Display})");
            }
        }

        [Test]
        public void KeysAreUniqueAndPathsDiffer()
        {
            var ids = new System.Collections.Generic.HashSet<string>();
            var paths = new System.Collections.Generic.HashSet<string>();
            foreach (var f in LvnFonts.Families)
            {
                Assert.IsTrue(ids.Add(f.Id), $"ключ повторяется: {f.Id}");
                Assert.IsTrue(paths.Add(f.Path), $"две гарнитуры смотрят в один файл: {f.Path}");
            }
            Assert.GreaterOrEqual(LvnFonts.Families.Length, 5);
        }

        [Test]
        public void PlayerChoiceWinsAndCanBeReturned()
        {
            LvnPrefs.FontFamily = "";
            Assert.IsFalse(LvnFonts.PlayerPicked, "пусто — гарнитуру выбирает новелла");

            LvnPrefs.FontFamily = "caveat";
            Assert.IsTrue(LvnFonts.PlayerPicked);
            Assert.AreEqual("caveat", LvnFonts.Chosen.Id);

            LvnPrefs.FontFamily = "";
            Assert.IsFalse(LvnFonts.PlayerPicked, "«как в игре» обязано возвращать тему, а не прошлый выбор");
            Assert.AreEqual(LvnFonts.Families[0].Id, LvnFonts.Chosen.Id);
        }

        [Test]
        public void OpticalSizeIsCorrectedPerFamily()
        {
            // Один кегль у разных гарнитур выглядит разной величиной, и
            // поправка на это ИЗМЕРЯЕТСЯ по шрифту. Направление поправки тест
            // больше не диктует: раньше здесь стояло «рукописную поднимаем,
            // пиксельную опускаем» — числа подобранные глазом, и именно они
            // разъехались («от руки огромен, а пиксель мал»). Проверяем то,
            // что действительно обязано выполняться.
            LvnPrefs.FontFamily = "";
            Assert.AreEqual(30, LvnFonts.Size(30), "без выбора игрока авторский кегль не трогаем");

            LvnPrefs.FontFamily = LvnFonts.Families[0].Id;
            Assert.AreEqual(30, LvnFonts.Size(30), "эталонная гарнитура — та, под которую подбирали кегли");

            bool anyCorrected = false;
            foreach (var f in LvnFonts.Families)
            {
                LvnPrefs.FontFamily = f.Id;
                if (LvnFonts.Size(30) != 30) anyCorrected = true;
            }
            Assert.IsTrue(anyCorrected, "ни одна гарнитура не поправлена — измерение молчит");
        }

        [Test]
        public void СтупениКегляСохраняютПропорцию()
        {
            // Поправка — ОДИН множитель на гарнитуру, а не своя кривая для
            // мелкого и крупного. Значит отношение «крупное к мелкому»
            // остаётся авторским у любой гарнитуры: макет, собранный на
            // ступенях 20/64, не разъезжается при смене шрифта.
            //
            // Прежде здесь жило «сжатие шкалы» — крупное подтягивалось к
            // мелкому, чтобы характерные гарнитуры не превращали заголовок в
            // плакат. Оно решало последствие того, что множитель был подобран
            // неверно; с измеренным множителем лечить нечего.
            LvnPrefs.FontFamily = "";
            float author = LvnFonts.Size(64) / (float)LvnFonts.Size(20);

            foreach (var f in LvnFonts.Families)
            {
                LvnPrefs.FontFamily = f.Id;
                int small = LvnFonts.Size(20), big = LvnFonts.Size(64);
                Assert.Less(small, big, $"{f.Title}: порядок ступеней обязан сохраниться");
                // Допуск — округление кегля до целых пикселей, не более.
                Assert.That(big / (float)small, Is.EqualTo(author).Within(0.15f * author),
                    $"{f.Title}: разброс ступеней разошёлся с авторским");
            }
        }

        [Test]
        public void OneBadCatalogRowCannotBreakEveryScreen()
        {
            // Размер меряется по самому шрифту, и метрики могут прийти
            // сломанными — границы держат вёрстку в любом случае.
            foreach (var f in LvnFonts.Families)
            {
                LvnPrefs.FontFamily = f.Id;
                foreach (int b in new[] { 20, 30, 48, 64 })
                {
                    Assert.GreaterOrEqual(LvnFonts.Size(b), Mathf.RoundToInt(b * 0.5f) - 1, f.Title);
                    Assert.LessOrEqual(LvnFonts.Size(b), Mathf.RoundToInt(b * 2.5f) + 1, f.Title);
                }
            }
        }

        [Test]
        public void БуквыОдинаковойВысотыУВсехГарнитур()
        {
            // Смысл поправки: при одном кегле СТРОЧНАЯ буква выходит одной
            // высоты у любой гарнитуры. Раньше поправка подбиралась глазом, и
            // «От руки» была вдвое крупнее «Пикселя» при одном и том же 30.
            const int Kegl = 30;
            LvnPrefs.FontFamily = LvnFonts.Families[0].Id;
            float reference = LetterPixels(Kegl);
            Assert.Greater(reference, 0f, "эталонную гарнитуру не измерить — тест бессмыслен");

            foreach (var f in LvnFonts.Families)
            {
                LvnPrefs.FontFamily = f.Id;
                float mine = LetterPixels(Kegl);
                if (mine <= 0f) continue;   // шрифт не собрался в SDF — Size честно оставит кегль
                Assert.That(mine, Is.EqualTo(reference).Within(0.25f * reference),
                    $"{f.Title}: строчная {mine:0.#}px против эталонных {reference:0.#}px при кегле {Kegl}");
            }
        }

        // Высота основной буквы в пикселях при данном кегле — то, что видит глаз.
        private static float LetterPixels(int kegl)
        {
            var fam = LvnFonts.Chosen;
            var font = Resources.Load<Font>(fam.Path);
            if (font == null) return 0f;
            var fa = LvnFonts.From(font);
            if (fa == null || fa.faceInfo.pointSize <= 0f) return 0f;
            var face = fa.faceInfo;
            float x = face.meanLine - face.baseline;
            if (x <= 0.0001f) x = face.capLine - face.baseline;
            if (x <= 0.0001f) return 0f;
            return x / face.pointSize * LvnFonts.Size(kegl);
        }

        [Test]
        public void SizeNeverCollapsesToZero()
        {
            LvnPrefs.FontFamily = "pixel";
            Assert.GreaterOrEqual(LvnFonts.Size(1), 1, "кегль ноль — это невидимый текст");
        }

        [Test]
        public void UnknownKeyFallsBackInsteadOfLeavingNoFont()
        {
            LvnPrefs.FontFamily = "нет-такой";
            Assert.AreEqual(LvnFonts.Families[0].Id, LvnFonts.FamilyOf(LvnPrefs.FontFamily).Id,
                "неизвестный ключ (старая настройка, чужая сборка) не имеет права оставить текст без шрифта");
        }
    }
}
