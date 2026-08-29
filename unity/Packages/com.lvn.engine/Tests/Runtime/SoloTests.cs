using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Lvn.Tests.Runtime
{
    /// <summary>
    /// РАСПОРЯДИТЕЛЬ СЦЕНЫ: три отказа, которые видели глазами и чинили
    /// догадками.
    ///
    /// <para>«Расталкивание работает херово: иногда скрывает агента на
    /// несколько ходов, а иногда героиню на нём рисуют» (Илья, 27.08). Оба
    /// симптома — одна причина: убирать умели все, а возвращать и ждать
    /// летящего не умел никто.</para>
    /// </summary>
    public class SoloTests
    {
        /// <summary>
        /// АРТ У КУКЛЫ ДОЛЖЕН БЫТЬ — иначе её вообще не выпустят в кадр.
        ///
        /// <para>Сцена НАМЕРЕННО не показывает актёра, которому нечем рисовать
        /// (<c>CanvasSceneRenderer.ApplyActor</c>): пустой <c>Image</c>
        /// заливает себя сплошняком, и это тот самый белый прямоугольник.
        /// Стенд без единого спрайта получал слот, созданный впрок, и
        /// НИКОГДА — живую куклу: проверки про «пережила уборку тем же
        /// объектом» смотрели на выключенный объект и не находили ничего.</para>
        /// </summary>
        private sealed class OneSpriteAssets : ILvnAssets
        {
            private Sprite _sprite;
            public Sprite Sprite => _sprite ??= Sprite.Create(
                new Texture2D(4, 4), new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f));

            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct)
                => Task.FromResult(Sprite);
            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct)
                => Task.FromResult<AudioClip>(null);
            public Task PreloadAsync(IReadOnlyList<string> urls, string kind, CancellationToken ct)
                => Task.CompletedTask;
            public void Unload(string url) { }
            public void UnloadAll() { }
        }

        private GameObject _go;
        private VnStage _stage;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("solo-stage", typeof(UIDocument));
            var doc = _go.GetComponent<UIDocument>();
            doc.panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            _stage = _go.AddComponent<VnStage>();
            _stage.Assets = new OneSpriteAssets();
        }

        [TearDown]
        public void TearDown() => Object.Destroy(_go);

        private static JObject Show(string id) => new JObject
        {
            ["op"] = "actor", ["id"] = id, ["show"] = true, ["position"] = "center",
            // Слой у каждого свой — иначе «облик тот же» спутает двух кукол.
            ["sprite_url"] = "test/" + id + ".png",
        };

        /// <summary>
        /// КУКЛА ПО ИМЕНИ — через дерево сцены, а не <c>GameObject.Find</c>.
        ///
        /// <para><c>GameObject.Find</c> видит только ВКЛЮЧЁННЫЕ объекты, а
        /// половина здешних вопросов ровно про выключенных: «пережила уборку
        /// живой, но ушла из кадра». Искать надо там, где кукла живёт, —
        /// в содержимом канваса ЭТОЙ сцены, а не по всей сцене теста (чужой
        /// стенд из соседнего теста ещё не снесён: <c>Destroy</c>
        /// отложенный).</para>
        /// </summary>
        private GameObject Doll(string id)
        {
            var content = _go != null
                ? _go.transform.Find("vn-world-canvas/game-root/content") : null;
            var t = content != null ? content.Find("vn-obj-" + id) : null;
            return t != null ? t.gameObject : null;
        }

        private IEnumerator Staged(params string[] ids)
        {
            _stage.Play(@"{""script"":[{""op"":""say"",""text"":""кадр""}]}");
            yield return null;
            foreach (var id in ids) _stage.ApplyStage(Show(id));
            yield return null;
        }

        // Ровно тот отказ: агента увели ради катсцены, и он пропал до конца
        // сцены — история про уход не знает и ставить заново не собирается.
        [UnityTest]
        public IEnumerator AsideActorsComeBackWithTheirOwnCommand()
        {
            yield return Staged("agent", "hero");

            var solo = _stage.BeginSoloAsync("hero");
            while (!solo.IsCompleted) yield return null;
            Assert.IsFalse(_stage.ActorVisibleOrPending("agent"),
                "sanity: катсцена обязана увести всех, кроме героини");

            _stage.EndSolo();
            yield return null;

            Assert.IsTrue(_stage.ActorVisibleOrPending("agent"),
                "агент не вернулся после катсцены — он пропадёт на несколько ходов, "
                + "пока сценарий сам не дойдёт до следующей команды о нём");
        }

        // Показ актёра асинхронный. Тот, чьи слои ещё грузятся, в списке
        // ВИДИМЫХ не значится — расталкивание его пропускало, и он всплывал
        // посреди катсцены рядом с героиней.
        [UnityTest]
        public IEnumerator ActorInFlightIsPushedAsideToo()
        {
            _stage.Play(@"{""script"":[{""op"":""say"",""text"":""кадр""}]}");
            yield return null;

            _stage.ApplyStage(Show("agent"));   // БЕЗ кадра ожидания — показ в полёте
            CollectionAssert.Contains(_stage.ActorsInFrame(), "agent",
                "летящий показ не виден распорядителю — его пропустит расталкивание");

            var solo = _stage.BeginSoloAsync("hero");
            while (!solo.IsCompleted) yield return null;
            yield return null;

            Assert.IsFalse(_stage.ActorVisibleOrPending("agent"),
                "актёр, чей показ был в полёте, пережил расталкивание — "
                + "он проявится уже посреди катсцены");
        }

        // Выход в меню кадр не возвращает: глава кончилась вместе с ним.
        // Забытые не должны воскреснуть при следующем EndSolo.
        [UnityTest]
        public IEnumerator DroppedFrameIsNotRestoredLater()
        {
            yield return Staged("agent", "hero");

            var solo = _stage.BeginSoloAsync("hero");
            while (!solo.IsCompleted) yield return null;
            _stage.DropSolo();
            _stage.EndSolo();
            yield return null;

            Assert.IsFalse(_stage.ActorVisibleOrPending("agent"),
                "забытый кадр всё-таки вернулся — персонаж прошлой главы встанет в меню");
            Assert.IsFalse(_stage.SoloActive, "катсцена кончилась, а кадр всё ещё за ней");
        }

        // «Катсцена при выходе не скрывает реплику» (Илья, 27.08): карточка
        // главы висела поверх ухода, и портал забирал героиню из-под неё.
        [UnityTest]
        public IEnumerator CutsceneTakesTheDialogueCardOffTheFrame()
        {
            _stage.Play(@"{""script"":[{""op"":""say"",""text"":""реплика главы""}]}");
            yield return null;
            yield return null;
            Assert.IsTrue(_stage.DialogueOnScreen, "sanity: реплика обязана быть на экране");

            var solo = _stage.BeginSoloAsync("hero");
            while (!solo.IsCompleted) yield return null;
            // ЖДЁМ ВРЕМЕНЕМ, А НЕ КАДРАМИ. Окно уходит СВОИМ ходом и прячется
            // хвостом анимации (см. SetSayVisible → DropOut): срок у неё в
            // миллисекундах, а батч-прогон успевает отсчитать три десятка
            // кадров быстрее, чем карточка доедет. Считать кадры значило бы
            // проверять скорость машины, а не уход окна.
            float until = Time.realtimeSinceStartup + 2f;
            while (_stage.DialogueOnScreen && Time.realtimeSinceStartup < until) yield return null;

            Assert.IsFalse(_stage.DialogueOnScreen,
                "окно реплики пережило начало катсцены — героиня уходит в портал "
                + "из-под карточки с чужим текстом");
        }

        // «Героиня пропадает, их 2 на самом деле: та что в игре и та что в
        // меню» (Илья, 27.08). Уборка сцены убивала ВСЕХ актёров, и по ту
        // сторону перехода собиралась вторая кукла — новый объект, новые слои,
        // новая загрузка. Тот, кто остаётся жить, обязан пережить уборку ТЕМ ЖЕ
        // объектом: один спрайт на меню и на игру.
        [UnityTest]
        public IEnumerator TheHeroineSurvivesASceneWipeAsTheSameObject()
        {
            yield return Staged("agent", "hero");
            _stage.KeepActorAlive = "hero";
            var before = Doll("hero");
            Assert.IsNotNull(before, "sanity: кукла героини должна стоять на сцене");
            Assert.IsTrue(before.activeSelf, "sanity: и стоять ВИДИМОЙ, а не слотом впрок");

            _stage.ClearStage();          // уборка сцены — смена главы
            yield return null;

            var after = Doll("hero");
            Assert.IsNotNull(after, "героиню снесло уборкой — по ту сторону соберётся вторая");
            Assert.AreSame(before, after,
                "героиня пересобрана: это уже ДРУГОЙ объект, а не та же кукла");
            Assert.IsNull(Doll("agent"),
                "sanity: все прочие уходят вместе со своей главой");
        }

        // ЖИВОЙ ОБЪЕКТ И ПАМЯТЬ О ПОЗЕ — РАЗНЫЕ ВЕЩИ. Объект переживает уборку,
        // чтобы не пересобираться; память — НЕ должна, потому что она липкая:
        // поза из витрины меню подмешивалась к авторской, и героиня выходила в
        // сцену стоящей по-менюшному («не встраивается в игру, хотя её реплика»).
        [UnityTest]
        public IEnumerator TheWipeForgetsHerPoseButKeepsHerAlive()
        {
            yield return Staged("agent", "hero");
            _stage.KeepActorAlive = "hero";

            _stage.ClearStage();
            yield return null;

            Assert.IsNotNull(Doll("hero"), "sanity: объект остаётся жить");
            Assert.IsFalse(_stage.RememberedByScript("hero"),
                "поза из меню пережила уборку — глава получит героиню, стоящую по-менюшному");
            Assert.IsFalse(_stage.RememberedByScript("agent"),
                "sanity: память об ушедшей главе уходит вместе с ней");
        }

        // ЧИНИТЬ КУКЛУ ЕСТЬ ЧЕМ, КЕМ БЫ ЕЁ НИ ПОСТАВИЛИ. Память «чем пересобрать
        // актёра» нужна троим: самолечению (слои умерли — собрать заново),
        // гардеробу (сменился наряд — переиграть ту же позу) и перестановке без
        // переодевания. Когда эту запись ограничили подписью истории, кукла
        // витрины осталась без команды: чинить стало нечем, и на главной повис
        // БЕЛЫЙ ПРЯМОУГОЛЬНИК (живой скрин Ильи, 27.08).
        [UnityTest]
        public IEnumerator EveryStagedActorLeavesSomethingToRebuildFrom()
        {
            _stage.Play(@"{""script"":[{""op"":""say"",""text"":""кадр""}]}");
            yield return null;

            foreach (var sender in new[] { LvnSender.Menu, LvnSender.Cutscene, LvnSender.Wardrobe })
            {
                var id = "doll-" + sender;
                _stage.ApplyStage(new JObject
                {
                    ["op"] = "actor", ["id"] = id, ["show"] = true, ["position"] = "center",
                }, sender);
                yield return null;

                Assert.IsTrue(_stage.RememberedByScript(id),
                    $"актёр от {sender} не оставил команды — самолечению нечем его пересобрать, "
                    + "и на его месте останется белый прямоугольник");
            }
        }

        // Уборка уводит её СО СЦЕНЫ: кадр новой главы обязан быть чистым, иначе
        // кукла из витрины так и стоит в нём до первой команды о ней.
        [UnityTest]
        public IEnumerator TheWipeTakesHerOffTheFrame()
        {
            yield return Staged("hero");
            _stage.KeepActorAlive = "hero";
            Assert.IsTrue(Doll("hero")?.activeSelf ?? false,
                "sanity: до уборки она в кадре — иначе проверка ниже ничего не значит");

            _stage.ClearStage();
            yield return null;

            var go = Doll("hero");
            Assert.IsNotNull(go, "sanity: объект жив");
            Assert.IsFalse(go.activeSelf, "героиня осталась в кадре новой главы");
        }

        // Героиня остаётся в кадре — катсцена уводит всех, КРОМЕ неё.
        [UnityTest]
        public IEnumerator TheKeptActorStaysInFrame()
        {
            yield return Staged("agent", "hero");

            var solo = _stage.BeginSoloAsync("hero");
            while (!solo.IsCompleted) yield return null;
            yield return null;

            Assert.IsTrue(_stage.ActorVisibleOrPending("hero"),
                "распорядитель увёл и ту, ради кого расчищали кадр");
        }
    }
}
