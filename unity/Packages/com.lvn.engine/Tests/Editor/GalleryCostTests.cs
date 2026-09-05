using System.Diagnostics;
using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ГАЛЕРЕЯ ОТКРЫВАЕТСЯ СРАЗУ, А ЗАБЫТОЕ НЕ ВОЗВРАЩАЕТСЯ.
    ///
    /// <para>Экран галереи спрашивает про КАЖДУЮ карточку отдельно, и каждый
    /// вопрос читал книжку и разбирал весь набор заново: замер 05.09 — 50
    /// карточек 1 мс, 200 — 15 мс, 500 — 88 мс, цена росла квадратом от
    /// наполнения. У большой игры галерея на сотни позиций — обычное дело.</para>
    ///
    /// <para>Вторая половина — про вчерашнюю починку «удалить аккаунт»: она
    /// стирала диск, а наборы, которые дома держат в ПАМЯТИ, не трогала. Замер:
    /// после «забыть игрока» прочитанное продолжало отвечать «читал», и первая
    /// же отметка нового игрока сохраняла старый набор обратно. Забвение
    /// объявляется теперь всем домам сразу.</para>
    /// </summary>
    public class GalleryCostTests
    {
        private const string Title = "замер-галереи";

        [TearDown]
        public void Clean() => LvnGalleryStore.Clear(Title);

        private static long ОткрытьМс(int карточек)
        {
            LvnGalleryStore.Clear(Title);
            for (int i = 0; i < карточек; i++) LvnGalleryStore.Unlock(Title, "cg" + i);

            // Экран галереи спрашивает КАЖДУЮ карточку по отдельности.
            var sw = Stopwatch.StartNew();
            int открыто = 0;
            for (int i = 0; i < карточек; i++)
                if (LvnGalleryStore.IsUnlocked(Title, "cg" + i)) открыто++;
            sw.Stop();
            Assert.AreEqual(карточек, открыто, "стенд: не все карточки открылись");
            return sw.ElapsedMilliseconds;
        }

        [Test]
        public void ЦенаОткрытияГалереиНеРастётКвадратом()
        {
            long малая = System.Math.Max(1, ОткрытьМс(200));
            long большая = ОткрытьМс(800);

            TestContext.WriteLine($"опрос экрана: 200 карточек {малая} мс, 800 карточек {большая} мс "
                                + $"(отношение {(double)большая / малая:F1}; линейно ≈4, квадратично ≈16)");

            Assert.Less((double)большая / малая, 8.0,
                "вчетверо большая галерея открывается дольше чем вчетверо — набор разбирается на каждую карточку");
        }

        /// Забвение доходит до памяти домов, а не только до диска.
        [Test]
        public void ЗабытоеНеВозвращаетсяИзПамяти()
        {
            const string t = "замер-забвение";
            LvnReadStore.Clear(t);
            LvnGalleryStore.Clear(t);
            LvnReadStore.MarkRead(t, "Герой", "старая реплика");
            LvnReadStore.FlushNow();
            LvnGalleryStore.Unlock(t, "cg-старый");

            Lvn.LvnKeep.ForgetPlayerData();

            TestContext.WriteLine($"сразу после забвения: прочитано={LvnReadStore.IsRead(t, "Герой", "старая реплика")}, "
                                + $"галерея={LvnGalleryStore.IsUnlocked(t, "cg-старый")}");

            // Первая запись нового игрока — не утащит ли она за собой старое?
            LvnReadStore.MarkRead(t, "Герой", "новая реплика");
            LvnReadStore.FlushNow();
            LvnGalleryStore.Unlock(t, "cg-новый");

            TestContext.WriteLine($"после первой записи нового игрока: старое прочитано="
                                + $"{LvnReadStore.IsRead(t, "Герой", "старая реплика")}, "
                                + $"старая галерея={LvnGalleryStore.IsUnlocked(t, "cg-старый")}");

            Assert.IsFalse(LvnReadStore.IsRead(t, "Герой", "старая реплика"),
                "забытая отметка вернулась из памяти дома — и уехала обратно на диск");
            Assert.IsFalse(LvnGalleryStore.IsUnlocked(t, "cg-старый"),
                "забытое открытое вернулось из памяти дома");
            Assert.IsTrue(LvnReadStore.IsRead(t, "Герой", "новая реплика"),
                "своя отметка нового игрока не записалась");
            Assert.IsTrue(LvnGalleryStore.IsUnlocked(t, "cg-новый"),
                "своё открытое нового игрока не записалось");

            LvnReadStore.Clear(t);
            LvnGalleryStore.Clear(t);
        }

        /// «Увидел однажды — видно всегда»: открытое переживает удаление
        /// сохранений и новое прохождение. Это обещание записано в самом доме,
        /// а проверки у него не было.
        [Test]
        public void ОткрытоеПереживаетУдалениеСохранений()
        {
            LvnGalleryStore.Clear(Title);
            LvnGalleryStore.Unlock(Title, "cg-1");

            LvnSaveStore.DeleteAll(Title);

            Assert.IsTrue(LvnGalleryStore.IsUnlocked(Title, "cg-1"),
                "открытое пропало вместе с сохранениями — «увидел однажды» перестало значить «навсегда»");
        }
    }
}
