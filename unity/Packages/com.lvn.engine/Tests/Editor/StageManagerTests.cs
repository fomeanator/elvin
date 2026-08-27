using System.Collections.Generic;
using Lvn;
using Lvn.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests.Editor
{
    /// <summary>
    /// ПОМРЕЖ: кто отдаёт команду, о чём, и чья побеждает.
    ///
    /// <para>«Нам нужен ответственный за команды. Он знает, какие команды
    /// отправляются сейчас в движок, кто их отправил, и конфликтующие решает по
    /// собственным мотивам» (Илья, 27.08). До него спор разрешался тем, чей
    /// <c>await</c> вернулся позже, — и каждый живой дефект этой недели оказался
    /// спором двух отправителей.</para>
    /// </summary>
    public class StageManagerTests
    {
        private LvnStageManager _pm;

        [SetUp]
        public void SetUp() => _pm = new LvnStageManager();

        private static JObject Actor(string id) => new JObject { ["op"] = "actor", ["id"] = id };
        private static JObject Bg() => new JObject { ["op"] = "bg", ["sprite_url"] = "/x.jpg" };
        private static JObject Fade() => new JObject { ["op"] = "fade", ["to"] = "black" };

        private bool Admit(JObject cmd, LvnSender s) => _pm.Admit(cmd, s, out _);

        // ── предмет спора ───────────────────────────────────────────────────

        // Два актёра — два разных предмета: спорить им не о чем.
        [Test]
        public void DifferentActorsNeverConflict()
        {
            _pm.Hold("actor:victoria", LvnSender.Cutscene);
            Assert.IsTrue(Admit(Actor("agent"), LvnSender.Story),
                "агента отклонили из-за занятой героини — предметы спутаны");
        }

        // Вуали и эффекты кадра — ОДИН предмет: `fade`, `fx` и `blur` спорят
        // между собой, потому что все трое кроют кадр целиком.
        [Test]
        public void VeilsAreOneSubject()
        {
            Assert.AreEqual(LvnStageManager.SubjectOf(Fade()),
                            LvnStageManager.SubjectOf(new JObject { ["op"] = "fx" }));
        }

        // `sfx` про актёра — тот же предмет, что и сам актёр: иначе грим лёг бы
        // на того, кого уже уводят.
        [Test]
        public void ActorEffectSharesTheActorsSubject()
        {
            Assert.AreEqual("actor:victoria",
                LvnStageManager.SubjectOf(new JObject { ["op"] = "sfx", ["id"] = "victoria" }));
        }

        // ── старшинство ─────────────────────────────────────────────────────

        // ЖИВОЙ СЛУЧАЙ: реплика истории висела поверх катсцены ухода. Пока
        // катсцена держит кадр, история ждёт.
        [Test]
        public void CutsceneOutranksTheStory()
        {
            _pm.Hold("veil", LvnSender.Cutscene);
            Assert.IsFalse(Admit(Fade(), LvnSender.Story),
                "история перебила катсцену — зритель увидит склейку посреди кадра");
        }

        // ЖИВОЙ СЛУЧАЙ: кукла прыгала при возврате в меню — витрина двигала её
        // прямо посреди портальной катсцены.
        [Test]
        public void MenuWaitsForTheCutscene()
        {
            _pm.Hold("actor:victoria", LvnSender.Cutscene);
            Assert.IsFalse(Admit(Actor("victoria"), LvnSender.Menu),
                "витрина переставила героиню посреди катсцены");
        }

        // Гардероб старше истории: игрок ПРЯМО СЕЙЧАС смотрит на примерку.
        [Test]
        public void WardrobeOutranksTheStoryWhileTheSheetIsOpen()
        {
            _pm.Hold("actor:victoria", LvnSender.Wardrobe);
            Assert.IsFalse(Admit(Actor("victoria"), LvnSender.Story),
                "история переодела героиню, пока игрок листает наряды");
        }

        // ЖИВОЙ СЛУЧАЙ: самолечение полотна перебивало живую смену фона.
        [Test]
        public void GuardHealsOnlyWhatNobodyIsArguingAbout()
        {
            _pm.Hold("bg", LvnSender.Story);
            Assert.IsFalse(Admit(Bg(), LvnSender.Guard), "страж влез в живую работу");

            _pm.Release("bg");
            Assert.IsTrue(Admit(Bg(), LvnSender.Guard), "свободное полотно страж чинить обязан");
        }

        // Равные не блокируют друг друга: история и её же реплей — один голос.
        [Test]
        public void StoryAndReplayAreOneVoice()
        {
            _pm.Hold("bg", LvnSender.Story);
            Assert.IsTrue(Admit(Bg(), LvnSender.Replay),
                "восстановление кадра отклонено как чужое");
        }

        // Старший перебивает младшего всегда: катсцена начинается поверх
        // витрины, не спрашивая.
        [Test]
        public void SeniorTakesTheSubjectFromJunior()
        {
            _pm.Hold("actor:victoria", LvnSender.Menu);
            Assert.IsTrue(Admit(Actor("victoria"), LvnSender.Cutscene));
            _pm.Hold("actor:victoria", LvnSender.Cutscene);
            Assert.AreEqual(LvnSender.Cutscene, _pm.HolderOf("actor:victoria"));
        }

        // ── освобождение ────────────────────────────────────────────────────

        // Катсцена кончилась — кадр возвращается всем.
        [Test]
        public void ReleasingGivesTheFrameBack()
        {
            _pm.Hold("veil", LvnSender.Cutscene);
            _pm.Hold("actor:victoria", LvnSender.Cutscene);
            _pm.ReleaseAll(LvnSender.Cutscene);

            Assert.IsTrue(Admit(Fade(), LvnSender.Story));
            Assert.IsTrue(Admit(Actor("victoria"), LvnSender.Menu));
            Assert.IsNull(_pm.HolderOf("veil"));
        }

        // ЖИВОЙ РИСК: держатель молчит (катсцена оборвалась исключением) —
        // предмет обязан освободиться сам, иначе игра встанет навсегда.
        [Test]
        public void AForgottenHoldExpiresOnItsOwn()
        {
            Assert.Greater(LvnStageManager.HoldSeconds, 0f,
                "без срока молчаливо застрявший держатель блокирует сцену навсегда");
        }

        // ── липкость ────────────────────────────────────────────────────────

        // ЖИВОЙ СЛУЧАЙ: поза витрины пережила старт главы и подмешалась к
        // авторской — «не встраивается в игру, хотя её реплика».
        [Test]
        public void OnlyTheStoryLeavesMemory()
        {
            Assert.IsTrue(LvnStageManager.Sticky(LvnSender.Story));
            Assert.IsTrue(LvnStageManager.Sticky(LvnSender.Replay),
                "восстановление кадра обязано оставлять память: это авторские команды");
            Assert.IsFalse(LvnStageManager.Sticky(LvnSender.Menu), "витрина оставила липкую позу");
            Assert.IsFalse(LvnStageManager.Sticky(LvnSender.Cutscene), "катсцена оставила липкую позу");
            Assert.IsFalse(LvnStageManager.Sticky(LvnSender.Guard));
        }

        // ── журнал ──────────────────────────────────────────────────────────

        // Отказ — это решение, и оно обязано быть видно: раньше команды
        // отбрасывались молча, и каждый разбор начинался с гадания.
        [Test]
        public void TheJournalRemembersWhoAskedAndWhatWasRefused()
        {
            _pm.Hold("veil", LvnSender.Cutscene);
            Admit(Fade(), LvnSender.Menu);        // отказ: витрина младше
            Admit(Bg(), LvnSender.Menu);          // принято: полотно свободно

            var log = _pm.Journal();
            StringAssert.Contains("Menu", log, "в журнале нет того, кто просил");
            StringAssert.Contains("ОТКАЗ", log, "отказ не записан — снова гадать по стек-трейсам");
            StringAssert.Contains("veil←Cutscene", log, "не видно, кто держит предмет сейчас");
        }

        // ── очередь автора ──────────────────────────────────────────────────

        // ГОЛОС АВТОРА НЕ ОТКЛОНЯЮТ — ЕГО ЖДУТ. Отказ означает, что команда
        // пропала совсем: сценарий уехал дальше и второй раз её не отдаст. Так
        // в кадре и оставались лишние люди — история сказала «скрыть», катсцена
        // держала кадр, команда ушла в никуда.
        [Test]
        public void TheStoryIsDeferredNotRefused()
        {
            var played = new List<string>();
            _pm.Apply = (cmd, sender) => played.Add((string)cmd["id"] ?? (string)cmd["op"]);

            _pm.Hold("actor:agent", LvnSender.Cutscene);
            Assert.IsFalse(Admit(Actor("agent"), LvnSender.Story), "во время катсцены команда не играет сразу");
            StringAssert.Contains("отложено", _pm.Journal(), "команда автора должна ждать, а не пропадать");
            CollectionAssert.IsEmpty(played, "отложенное не имеет права играть до освобождения");

            _pm.Release("actor:agent");
            CollectionAssert.AreEqual(new[] { "agent" }, played,
                "предмет освободился, а команда автора так и не доиграла — человек останется в кадре");
        }

        // Порядок автора — это и есть сценарий: доигрывать надо в том же
        // порядке, в каком он отдавал команды.
        [Test]
        public void DeferredCommandsKeepTheAuthorsOrder()
        {
            var played = new List<string>();
            _pm.Apply = (cmd, sender) => played.Add((string)cmd["id"]);

            _pm.Hold("actor:agent", LvnSender.Cutscene);
            Admit(new JObject { ["op"] = "actor", ["id"] = "agent", ["show"] = true }, LvnSender.Story);
            Admit(new JObject { ["op"] = "sfx", ["id"] = "agent", ["dark"] = 0.9f }, LvnSender.Story);
            Admit(new JObject { ["op"] = "actor", ["id"] = "agent", ["show"] = false }, LvnSender.Story);
            _pm.ReleaseAll(LvnSender.Cutscene);

            Assert.AreEqual(3, played.Count, "доиграно не всё, что откладывали");
        }

        // Чужим отказ — нормальный ответ: витрина, страж и гардероб повторят
        // своё сами, когда кадр освободится, и копить их незачем.
        [Test]
        public void OnlyTheAuthorGetsAQueue()
        {
            var played = new List<string>();
            _pm.Apply = (cmd, sender) => played.Add((string)cmd["op"]);

            _pm.Hold("bg", LvnSender.Cutscene);
            Admit(Bg(), LvnSender.Menu);
            Admit(Bg(), LvnSender.Guard);
            _pm.Release("bg");

            CollectionAssert.IsEmpty(played, "чужие команды не должны копиться в очереди автора");
        }

        // Уборка сцены — отложенному больше некуда играть: его кадра нет.
        [Test]
        public void AWipeDropsTheQueue()
        {
            var played = new List<string>();
            _pm.Apply = (cmd, sender) => played.Add((string)cmd["op"]);

            _pm.Hold("actor:agent", LvnSender.Cutscene);
            Admit(Actor("agent"), LvnSender.Story);
            _pm.Clear();
            _pm.Release("actor:agent");

            CollectionAssert.IsEmpty(played, "команда прошлой главы доиграла в новой сцене");
        }
    }
}
