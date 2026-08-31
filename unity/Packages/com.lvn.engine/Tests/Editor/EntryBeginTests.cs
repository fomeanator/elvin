using System.Collections.Generic;
using Lvn.Content;
using Lvn.UI;
using Lvn.UI.Screens;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ЧЕМ ЯВЛЯЕТСЯ ЗАХОД В НОВЕЛЛУ — <see cref="LvnProgress.BeginEntry"/>.
    ///
    /// <para>Игрок жмёт одну кнопку, а ответить надо на три вопроса сразу: с
    /// какой главы начинать, впервые ли он здесь (по этому первая глава заново
    /// спрашивает имя) и не оплачен ли этот вход уже. Ответы сплетены: второй
    /// считается по первому, третий — только когда первый сказал «продолжаем».
    /// Раньше они стояли прямо в теле игрового цикла, и порядок между ними
    /// держался комментариями.</para>
    ///
    /// <para>Цена ошибки в каждом — своя и вся видимая: «продолжить» уводит в
    /// начало, игру не спрашивают имя (или спрашивают на середине прохождения),
    /// с игрока берут энергию за главу, из которой он вышел минуту назад.</para>
    /// </summary>
    public sealed class EntryBeginTests
    {
        private const string Id = "t_entry_begin";

        private static LvnTitle Title(params (string id, int number, string script)[] chapters)
        {
            var list = new List<LvnChapter>();
            foreach (var (id, number, script) in chapters)
                list.Add(new LvnChapter { id = id, number = number, script_url = script });
            return new LvnTitle { id = Id, seasons = new List<LvnSeason> { new LvnSeason { chapters = list } } };
        }

        private static LvnTitle Three() => Title(
            ("c1", 1, "/content/t/ch1.lvn"),
            ("c2", 2, "/content/t/ch2.lvn"),
            ("c3", 3, "/content/t/ch3.lvn"));

        private static LvnChapter Гл(LvnTitle t, int i) => t.ChaptersOf()[i];

        /// <summary>Автосейв входа — тот самый, по которому и решают, платили
        /// ли за эту главу. Пишется на входе в неё; <paramref name="finished"/>
        /// значит «глава была дочитана до конца».</summary>
        private static void Автосейв(string scriptUrl, bool finished = false)
            => LvnSaveStore.Put(Id, LvnSaveStore.AutoSlot, new LvnSaveSlot
            {
                Snap = new LvnPlayer.LvnSnapshot { ScriptUrl = scriptUrl, Finished = finished },
                ChapterId = "—",
            });

        [SetUp]
        [TearDown]
        public void Clean()
        {
            LvnProgress.ResetTitle(Id);
            LvnSaveStore.DeleteAll(Id);
        }

        // ── с какой главы начинать ──────────────────────────────────────────

        // Звонящий называет главу «по умолчанию» — ту, что показывает карточка,
        // обычно первую. Позиция игрока принадлежит ИГРОКУ и сильнее любого
        // умолчания: иначе «Продолжить» на седьмой главе начинает новеллу
        // сначала, а прохождение семи глав пропадает у него на глазах.
        [Test]
        public void ТочкаПродолженияСильнееНазваннойЗвонящимГлавы()
        {
            var t = Three();
            LvnProgress.StartChapter(t, Гл(t, 1));

            Assert.AreEqual("c2", LvnProgress.BeginEntry(t, Гл(t, 0)).Chapter.id,
                "заход поехал в главу, названную кнопкой, а не туда, где стоит игрок");
            Assert.AreEqual("c2", LvnProgress.BeginEntry(t, null).Chapter.id,
                "звонящий вправе не знать главы — точка продолжения знает её сама");
        }

        // Продолжать нечего — идём туда, куда позвали. Иначе кнопка «Играть» на
        // непочатой новелле не ведёт никуда.
        [Test]
        public void БезТочкиПродолженияЕдемВНазваннуюГлаву()
        {
            var t = Three();
            Assert.AreEqual("c1", LvnProgress.BeginEntry(t, Гл(t, 0)).Chapter.id);
        }

        // ── чистый лист ─────────────────────────────────────────────────────

        // «Чистый лист» — это повод спросить имя игрока, и считается он ДО
        // того, как заход где-либо отметится. Отметь сначала — и сам заход
        // сотрёт свой признак: имя не спросят никогда, а героиню всю новеллу
        // будут звать умолчанием.
        [Test]
        public void ЧистыйЛистСчитаетсяДоЛюбойЗаписиТочки()
        {
            var t = Three();

            Assert.IsTrue(LvnProgress.BeginEntry(t, Гл(t, 0)).NovelFreshStart,
                "первый в жизни заход не признан чистым листом — имя игрока не спросят");
            Assert.IsTrue(LvnProgress.BeginEntry(t, Гл(t, 0)).NovelFreshStart,
                "заход отметил сам себя: второй вопрос про тот же чистый лист получил другой ответ");
            Assert.IsNull(LvnProgress.Current(t),
                "начало захода уже подвинуло точку продолжения — отметить его вправе только игровой цикл");
        }

        // А продолжение — не чистый лист: спросить имя на середине прохождения
        // значит переспросить то, на что игрок уже отвечал, и переименовать
        // героиню посреди истории.
        [Test]
        public void ПродолжениеЧистымЛистомНеСчитается()
        {
            var t = Three();
            LvnProgress.StartChapter(t, Гл(t, 1));

            Assert.IsFalse(LvnProgress.BeginEntry(t, Гл(t, 0)).NovelFreshStart,
                "возврат в свою же главу выдан за первый заход — имя переспросят посреди истории");
        }

        // ── пройденная новелла ──────────────────────────────────────────────

        // Финал снимает точку продолжения, но ПЕРЕМЕННЫЕ новеллы всё ещё держат
        // всё прохождение. Пусти повтор как есть — первая глава начнётся с
        // итоговыми статами: гейты выборов откроются сразу, героиня «уже
        // знакома» со всеми, а игрок видит вторую серию вместо первой. Поэтому
        // повтор уходит через перезапуск: глава сядет на свой нетронутый
        // чекпойнт.
        [Test]
        public void ПройденнаяНовеллаУходитНаПерезапуск_АНепочатаяНет()
        {
            var t = Three();
            Assert.AreEqual("", LvnProgress.PendingRestart(Id),
                "непочатую новеллу отправили на перезапуск — перезапускать в ней нечего");
            LvnProgress.BeginEntry(t, Гл(t, 0));
            Assert.AreEqual("", LvnProgress.PendingRestart(Id),
                "первый в жизни заход завёл одноразовый флаг, который потом придётся гасить");

            LvnProgress.StartChapter(t, Гл(t, 2));
            LvnProgress.FinishChapter(t, null);          // новелла дочитана
            var entry = LvnProgress.BeginEntry(t, Гл(t, 0));

            Assert.AreEqual("c1", entry.Chapter.id, "повтор пройденной новеллы начался не с первой главы");
            Assert.IsTrue(entry.NovelFreshStart, "повтор после финала не признан чистым листом");
            Assert.AreEqual("c1", LvnProgress.PendingRestart(Id),
                "повтор пойдёт с итоговыми статами прошлого прохождения — первая глава окажется сыграна наперёд");
        }

        // ── за этот вход уже платили? ───────────────────────────────────────

        // Вход в главу стоит энергии. Игрок вышел в меню и вернулся — платить
        // второй раз за ту же главу нельзя, и «уже входили» доказывает ЕЁ
        // автосейв: он пишется на входе. Без автосейва доказательства нет —
        // вход первый и платный.
        [Test]
        public void ВозвратВСвоюГлавуНеБерётПлатуВторойРаз()
        {
            var t = Three();
            LvnProgress.StartChapter(t, Гл(t, 1));

            Assert.IsFalse(LvnProgress.BeginEntry(t, Гл(t, 0)).AlreadyPaid,
                "вход объявлен оплаченным без единого доказательства");

            Автосейв("/content/t/ch2.lvn");
            Assert.IsTrue(LvnProgress.BeginEntry(t, Гл(t, 0)).AlreadyPaid,
                "с игрока возьмут энергию за главу, из которой он вышел минуту назад");
        }

        // САМАЯ ТОНКАЯ ИЗ ЧЕТЫРЁХ. Метка прогресса и автосейв говорят о разном:
        // конец главы двигает метку на СЛЕДУЮЩУЮ, за которую ещё не платили, а
        // автосейв остаётся от прежней. Спроси про оплату метку — и новая глава
        // откроется бесплатно после каждой дочитанной, то есть вся новелла
        // окажется бесплатной, кроме первой главы.
        [Test]
        public void ОплаченаТаГлаваЧейАвтосейв_АНеТаНаКоторойСтоитМетка()
        {
            var t = Three();
            LvnProgress.StartChapter(t, Гл(t, 1));
            Автосейв("/content/t/ch2.lvn");
            LvnProgress.FinishChapter(t, Гл(t, 2));   // вторая дочитана, метка уехала на третью

            Assert.AreEqual("c3", LvnProgress.BeginEntry(t, Гл(t, 0)).Chapter.id);
            Assert.IsFalse(LvnProgress.BeginEntry(t, Гл(t, 0)).AlreadyPaid,
                "автосейв прошлой главы оплатил вход в следующую — новелла раздаётся даром");
        }

        // Доигранный автосейв — свидетельство того, что глава ЗАКОНЧЕНА, а не
        // того, что вход в неё оплачен. Прими его — и финальная глава новеллы
        // открывалась бы бесплатно раз за разом: заходи, досматривай концовку,
        // выходи, повторяй.
        [Test]
        public void ДоигранныйАвтосейвОплаченнымВходомНеСчитается()
        {
            var t = Three();
            LvnProgress.StartChapter(t, Гл(t, 2));
            Автосейв("/content/t/ch3.lvn", finished: true);

            Assert.IsFalse(LvnProgress.BeginEntry(t, Гл(t, 0)).AlreadyPaid,
                "дочитанная глава открывается бесплатно сколько угодно раз");
        }

        // Оплата привязана к УДЕРЖИВАЕМОЙ позиции. После финала точки нет —
        // это новое прохождение, и автосейв прошлого его не оплачивает: иначе
        // повтор новеллы шёл бы даром по всем главам, чьи автосейвы уцелели.
        [Test]
        public void АвтосейвПрошлогоПрохожденияНовыйЗаходНеОплачивает()
        {
            var t = Three();
            LvnProgress.StartChapter(t, Гл(t, 2));
            LvnProgress.FinishChapter(t, null);
            Автосейв("/content/t/ch1.lvn");

            Assert.IsFalse(LvnProgress.BeginEntry(t, Гл(t, 0)).AlreadyPaid,
                "повтор пройденной новеллы объявлен оплаченным по автосейву прошлой жизни");
        }

        // Адрес одной и той же главы живёт двумя записями — буквами и
        // процентами; кириллица в именах файлов у нас правило, а не край.
        // Автосейв хранит запись ПРОШЛОЙ версии контента, а сравнивают её с
        // сегодняшним манифестом — то есть ровно там, где записи и расходятся.
        // Сравни строки напрямую — и с игрока возьмут плату второй раз за
        // главу, в которой он стоит, просто потому, что адрес набран иначе.
        [Test]
        public void АдресГлавыЗаписанныйИначеОстаётсяТойЖеГлавой()
        {
            var t = Title(("c1", 1, "/content/t/Глава1.lvn"), ("c2", 2, "/content/t/Глава2.lvn"));
            LvnProgress.StartChapter(t, Гл(t, 1));
            Автосейв("/content/t/%D0%93%D0%BB%D0%B0%D0%B2%D0%B02.lvn");

            Assert.IsTrue(LvnProgress.BeginEntry(t, Гл(t, 0)).AlreadyPaid,
                "та же глава, записанная процентами, объявлена чужой — за неё заплатят дважды");
        }

        // Автосейв ДРУГОЙ главы — не доказательство: игрок мог войти в неё из
        // списка глав, минуя ту, где стоял. Плата берётся за ту главу, в
        // которую заходят.
        [Test]
        public void АвтосейвЧужойГлавыВходНеОплачивает()
        {
            var t = Three();
            LvnProgress.StartChapter(t, Гл(t, 2));
            Автосейв("/content/t/ch1.lvn");

            Assert.IsFalse(LvnProgress.BeginEntry(t, Гл(t, 0)).AlreadyPaid,
                "автосейв посторонней главы оплатил вход в третью");
        }

        // ── неполные данные ─────────────────────────────────────────────────

        // Заход считают на старте игрового цикла, когда манифест мог не
        // доехать, а глава — не найтись. Ответ «не знаю» тут законен, падение —
        // нет: это чёрный экран вместо новеллы.
        [Test]
        public void НетНовеллыИлиГлавыЗаходНеРоняет()
        {
            LvnProgress.Entry пусто = default;
            Assert.DoesNotThrow(() => пусто = LvnProgress.BeginEntry(null, null),
                "заход без новеллы уронил игровой цикл вместо честного «не знаю»");
            Assert.IsNull(пусто.Chapter);
            Assert.IsTrue(пусто.NovelFreshStart, "начинать не с чего — значит с чистого листа");
            Assert.IsFalse(пусто.AlreadyPaid, "вход в никуда объявлен оплаченным");

            var t = Three();
            LvnProgress.StartChapter(t, Гл(t, 2));
            LvnProgress.FinishChapter(t, null);
            Assert.DoesNotThrow(() => LvnProgress.BeginEntry(t, null),
                "заход без главы уронил игровой цикл");
        }
    }
}
