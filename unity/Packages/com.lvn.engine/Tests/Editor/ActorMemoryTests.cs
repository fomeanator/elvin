using System.Linq;
using Lvn;
using Lvn.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ЧТО СЦЕНА ПОМНИТ О ФИГУРЕ — правила одной записи вместо пяти словарей.
    ///
    /// <para>Пять словарей жили по одному ключу, разъехавшись по девяти файлам
    /// сцены, и меняться были обязаны ВМЕСТЕ: показал — записал в три, убрал —
    /// вычистил из четырёх. Держалось правило на памяти того, кто правит, а
    /// цена ошибки тихая: забытая запись ничего не роняет, она делает фигуру
    /// НЕМНОГО не той — та встаёт по старому месту, наследует чужую позу,
    /// пересобирается там, где могла бы просто показаться.</para>
    ///
    /// <para>Здесь закреплены ровно те правила, ради которых пятерых сводили в
    /// одного: каждое когда-то было живым дефектом.</para>
    /// </summary>
    public class ActorMemoryTests
    {
        private LvnActorMemory _mem;

        [SetUp]
        public void SetUp() => _mem = new LvnActorMemory();

        private static JObject Actor(string id) => new JObject { ["op"] = "actor", ["id"] = id };

        /// Всё, что сцена вообще может знать о фигуре, — одним вызовом. Тест на
        /// «забыли целиком» обязан спрашивать про ПЯТЬ записей и про перечни:
        /// проверить четыре — значит повторить исходный дефект.
        private void AssertЗабытаЦеликом(string id, string когда)
        {
            Assert.IsFalse(_mem.TryCommand(id, out _), когда + ": осталась команда");
            Assert.IsFalse(_mem.Knows(id), когда + ": сцена всё ещё берётся пересобрать фигуру");
            Assert.IsFalse(_mem.TryTarget(id, out _), когда + ": осталась цель — куда просили встать");
            Assert.IsFalse(_mem.TryPoseSender(id, out _), когда + ": остался отправитель позы");
            Assert.IsFalse(_mem.TryWhere(id, out _), когда + ": осталось место");
            Assert.IsFalse(_mem.HasWhere(id), когда + ": осталось место");
            Assert.IsFalse(_mem.TryLook(id, out _), когда + ": остался облик");
            CollectionAssert.DoesNotContain(_mem.Ids().ToList(), id, когда + ": фигура осталась в переписи");
            CollectionAssert.DoesNotContain(_mem.Wheres().Select(kv => kv.Key).ToList(), id,
                когда + ": фигура осталась в перечне мест — арбитр слотов расталкивает призрака");
            CollectionAssert.DoesNotContain(_mem.Targets().Select(kv => kv.Key).ToList(), id,
                когда + ": фигура осталась в перечне целей — диалог даст имя со стороны призрака");
        }

        /// Полная память об одном человеке: команда с подписью, цель, место и
        /// облик — все пять записей сразу.
        private void ПомнитьВсё(string id, float x, string look, LvnSender sender = LvnSender.Story)
        {
            _mem.Remember(id, Actor(id), sender);
            _mem.SetTarget(id, Placement.Standing(x));
            _mem.SetWhere(id, Placement.Standing(x));
            _mem.SetLook(id, look);
        }

        // ── забыть фигуру — ОДНО действие ───────────────────────────────────

        // Ровно тот дефект, ради которого пятерых сводили: забывали из четырёх
        // словарей, пятый оставлял тень человека.
        [Test]
        public void Forget_УноситВсёПятьЗаписейСразу()
        {
            ПомнитьВсё("victoria", 0.5f, "dress=gala");

            _mem.Forget("victoria");

            AssertЗабытаЦеликом("victoria", "забыли фигуру");
        }

        // Тень человека узнаётся не пустотой сразу после забвения, а тем, что
        // СЛЕДУЮЩИЙ показ той же роли наследует прошлую жизнь: встаёт по
        // старому месту и считается уже одетым в наряд, которого на новой
        // фигуре нет.
        [Test]
        public void Forget_СледующийПоказНеНаследуетПрошлуюЖизнь()
        {
            ПомнитьВсё("agent", 0.2f, "coat=old");
            _mem.Forget("agent");

            _mem.Remember("agent", Actor("agent"), LvnSender.Story);

            Assert.IsFalse(_mem.TryWhere("agent", out _), "новый показ встал по месту прошлой жизни");
            Assert.IsFalse(_mem.TryTarget("agent", out _), "новому показу досталась старая цель");
            Assert.IsFalse(_mem.TryLook("agent", out _), "на новой фигуре «уже надет» облик прошлой");
        }

        // ── уборка сцены: память ГЛАВЫ уходит, облик остаётся ───────────────

        // Поза липкая. Команда, которой куклу ставило МЕНЮ (центр, рост
        // витрины), переживала старт главы и подмешивалась к авторской —
        // героиня выходила в сцену стоящей по-менюшному. Уносится и подпись:
        // правило «авторская команда не наследует позу витрины» смотрит именно
        // на отправителя, и оставить его — значит оставить дефект.
        [Test]
        public void ForgetPoses_УноситКомандуЦельМестоИОтправителя()
        {
            ПомнитьВсё("victoria", 0.5f, "dress=gala", LvnSender.Menu);

            _mem.ForgetPoses();

            Assert.IsFalse(_mem.TryCommand("victoria", out _), "команда витрины пережила старт главы");
            Assert.IsFalse(_mem.Knows("victoria"), "сцена берётся пересобрать героиню по команде прошлой главы");
            Assert.IsFalse(_mem.TryPoseSender("victoria", out _),
                "отправитель позы остался — авторская команда унаследует позу витрины");
            Assert.IsFalse(_mem.TryTarget("victoria", out _), "цель прошлой главы пережила уборку");
            Assert.IsFalse(_mem.TryWhere("victoria", out _), "место прошлой главы пережило уборку");
        }

        // А облик — свойство самой ФИГУРЫ, а не договора истории с собой:
        // героиня переживает уборку живой, слои на ней уже надеты. Стереть эту
        // запись значило заставить её собираться заново на выходе из главы.
        [Test]
        public void ForgetPoses_ОбликПереживаетУборку()
        {
            ПомнитьВсё("victoria", 0.5f, "dress=gala");

            _mem.ForgetPoses();

            Assert.IsTrue(_mem.TryLook("victoria", out var look),
                "уборка сняла с героини наряд — на выходе из главы она пересоберётся заново");
            Assert.AreEqual("dress=gala", look, "уборка подменила наряд");
        }

        // Уборка — это уборка: та, о ком помнили ТОЛЬКО позу, уходит из
        // переписи целиком, а не остаётся пустой записью-призраком. А та, на
        // ком есть надетое, остаётся в переписи вместе с ним.
        [Test]
        public void ForgetPoses_БезОблика_ФигураУходитИзПереписи_СОбликом_Остаётся()
        {
            ПомнитьВсё("victoria", 0.5f, "dress=gala"); // живая героиня: наряд надет
            ПомнитьВсё("agent", 0.2f, null);            // статист: о нём знали только позу

            _mem.ForgetPoses();

            var перепись = _mem.Ids().ToList();
            CollectionAssert.DoesNotContain(перепись, "agent",
                "статист без облика остался пустой записью — тень человека в переписи");
            CollectionAssert.Contains(перепись, "victoria",
                "героиню с надетым обликом вычистили вместе со статистами");
            AssertЗабытаЦеликом("agent", "уборка сцены");
        }

        // ── вернуть команду, не переписывая, КТО её отдал ───────────────────

        // Примерка прячет фигуру и возвращает её ПРЕЖНЕЙ командой — но ставил
        // её по-прежнему автор. Правило «авторская команда не наследует позу
        // витрины» смотрит на отправителя: перепиши его на гардероб — и после
        // закрытия листа героиня останется стоять по-примерочному.
        [Test]
        public void RestoreCommand_НеТрогаетОтправителяПозы()
        {
            _mem.Remember("victoria", Actor("victoria"), LvnSender.Story);

            var прежняя = Actor("victoria");
            прежняя["position"] = "left";
            _mem.RestoreCommand("victoria", прежняя);

            Assert.IsTrue(_mem.TryPoseSender("victoria", out var кто), "возврат команды стёр отправителя позы");
            Assert.AreEqual(LvnSender.Story, кто, "возврат команды переписал отправителя позы на себя");
            Assert.IsTrue(_mem.TryCommand("victoria", out var cmd), "команда не вернулась вовсе");
            Assert.AreEqual("left", (string)cmd["position"], "вернулась не та команда");
        }

        // Разные глаголы не случайно: ПОКАЗ подписывает позу заново. Витрина,
        // поставившая куклу, обязана остаться в подписи — иначе спор
        // отправителей разрешится в пользу того, кто уже ушёл.
        [Test]
        public void Remember_ОтправителяПозыМеняет()
        {
            _mem.Remember("victoria", Actor("victoria"), LvnSender.Story);

            _mem.Remember("victoria", Actor("victoria"), LvnSender.Menu);

            Assert.IsTrue(_mem.TryPoseSender("victoria", out var кто));
            Assert.AreEqual(LvnSender.Menu, кто, "показ витрины не переподписал позу — подпись осталась авторской");
        }

        // ── уборка «остаётся одна» ──────────────────────────────────────────

        // Героиня уходит на миссию и возвращается ТОЙ ЖЕ куклой: место и наряд,
        // с которыми кончилась глава, обязаны пережить переход. Иначе она
        // пересобирается из умолчаний — живой репорт «открыл гардероб, нажал
        // полный рост, вернулся — ГГ по центру».
        [Test]
        public void ForgetAllExcept_ОставляетОдного_СоВсемЧтоОНёмПомнили()
        {
            ПомнитьВсё("victoria", 0.5f, "dress=gala");
            ПомнитьВсё("agent", 0.2f, "coat=grey");
            ПомнитьВсё("waiter", 0.8f, "apron=white");

            _mem.ForgetAllExcept("victoria");

            CollectionAssert.AreEquivalent(new[] { "victoria" }, _mem.Ids().ToList(),
                "после уборки на сцене остался не один");
            Assert.IsTrue(_mem.TryWhere("victoria", out var место), "оставшаяся потеряла своё место");
            Assert.AreEqual(0.5f, место.X, 1e-6f, "оставшаяся встала не туда, где стояла");
            Assert.IsTrue(_mem.TryLook("victoria", out var look), "оставшаяся потеряла наряд — соберётся из умолчаний");
            Assert.AreEqual("dress=gala", look, "по обе стороны перехода это должна быть ОДНА кукла");
            Assert.IsTrue(_mem.Knows("victoria"), "оставшуюся стало нечем пересобрать");
            Assert.IsTrue(_mem.TryTarget("victoria", out _), "оставшаяся потеряла цель");
            Assert.IsTrue(_mem.TryPoseSender("victoria", out _), "оставшаяся потеряла подпись под позой");

            AssertЗабытаЦеликом("agent", "убрали всех, кроме героини");
            AssertЗабытаЦеликом("waiter", "убрали всех, кроме героини");
        }

        // Уборка, которой некого оставлять (никто не назван), обязана оставить
        // ЧИСТУЮ сцену, а не половину: половина — это и есть тень человека,
        // ради которой всё сводилось.
        [Test]
        public void ForgetAllExcept_НекогоОставлять_СценаЧистая()
        {
            ПомнитьВсё("victoria", 0.5f, "dress=gala");
            ПомнитьВсё("agent", 0.2f, "coat=grey");

            _mem.ForgetAllExcept(null);

            CollectionAssert.IsEmpty(_mem.Ids().ToList(), "уборка без имени оставила половину сцены");
            AssertЗабытаЦеликом("victoria", "уборка без имени");
        }

        // ── перечни отдают только ИЗВЕСТНОЕ ─────────────────────────────────

        // По Wheres() арбитр решает, не встали ли двое друг в друга. Запись, о
        // которой известен только облик, — не фигура на сцене: попади она в
        // перечень с местом по умолчанию (0,0, скрыта), арбитр начнёт
        // расталкивать несуществующего.
        [Test]
        public void Wheres_ОтдаётТолькоТех_КомуМестоЗадавали()
        {
            _mem.SetWhere("victoria", Placement.Standing(0.5f));
            _mem.SetLook("ghost", "dress=gala");                // о нём знают ТОЛЬКО облик
            _mem.SetTarget("flying", Placement.Standing(0.2f));  // летит в кадр, но ещё не встал

            CollectionAssert.AreEquivalent(new[] { "victoria" }, _mem.Wheres().Select(kv => kv.Key).ToList(),
                "в перечень мест попал тот, чьего места сцена не знает");
        }

        // По Targets() диалог выбирает сторону для имени говорящего ДО того,
        // как доедет арт. Пустая запись здесь поставила бы имя со стороны того,
        // кого в кадре нет.
        [Test]
        public void Targets_ОтдаётТолькоТех_КомуЦельСтавили()
        {
            _mem.SetTarget("flying", Placement.Standing(0.2f));
            _mem.SetLook("ghost", "dress=gala");                // о нём знают ТОЛЬКО облик
            _mem.SetWhere("victoria", Placement.Standing(0.5f)); // стоит, но цели ему не ставили

            CollectionAssert.AreEquivalent(new[] { "flying" }, _mem.Targets().Select(kv => kv.Key).ToList(),
                "в перечень целей попал тот, кому встать никто не просил");
        }

        // Место снимается — ЧЕЛОВЕК остаётся: сцена всё ещё знает, чем его
        // пересобрать и во что он одет. Иначе снятие места было бы забвением.
        [Test]
        public void DropWhere_СнимаетМесто_НоНеФигуру()
        {
            ПомнитьВсё("victoria", 0.5f, "dress=gala");

            _mem.DropWhere("victoria");

            Assert.IsFalse(_mem.HasWhere("victoria"), "место осталось известным");
            Assert.IsFalse(_mem.TryWhere("victoria", out _), "место осталось известным");
            CollectionAssert.DoesNotContain(_mem.Wheres().Select(kv => kv.Key).ToList(), "victoria",
                "снятое место всё ещё раздаётся арбитру слотов");
            Assert.IsTrue(_mem.Knows("victoria"), "вместе с местом ушла и команда");
            Assert.IsTrue(_mem.TryLook("victoria", out _), "вместе с местом ушёл и наряд");
            Assert.IsTrue(_mem.TryTarget("victoria", out _), "вместе с местом ушла и цель");
        }

        // Смена фона снимает одежду со всех разом — но не самих людей: сцена
        // после неё обязана знать, кого и чем пересобирать.
        [Test]
        public void ForgetLooks_УноситОдеждуСоВсех_НоНеЛюдей()
        {
            ПомнитьВсё("victoria", 0.5f, "dress=gala");
            ПомнитьВсё("agent", 0.2f, "coat=grey");

            _mem.ForgetLooks();

            Assert.IsFalse(_mem.TryLook("victoria", out _), "наряд героини пережил смену фона");
            Assert.IsFalse(_mem.TryLook("agent", out _), "наряд статиста пережил смену фона");
            CollectionAssert.AreEquivalent(new[] { "victoria", "agent" }, _mem.Ids().ToList(),
                "вместе с одеждой забыли и самих людей");
            Assert.IsTrue(_mem.Knows("victoria"), "вместе с одеждой ушла команда — героиню нечем пересобрать");
            Assert.IsTrue(_mem.TryWhere("agent", out _), "вместе с одеждой ушло и место");
        }

        // ── краевые случаи: память зовут и на пустом месте ──────────────────

        // id приходит из команды автора и от хоста — пустым он бывает. Память
        // не имеет права ни падать на нём, ни заводить БЕЗЫМЯННОГО призрака:
        // такой призрак попал бы в перечень мест к арбитру слотов.
        [Test]
        public void ПустойИНулевойId_НеПадаютИНеЗаводятПризрака()
        {
            foreach (var id in new[] { null, "" })
            {
                var кто = id;
                Assert.DoesNotThrow(() =>
                {
                    _mem.Remember(кто, Actor("x"), LvnSender.Story);
                    _mem.RestoreCommand(кто, Actor("x"));
                    _mem.SetTarget(кто, Placement.Standing(0.5f));
                    _mem.SetWhere(кто, Placement.Standing(0.5f));
                    _mem.SetLook(кто, "dress=gala");
                    _mem.DropWhere(кто);
                    _mem.DropLook(кто);
                    _mem.Forget(кто);
                    _mem.TryCommand(кто, out _);
                    _mem.TryTarget(кто, out _);
                    _mem.TryWhere(кто, out _);
                    _mem.TryPoseSender(кто, out _);
                    _mem.TryLook(кто, out _);
                    _mem.Knows(кто);
                    _mem.HasWhere(кто);
                }, "безымянный id уронил память сцены");
            }

            CollectionAssert.IsEmpty(_mem.Ids().ToList(), "безымянная запись попала в перепись");
            CollectionAssert.IsEmpty(_mem.Wheres().ToList(), "безымянная запись попала в перечень мест");
            CollectionAssert.IsEmpty(_mem.Targets().ToList(), "безымянная запись попала в перечень целей");
        }

        // О незнакомой фигуре спрашивают на каждом кадре — из диалога, с
        // арбитража, из стража здоровья. Пустой ответ обязан быть ОТВЕТОМ, и
        // сам вопрос не имеет права заводить о ней запись.
        [Test]
        public void ЧтениеОНезнакомойФигуре_ПустоИБезЗаписи()
        {
            Assert.IsFalse(_mem.TryCommand("никто", out var cmd));
            Assert.IsNull(cmd, "о незнакомце выдали команду");
            Assert.IsFalse(_mem.Knows("никто"));
            Assert.IsFalse(_mem.TryTarget("никто", out _));
            Assert.IsFalse(_mem.TryWhere("никто", out _));
            Assert.IsFalse(_mem.HasWhere("никто"));
            Assert.IsFalse(_mem.TryPoseSender("никто", out _));
            Assert.IsFalse(_mem.TryLook("никто", out var look));
            Assert.IsNull(look, "о незнакомце выдали наряд");

            CollectionAssert.IsEmpty(_mem.Ids().ToList(), "вопрос о незнакомце завёл о нём запись");
        }

        // Забвение приходит по нескольким путям сразу (ушёл сам, убрали
        // уборкой, закрылся лист гардероба) — второй заход обязан быть тихим.
        [Test]
        public void ПовторноеЗабвение_И_УборкаПустойСцены_Тихи()
        {
            ПомнитьВсё("victoria", 0.5f, "dress=gala");
            _mem.Forget("victoria");

            Assert.DoesNotThrow(() =>
            {
                _mem.Forget("victoria");
                _mem.Forget("никто");
                _mem.DropWhere("никто");
                _mem.DropLook("никто");
                _mem.ForgetPoses();
                _mem.ForgetLooks();
                _mem.ForgetAllExcept("victoria");
            }, "второй заход забвения уронил сцену");

            AssertЗабытаЦеликом("victoria", "повторное забвение");
        }

        // Снять то, чего не было, — обычное дело: страж роняет наряд неполной
        // фигуре, перетаскивание снимает место у той, кого не двигали. Такое
        // снятие обязано быть безобидным, а не забирать соседнюю запись.
        [Test]
        public void СнятиеТого_ЧегоНеБыло_НеТрогаетОстального()
        {
            _mem.Remember("agent", Actor("agent"), LvnSender.Story);
            _mem.SetLook("agent", "coat=grey");

            _mem.DropWhere("agent"); // места ему не задавали

            Assert.IsTrue(_mem.Knows("agent"), "снятие несуществующего места забрало команду");
            Assert.IsTrue(_mem.TryLook("agent", out _), "снятие несуществующего места забрало наряд");

            _mem.DropLook("agent");
            _mem.DropLook("agent"); // второй раз — облика уже нет

            Assert.IsFalse(_mem.TryLook("agent", out _), "наряд не снялся");
            Assert.IsTrue(_mem.Knows("agent"), "снятие наряда забрало команду");
            Assert.IsTrue(_mem.TryPoseSender("agent", out _), "снятие наряда забрало подпись под позой");
        }
    }
}
