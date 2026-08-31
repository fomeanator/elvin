using System.Collections.Generic;
using Lvn;
using Lvn.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// <summary>
    /// Забвение: игрок попросил себя забыть — и хранилища личного пусты.
    /// Проверяется не «метод позвался», а то, ради чего он есть: НИ ОДНО из
    /// хранилищ не пережило обряд.
    /// </summary>
    public class ForgetTests
    {
        private const string T = "test-forget-title";
        private const string Hero = "test-forget-hero";

        [SetUp]
        [TearDown]
        public void Clean()
        {
            PlayerPrefs.DeleteKey("lvn_slots_" + T);
            PlayerPrefs.DeleteKey("lvn.gallery." + T);
            PlayerPrefs.DeleteKey("lvn.read." + T);
            PlayerPrefs.DeleteKey("lvn_state_" + T);
            PlayerPrefs.DeleteKey("lvn_state_base_" + T);
            PlayerPrefs.DeleteKey("lvn_wardrobe_" + Hero);
            PlayerPrefs.DeleteKey("lvn_state___global");
        }

        private static void FillTitle()
        {
            LvnSaveStore.Put(T, "slot1", new LvnSaveSlot
            {
                Snap = new LvnPlayer.LvnSnapshot { Index = 7, CallStack = new int[0] },
                ChapterId = "ch01",
                Preview = "линия",
            });
            LvnGalleryStore.Unlock(T, "cg-01");
            LvnReadStore.MarkRead(T, "Майя", "Привет");
            PlayerPrefs.SetString("lvn_state_" + T, "{\"vars\":{\"gold\":5}}");
            PlayerPrefs.SetString("lvn_state_base_" + T, "{\"gold\":5}");
        }

        [Test]
        public void TitleWipesEveryPersonalStoreOfThatNovel()
        {
            FillTitle();

            LvnForget.Title(T);

            Assert.AreEqual(0, LvnSaveStore.Slots(T).Count, "сейвы");
            Assert.IsFalse(LvnGalleryStore.IsUnlocked(T, "cg-01"), "галерея");
            Assert.IsFalse(LvnReadStore.IsRead(T, "Майя", "Привет"), "прочитанное");
            Assert.AreEqual("", PlayerPrefs.GetString("lvn_state_" + T, ""), "переменные");
            Assert.AreEqual("", PlayerPrefs.GetString("lvn_state_base_" + T, ""),
                "база синхронизации: пережив стирание, она вернула бы значения с сервера");
        }

        [Test]
        public void TitleKeepsPlayerStatsBecauseTheyOutliveTheExpedition()
        {
            PlayerPrefs.SetString("lvn_state___global", "{\"vars\":{\"karma\":3}}");

            LvnForget.Title(T);

            Assert.AreNotEqual("", PlayerPrefs.GetString("lvn_state___global", ""),
                "«начать заново» стирает экспедицию, а не игрока");
        }

        [Test]
        public void AllWipesWhatOutlivesASingleNovel()
        {
            FillTitle();
            PlayerPrefs.SetString("lvn_state___global", "{\"vars\":{\"karma\":3}}");
            LvnWardrobe.Equip(Hero, "hair", "long");
            LvnPlayerName.Set("Майя");
            LvnPrefs.IntroDone = true;

            LvnForget.All(new[] { T }, new[] { Hero });

            Assert.AreEqual(0, LvnSaveStore.Slots(T).Count, "сейвы");
            Assert.AreEqual("", PlayerPrefs.GetString("lvn_state___global", ""), "статы игрока");
            Assert.AreEqual(0, LvnWardrobe.Equipped(Hero).Count, "гардероб");
            Assert.AreEqual("", LvnPlayerName.Current, "имя");
            Assert.IsFalse(LvnPrefs.IntroDone, "флаг вступления");
        }

        [Test]
        public void RegisteredStoreIsAskedByBothRites()
        {
            string forgottenTitle = null;
            bool forgotAll = false;
            LvnForget.Register("тестовое хранилище", id => forgottenTitle = id, () => forgotAll = true);

            LvnForget.Title(T);
            Assert.AreEqual(T, forgottenTitle, "хранилище оболочки не спросили про новеллу");

            LvnForget.All(null);
            Assert.IsTrue(forgotAll, "хранилище оболочки не спросили про игрока");

            LvnForget.Register("тестовое хранилище", null, null); // не мешать соседям
        }

        [Test]
        public void OneFailingStoreDoesNotStopTheRest()
        {
            FillTitle();
            LvnForget.Register("падучее", _ => throw new System.InvalidOperationException("нарочно"), null);

            LvnForget.Title(T);

            Assert.AreEqual(0, LvnSaveStore.Slots(T).Count,
                "упавшее хранилище остановило забвение — половина игрока осталась");
            LvnForget.Register("падучее", null, null);
        }

        [Test]
        public void ForgettingNobodyIsANoOp()
        {
            // «Начать заново» без выбранной новеллы не имеет права стирать
            // ЧТО-НИБУДЬ наугад.
            FillTitle();

            LvnForget.Title(null);
            LvnForget.Title("");

            Assert.AreEqual(1, LvnSaveStore.Slots(T).Count, "стёрли не ту новеллу");
        }

        [Test]
        public void ForgettingOneNovelLeavesTheNeighbourAlone()
        {
            const string other = "test-forget-other";
            try
            {
                FillTitle();
                LvnGalleryStore.Unlock(other, "cg-01");

                LvnForget.Title(T);

                Assert.IsTrue(LvnGalleryStore.IsUnlocked(other, "cg-01"),
                    "«начать заново» стирает ОДНУ экспедицию, а не соседнюю");
            }
            finally { PlayerPrefs.DeleteKey("lvn.gallery." + other); }
        }

        [Test]
        public void AccountDeletionWithoutACatalogStillForgetsThePlayer()
        {
            // Список новелл может не доехать (нет сети) — личное игрока обязано
            // уйти всё равно.
            LvnPlayerName.Set("Майя");
            PlayerPrefs.SetString("lvn_state___global", "{\"vars\":{\"karma\":3}}");

            LvnForget.All(null);

            Assert.AreEqual("", LvnPlayerName.Current);
            Assert.AreEqual("", PlayerPrefs.GetString("lvn_state___global", ""));
        }

        [Test]
        public void ReRegisteringTheSameStoreDoesNotDoubleIt()
        {
            // Подъём приложения не обязан начинаться с чистой статики: вторая
            // регистрация того же имени ЗАМЕНЯЕТ первую, а не добавляет второго.
            int asked = 0;
            LvnForget.Register("двойное хранилище", _ => asked++, null);
            LvnForget.Register("двойное хранилище", _ => asked++, null);

            LvnForget.Title(T);

            Assert.AreEqual(1, asked);
            LvnForget.Register("двойное хранилище", null, null);
        }

        // ── обряд, повторённый дважды ───────────────────────────────────────

        // Обряд забвения зовут оттуда, где ответ теряется: удаление аккаунта
        // ходит на сервер, экран может не дождаться и предложить кнопку снова.
        // Второй заход обязан быть таким же тихим, как первый: игрок, который
        // жмёт «Удалить» дважды, не должен получить ошибку — она читается как
        // «удалить не вышло», и он останется думать, что игра его помнит.
        [Test]
        public void ПовторноеЗабвениеНеПадаетИНичегоНеВоскрешает()
        {
            FillTitle();
            LvnPlayerName.Set("Майя");
            PlayerPrefs.SetString("lvn_state___global", "{\"vars\":{\"karma\":3}}");

            LvnForget.Title(T);
            Assert.DoesNotThrow(() => LvnForget.Title(T),
                "второе «начать заново» подряд уронило экран — игрок решит, что стереть не вышло");
            Assert.DoesNotThrow(() => LvnForget.All(new[] { T }, new[] { Hero }),
                "удаление аккаунта после «начать заново» уронило экран");
            Assert.DoesNotThrow(() => LvnForget.All(new[] { T }, new[] { Hero }),
                "второе удаление аккаунта подряд уронило экран");

            Assert.AreEqual(0, LvnSaveStore.Slots(T).Count, "повтор обряда вернул сейвы");
            Assert.AreEqual("", LvnPlayerName.Current, "повтор обряда вернул имя игрока");
            Assert.AreEqual("", PlayerPrefs.GetString("lvn_state___global", ""),
                "повтор обряда вернул статы игрока");
        }

        // Новеллу, которую ни разу не открывали, «забыть» просят штатно: кнопка
        // «начать заново» есть на КАЖДОЙ карточке, и по ней жмут из любопытства.
        // Ошибка здесь означала бы красный экран там, где стирать попросту
        // нечего, — и, что важнее, обряд обязан пройти мимо соседних новелл.
        [Test]
        public void ЗабвениеНеигранойНовеллыНеОшибка()
        {
            FillTitle();

            Assert.DoesNotThrow(() => LvnForget.Title("test-forget-никогда-не-открывали"),
                "«начать заново» на непочатой новелле уронило экран");

            Assert.AreEqual(1, LvnSaveStore.Slots(T).Count,
                "забвение неигранной новеллы стёрло прохождение соседней");
        }

        // Личное лежит не только в записной книжке. Миниатюра слота — это КАДР
        // ИГРЫ, снятый на устройстве, и живёт он отдельным PNG-файлом рядом с
        // сохранениями. Промах здесь уже случался: слоты сносили, а картинки
        // оставались лежать на диске — человек «стёр прохождение», а снимки его
        // сцен никуда не делись, и следующее сохранение показывало кадр чужой
        // игры.
        [Test]
        public void ЗабвениеУноситИМиниатюрыСейвовСДиска()
        {
            FillTitle();
            var кадр = new Texture2D(4, 2, TextureFormat.RGBA32, false);
            LvnSaveStore.WriteThumb(T, "slot1", кадр);
            Object.DestroyImmediate(кадр);
            var путь = LvnSaveStore.ThumbPath(T, "slot1");
            Assert.IsTrue(System.IO.File.Exists(путь), "стенд не сложился: миниатюра не записалась");

            LvnForget.Title(T);

            Assert.IsFalse(System.IO.File.Exists(путь),
                "кадр прохождения остался на диске после забвения — стёрли запись, а не картинку");
        }

        // ── что уносит удаление аккаунта ────────────────────────────────────

        // Флагов вступления ДВА, и переживший второй виден игроку сразу.
        // «Вступление пройдено» — воронка: без сброса новый человек на том же
        // устройстве попадает не в первую главу, а на витрину. «Приветствие
        // видели» — экран входа: он здоровается ровно один раз, и уцелей этот
        // флаг, вошедшего заново никто не спросит, кто он.
        [Test]
        public void ВсёУноситОбаФлагаВступления()
        {
            LvnPrefs.IntroDone = true;
            LvnPrefs.SeenWelcome = true;

            LvnForget.All(null);

            Assert.IsFalse(LvnPrefs.IntroDone,
                "вступление осталось «пройденным» — забытый игрок попадёт сразу на витрину");
            Assert.IsFalse(LvnPrefs.SeenWelcome,
                "приветствие осталось «показанным» — вошедшего заново не спросят, кто он");
        }

        // САМОЕ ДОРОГОЕ ИЗ ЗАБЫВАЕМОГО. Постоянная метка — ключ ко всему, что
        // сервер помнит об игроке: кошелёк, покупки, облачные сейвы. Переживи
        // она удаление аккаунта — следующий старт зарегистрируется НЕ новым
        // человеком, а тем же самым, и «я всё удалил» окажется неправдой,
        // которую видно по остаткам на счету. Метка живёт в двух домах
        // (книжка и файл), и уцелевшего хватает, чтобы вернуть игрока в
        // удалённую учётку, — поэтому проверяется результат, а не вызов.
        [Test]
        public void ВсёУноситПостоянныеМетки()
        {
            const string имя = "test_forget_метка";
            var была = LvnMark.Steady(имя);

            LvnForget.All(null);

            Assert.AreNotEqual(была, LvnMark.Steady(имя),
                "метка пережила удаление аккаунта — следующий старт войдёт в удалённую учётку");
            LvnMark.Forget(имя);
        }

        // Хранилища личного адресуются по ключу и списка своих ключей не ведут:
        // перечень новелл приходит из манифеста. Обойди только первую — и всё
        // прочитанное во всех остальных новеллах переживёт удаление аккаунта
        // молча: игрок увидит это, когда откроет вторую историю и обнаружит там
        // свои открытые кадры.
        [Test]
        public void ВсёУноситКаждуюНазваннуюНовеллу()
        {
            const string вторая = "test-forget-вторая";
            try
            {
                FillTitle();
                LvnGalleryStore.Unlock(вторая, "cg-02");
                LvnReadStore.MarkRead(вторая, "Майя", "Привет");

                LvnForget.All(new[] { T, вторая });

                Assert.AreEqual(0, LvnSaveStore.Slots(T).Count, "первая новелла не забыта");
                Assert.IsFalse(LvnGalleryStore.IsUnlocked(вторая, "cg-02"),
                    "обряд остановился на первой новелле — кадры остальных пережили удаление аккаунта");
                Assert.IsFalse(LvnReadStore.IsRead(вторая, "Майя", "Привет"),
                    "прочитанное во второй новелле пережило удаление аккаунта");
            }
            finally
            {
                PlayerPrefs.DeleteKey("lvn.gallery." + вторая);
                PlayerPrefs.DeleteKey("lvn.read." + вторая);
            }
        }

        // Хранилище оболочки бывает ПОЛОВИНЧАТЫМ, и это законно: прогресс умеет
        // забывать одну новеллу, а серверный сейф прогресса — только всё сразу.
        // Так они и объявлены при подъёме приложения. Спроси у половинчатого
        // недостающую половину — и удаление аккаунта упадёт на первом же из них.
        [Test]
        public void ХранилищеБезОднойИзПоловинОбрядНеЛомает()
        {
            bool целиком = false;
            LvnForget.Register("только целиком", null, () => целиком = true);
            try
            {
                Assert.DoesNotThrow(() => LvnForget.Title(T),
                    "хранилище без новелльной части уронило «начать заново»");
                LvnForget.All(null);
                Assert.IsTrue(целиком, "хранилище без новелльной части не спросили и про игрока");
            }
            finally { LvnForget.Register("только целиком", null, null); }
        }

        // Удаление аккаунта обходит новеллы по одной, а потом снимает общее —
        // и хранилища оболочки обязаны попасть в ОБА захода. Прогресс объявлен
        // как раз по-новелльно: пропусти его в поимённом обходе — и «продолжить»
        // на витрине будет звать удалённого игрока обратно в седьмую главу.
        [Test]
        public void ВсёСпрашиваетХранилищеОболочкиПоКаждойНовелле()
        {
            var спрошены = new List<string>();
            bool целиком = false;
            LvnForget.Register("тестовый прогресс", id => спрошены.Add(id), () => целиком = true);
            try
            {
                LvnForget.All(new[] { T, "test-forget-вторая" });

                CollectionAssert.AreEqual(new[] { T, "test-forget-вторая" }, спрошены,
                    "удаление аккаунта обошло не все новеллы — их прогресс пережил обряд");
                Assert.IsTrue(целиком, "общая часть хранилища оболочки не спрошена");
            }
            finally { LvnForget.Register("тестовый прогресс", null, null); }
        }

        // «Стёрли половину, потом упали» — худший из возможных исходов, и при
        // удалении аккаунта он дороже, чем при «начать заново»: недостёртыми
        // остаются имя и статы, то есть ровно то, ради чего человек и нажал
        // кнопку. Одно чужое хранилище не имеет права до этого довести — ни
        // своей новелльной частью, ни общей.
        [Test]
        public void ПадениеХранилищаНеОставляетИгрокаЗабытымНаполовину()
        {
            FillTitle();
            LvnPlayerName.Set("Майя");
            PlayerPrefs.SetString("lvn_state___global", "{\"vars\":{\"karma\":3}}");
            LvnForget.Register("падучее целиком",
                _ => throw new System.InvalidOperationException("нарочно"),
                () => throw new System.InvalidOperationException("нарочно"));
            try
            {
                Assert.DoesNotThrow(() => LvnForget.All(new[] { T }, new[] { Hero }),
                    "упавшее хранилище уронило удаление аккаунта");

                Assert.AreEqual("", LvnPlayerName.Current, "имя пережило удаление аккаунта");
                Assert.AreEqual("", PlayerPrefs.GetString("lvn_state___global", ""),
                    "статы игрока пережили удаление аккаунта");
                Assert.AreEqual(0, LvnSaveStore.Slots(T).Count, "сейвы пережили удаление аккаунта");
            }
            finally { LvnForget.Register("падучее целиком", null, null); }
        }

        // ── чего обряды НЕ трогают ──────────────────────────────────────────

        // Наряд куплен за деньги и принадлежит ИГРОКУ, а не экспедиции — как и
        // кросс-новелльные статы рядом. «Начать заново» одной новеллы,
        // раздевающее героиню, читается как отнятая покупка, и вернуть её
        // будет нечем.
        [Test]
        public void ГардеробПереживаетНачатьЗаново()
        {
            LvnWardrobe.Equip(Hero, "hair", "long");

            LvnForget.Title(T);

            Assert.AreEqual(1, LvnWardrobe.Equipped(Hero).Count,
                "«начать заново» раздело героиню — купленный наряд пропал вместе с экспедицией");
        }

        // Личное — это то, что игра помнит об ИГРОКЕ, а не то, как он её
        // настроил. Громкость, язык и размер текста он выставил под себя и
        // своё устройство; снеси их вместе с аккаунтом — и человек, начавший
        // заново, получит чужие настройки: не тот язык на первом же экране и
        // музыку на полной громкости.
        [Test]
        public void НастройкиУдалениеАккаунтаПереживают()
        {
            float былаГромкость = LvnPrefs.VolMusic;
            float былМасштаб = LvnPrefs.TextScale;
            try
            {
                LvnPrefs.VolMusic = 0.31f;
                LvnPrefs.TextScale = 1.15f;

                LvnForget.All(new[] { T }, new[] { Hero });

                Assert.AreEqual(0.31f, LvnPrefs.VolMusic, 0.001f,
                    "удаление аккаунта сбросило громкость — это настройка устройства, а не память об игроке");
                Assert.AreEqual(1.15f, LvnPrefs.TextScale, 0.001f,
                    "удаление аккаунта сбросило размер текста — его выставляли под своё зрение, а не под аккаунт");
            }
            finally
            {
                LvnPrefs.VolMusic = былаГромкость;
                LvnPrefs.TextScale = былМасштаб;
            }
        }
    }
}
