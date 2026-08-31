using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// <summary>
    /// УХОД ФИГУРЫ — РАБОТА БЕЗ АРТА.
    ///
    /// <para>Сорок строк ухода жили началом чужого метода — самого длинного в
    /// рантайме показа, — и держались там на РАННЕМ ВОЗВРАТЕ, то есть на
    /// порядке строк. Пока уход шёл общим путём, он ЖДАЛ те самые слои, которые
    /// собирался увести: на медленной сети фигура оставалась в кадре целыми
    /// тактами после своего ухода. Вставь кто-нибудь работу выше раннего
    /// возврата — и правило тихо перестало бы действовать, а увидеть это можно
    /// только на живом телефоне с плохой связью.</para>
    ///
    /// <para>ЗДЕСЬ ОНО ЗАКРЕПЛЕНО ПОВЕДЕНИЕМ, А НЕ ФОРМОЙ. Форму (у ухода нет
    /// ожиданий и добычи арта) сторожит Go-страж по тексту файла; он молчит,
    /// если ждать начнут ЧУЖИМИ руками — например, вызовом, который сам внутри
    /// ждёт. Поэтому уход здесь запускают настоящей командой с настоящим
    /// загрузчиком, который НИКОГДА не отвечает: показ на таком повисает, уход
    /// обязан кончиться в тот же миг.</para>
    ///
    /// <para>КАК ПОДНЯТА СЦЕНА. Выключенной (SetActive(false) до AddComponent):
    /// жизненный цикл MonoBehaviour в EditMode не запускается, панель UITK не
    /// строится — а уходу панель не нужна. Рисование подменено записывающим
    /// рендерером: «убрал фигуру» проверяется по тому, что сцена ПОПРОСИЛА
    /// нарисовать, а не по пикселям.</para>
    /// </summary>
    public class ActorExitTests
    {
        private GameObject _go;
        private VnStage _stage;
        private RecordingRenderer _drawn;
        private StalledAssets _art;
        private Sprite _pixel;
        private Texture2D _tex;
        private float _tick;

        private const BindingFlags Any =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        // ── помощники сцены ──────────────────────────────────────────────────

        /// <summary>Рендерер-протокол: что сцена попросила нарисовать и с каким
        /// размещением. Ни одной живой поверхности — уходу они не нужны.</summary>
        private sealed class RecordingRenderer : ISceneRenderer
        {
            public readonly List<string> Placed = new List<string>();
            public readonly List<(string id, bool withLayers, Placement pl)> Applied
                = new List<(string, bool, Placement)>();
            public int Calls => Placed.Count + Applied.Count;

            public void PlaceActor(string id, Placement placement) => Placed.Add(id);
            public void ApplyActor(string id, IReadOnlyList<Sprite> layers, Placement placement,
                Action onClick, IReadOnlyList<string> layerIds, IReadOnlyList<Vector4> layerRects,
                IReadOnlyList<SpriteCatalog.ResolvedLayer> layerDefs = null)
                => Applied.Add((id, layers != null, placement));

            public void SetBackground(Sprite sprite) { }
            public void SetBackground(Sprite sprite, float crossfadeSeconds) { }
            public void PanBackground(float from01, float to01, float seconds) { }
            public void ClearBackground() { }
            public Rect? ActorScreenRect(string id) => null;
            public void RemoveAll() { }
            public string KeepAlive { get; set; }
            public void SetFrames(string id, Dictionary<string, Dictionary<string, Sprite>> frames) { }
            public void EnsureIdle(string id, LvnAnim idle) { }
            public void EnsureBlink(string id, LvnAnim blink) { }
            public void PlayGesture(string id, LvnAnim gesture, LvnAnim idle) { }
            public void PlayAnim(string id, string channel, LvnAnim anim) { }
            public void PlayAnimQueued(string id, string channel, LvnAnim anim) { }
            public void StopAnim(string id, string target) { }
            public void Talk(string id, LvnAnim talk, bool on) { }
            public void HighlightSpeaker(string who) { }
            public void Shake(float amplitude, float seconds) { }
            public void Zoom(float factor, float seconds) { }
            public void Pan(float x, float y, float seconds) { }
            public void ResetCamera(float seconds) { }
            public void Set3DBackdrop(GameObject prefab) { }
            public void Frame3D(float? x, float? y, float? z, float? pitch, float? yaw, float? fov, float seconds) { }
            public void Set3DLive(bool live) { }
            public bool TryBlur(float strength01, float seconds) => false;
            public bool TryFx(JObject cmd) => false;
            public bool TryPortal(JObject cmd) => false;
            public bool TrySpriteFx(string id, JObject cmd) => false;
            public void Teardown() { }
        }

        /// <summary>Загрузчик, у которого всё уже под рукой: слой отдаётся в том
        /// же такте. Нужен там, где надо ВИДЕТЬ разницу между «показ принёс
        /// слои» и «уход не принёс».</summary>
        private sealed class InstantAssets : ILvnAssets
        {
            private readonly Sprite _sprite;
            public InstantAssets(Sprite s) => _sprite = s;
            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct) => Task.FromResult(_sprite);
            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct) => Task.FromResult<AudioClip>(null);
            public Task PreloadAsync(IReadOnlyList<string> urls, string kind, CancellationToken ct) => Task.CompletedTask;
            public void Unload(string url) { }
            public void UnloadAll() { }
        }

        /// <summary>МЕДЛЕННАЯ СЕТЬ ЦЕЛИКОМ: запрошенный слой не приезжает
        /// НИКОГДА. Показ на таком загрузчике честно висит — ровно то, во что
        /// упирался уход, пока шёл общим путём.</summary>
        private sealed class StalledAssets : ILvnAssets
        {
            public readonly List<string> Asked = new List<string>();
            private readonly List<TaskCompletionSource<Sprite>> _pending
                = new List<TaskCompletionSource<Sprite>>();

            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct)
            {
                Asked.Add(url);
                var tcs = new TaskCompletionSource<Sprite>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending.Add(tcs);
                return tcs.Task;
            }

            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct) => Task.FromResult<AudioClip>(null);
            public Task PreloadAsync(IReadOnlyList<string> urls, string kind, CancellationToken ct) => Task.CompletedTask;
            public void Unload(string url) { }
            public void UnloadAll() { }

            /// <summary>Отпустить всех ждущих отменой: показ выходит по
            /// OperationCanceledException, не уводя в ретраи с их логами.</summary>
            public void ReleaseAll()
            {
                foreach (var t in _pending) t.TrySetCanceled();
                _pending.Clear();
            }
        }

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("actor-exit-stage");
            _go.SetActive(false);   // OnEnable не строит панель — уходу она не нужна
            _stage = _go.AddComponent<VnStage>();

            _drawn = new RecordingRenderer();
            Set("_renderer", _drawn);
            _art = new StalledAssets();

            // ЧАСЫ ТЕСТА ВСЕГДА В БУДУЩЕМ. Уход ставит постановочный барьер
            // («уходящий доигрывает уход, прежде чем войдёт следующий»), и
            // следующая команда честно ждёт его доли секунды. Это правильно и
            // проверяется отдельно — но здесь превратило бы каждый тест про
            // уход в ожидание секундомера. Хронометрист берёт «сейчас» снаружи
            // ровно для таких случаев: каждое обращение уносит часы на сто
            // секунд вперёд, и всякий барьер оказывается просроченным.
            _tick = 0f;
            ((LvnStageClock)Field("_clock").GetValue(_stage)).Now = () => _tick += 100f;
            // Без токена загрузчик даже не спрашивают (см. LoadSceneSpriteAsync),
            // и «показ ждёт арт» доказать было бы нечем.
            Set("_cts", new CancellationTokenSource());

            _tex = new Texture2D(2, 2);
            _pixel = Sprite.Create(_tex, new Rect(0, 0, 2, 2), new Vector2(0.5f, 0.5f));
        }

        [TearDown]
        public void TearDown()
        {
            _art?.ReleaseAll();
            if (_go != null) UnityEngine.Object.DestroyImmediate(_go);
            if (_pixel != null) UnityEngine.Object.DestroyImmediate(_pixel);
            if (_tex != null) UnityEngine.Object.DestroyImmediate(_tex);
        }

        // ── правила ──────────────────────────────────────────────────────────

        /// <summary>ГЛАВНОЕ ПРАВИЛО: уход не ждёт арта. Загрузчик не отвечает
        /// никогда — показ на нём висит, а уход обязан закончиться в тот же
        /// такт и не попросить ни одной картинки.</summary>
        [Test]
        public void УходНеЖдётЗагрузкуАртаИНеПроситКартинок()
        {
            _stage.Assets = _art;

            var show = Apply(Actor("mira", ("sprite_url", "mira/body.png")));
            Assert.IsFalse(show.IsCompleted,
                "опора: показ на неотвечающем загрузчике обязан ЖДАТЬ — иначе уход не с чем сравнивать");
            Assert.AreEqual(1, _art.Asked.Count, "опора: показ попросил слой");

            _art.Asked.Clear();
            var hide = Apply(Actor("mira", ("show", false), ("sprite_url", "mira/body.png")));

            Assert.IsTrue(hide.IsCompleted,
                "уход ждёт те самые слои, которые собирался увести: на медленной сети "
                + "фигура останется в кадре целыми тактами после своей же команды");
            Assert.IsEmpty(_art.Asked,
                "уходу арт НЕ НУЖЕН — ему нужно место, где фигура стоит сейчас, и повод её убрать");
        }

        /// <summary>Команда со <c>show=false</c> действительно убирает фигуру:
        /// рендерер получает размещение с погашенной видимостью и БЕЗ слоёв —
        /// арт не пересобирается, уже надетое просто уводится.</summary>
        [Test]
        public void КомандаShowЛожьУбираетФигуруНеПересобираяАрт()
        {
            _stage.Assets = new InstantAssets(_pixel);
            Done(Apply(Actor("mira", ("position", "left"), ("sprite_url", "mira/body.png"))));
            Assert.IsTrue(LastApplied("mira").pl.Show, "опора: показ вывел фигуру");
            Assert.IsTrue(LastApplied("mira").withLayers, "опора: показ принёс слои");

            _drawn.Applied.Clear();
            // Арт у команды разрешим — ровно как в жизни (каталог знает фигуру).
            // Иначе «слоёв не прислали» доказывало бы лишь то, что их неоткуда взять.
            Done(Apply(Actor("mira", ("show", false), ("sprite_url", "mira/body.png"))));

            var (withLayers, pl) = LastApplied("mira");
            Assert.IsFalse(pl.Show, "фигура осталась видимой после команды уйти");
            Assert.IsFalse(withLayers,
                "уход прислал слои — значит, он шёл путём показа и пересобирал арт вместо того, "
                + "чтобы просто увести уже надетое");
            Assert.AreEqual(0.25f, pl.X, 0.001f,
                "уход уводит фигуру С ЕЁ МЕСТА: место берётся из памяти, а не из умолчания");
        }

        /// <summary>Ушедшую фигуру нельзя нажать. Горячая точка снимается ровно
        /// у неё — соседи по кадру остаются кликабельными.</summary>
        [Test]
        public void УходСнимаетГорячуюТочкуТолькоУУшедшего()
        {
            Show("mira", ("on_click", "к_мире"));
            Show("dorn", ("on_click", "к_дорну"), ("position", "right"));
            Assert.AreEqual(2, Hotspots.Count, "опора: обе фигуры кликабельны");

            Hide("mira");

            Assert.IsFalse(Hotspots.Exists(h => h.id == "mira"),
                "по ушедшей фигуре всё ещё можно щёлкнуть — нажатие уйдёт в пустое место кадра");
            Assert.IsTrue(Hotspots.Exists(h => h.id == "dorn"),
                "уход одной фигуры обезоружил другую");
        }

        /// <summary>Ушедший предмет нельзя тащить: невидимое перетаскиваемое —
        /// это предмет, который тянется из ниоткуда.</summary>
        [Test]
        public void УходСнимаетПеретаскиваемость()
        {
            Show("apple", ("draggable", true), ("on_drop", "bag:яблоко_в_сумке"));
            Assert.IsTrue(Draggables.Contains("apple"), "опора: предмет взведён на перетаскивание");

            Hide("apple");

            Assert.IsFalse(Draggables.Contains("apple"),
                "спрятанный предмет остался перетаскиваемым — его можно тащить, не видя");
        }

        /// <summary>
        /// УХОД НИКОГО НЕ СТАВИТ. Подпись под позой отвечает на вопрос «кто
        /// поставил фигуру», и скрытие на него не отвечает: место осталось
        /// прежним, авторским. Запиши уход себя автором — и правило «авторская
        /// команда не наследует чужую позу» сочтёт позу гардеробной.
        /// </summary>
        [Test]
        public void УходЗапоминаетКомандуНоНеПодписываетсяПодПозой()
        {
            Show("mira", ("position", "left"));
            Assert.IsTrue(_stage.Memory.TryPoseSender("mira", out var afterShow) && afterShow == LvnSender.Story,
                "опора: позу поставил автор");

            var hide = Actor("mira", ("show", false));
            Done(Apply(hide, LvnSender.Wardrobe));   // гардероб убрал манекен

            Assert.IsTrue(_stage.Memory.TryPoseSender("mira", out var after), "подпись под позой пропала совсем");
            Assert.AreEqual(LvnSender.Story, after,
                "уход объявил автором позы себя — следующая авторская команда сочтёт фигуру свежей");
            Assert.IsTrue(_stage.Memory.TryCommand("mira", out var remembered) && ReferenceEquals(remembered, hide),
                "команда ухода не запомнена: пересобирать фигуру (самолечение, гардероб) стало нечем");
        }

        /// <summary>ТО ЖЕ ПРАВИЛО ПОСЛЕДСТВИЕМ, которое видит игрок: героиня
        /// стояла слева, гардероб её убрал, автор просто показывает её снова —
        /// она обязана вернуться ТУДА ЖЕ, а не в слот по умолчанию.</summary>
        [Test]
        public void ПослеУходаАвторскийПоказБезПозицииНеУводитВСлотПоУмолчанию()
        {
            Show("mira", ("position", "left"));
            Done(Apply(Actor("mira", ("show", false)), LvnSender.Wardrobe));

            _drawn.Applied.Clear();
            Show("mira");   // автор: «покажи её», без position

            Assert.AreEqual(0.25f, LastApplied("mira").pl.X, 0.001f,
                "фигура уехала в слот по умолчанию: уход подписался под позой, и авторская команда "
                + "сочла героиню свежей — игрок видит это как «закрыл гардероб — героиня переехала»");
        }

        /// <summary>Уход фигуры, которой на сцене НЕ БЫЛО, ничего не рисует и
        /// не падает: скрывать нечего, а команда законна (катсцена расчищает
        /// кадр, не спрашивая, кто в нём есть).</summary>
        [Test]
        public void УходНикогдаНеПоказаннойФигурыНичегоНеРисуетИНеРоняет()
        {
            _stage.Assets = _art;

            Hide("призрак");

            Assert.AreEqual(0, _drawn.Calls,
                "сцена рисовала уход фигуры, которой в кадре никогда не было");
            Assert.IsEmpty(_art.Asked, "и уж тем более не качала её арт");
            Assert.IsTrue(_stage.Memory.TryCommand("призрак", out _),
                "команда всё равно запоминается: следующий показ обязан знать, чем её собрать");
        }

        // ── помощники ────────────────────────────────────────────────────────

        private static JObject Actor(string id, params (string key, object value)[] fields)
        {
            var cmd = new JObject { ["op"] = "actor", ["id"] = id };
            foreach (var f in fields) cmd[f.key] = JToken.FromObject(f.value);
            return cmd;
        }

        /// <summary>Показ БЕЗ загрузчика: слои не запрашиваются вовсе, и вся
        /// работа кончается в том же такте — тестам про уход нужна только
        /// память сцены и рендерер.</summary>
        private void Show(string id, params (string key, object value)[] fields)
        {
            _stage.Assets = null;
            Done(Apply(Actor(id, fields)));
        }

        private void Hide(string id) => Done(Apply(Actor(id, ("show", false))));

        private Task Apply(JObject cmd, LvnSender sender = LvnSender.Story)
        {
            var m = typeof(VnStage).GetMethod("ApplyActorAsync", Any);
            if (m == null) Assert.Fail("метод ApplyActorAsync у VnStage пропал — поправь якорь теста");
            var task = (Task)m.Invoke(_stage, new object[] { cmd, false, false, sender });
            if (task.IsFaulted) throw task.Exception.GetBaseException();
            return task;
        }

        private static void Done(Task t)
            => Assert.IsTrue(t.IsCompleted, "работа сцены не кончилась в этом такте, хотя ждать ей нечего");

        private (bool withLayers, Placement pl) LastApplied(string id)
        {
            for (int i = _drawn.Applied.Count - 1; i >= 0; i--)
                if (_drawn.Applied[i].id == id)
                    return (_drawn.Applied[i].withLayers, _drawn.Applied[i].pl);
            Assert.Fail($"сцена ни разу не попросила нарисовать '{id}'");
            return default;
        }

        private List<(string id, Action onClick)> Hotspots
            => (List<(string id, Action onClick)>)Field("_hotspots").GetValue(_stage);

        private List<string> Draggables
        {
            get
            {
                var dict = (System.Collections.IDictionary)Field("_draggables").GetValue(_stage);
                var ids = new List<string>();
                foreach (var k in dict.Keys) ids.Add((string)k);
                return ids;
            }
        }

        private static FieldInfo Field(string name)
        {
            for (Type t = typeof(VnStage); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, Any);
                if (f != null) return f;
            }
            Assert.Fail($"поле {name} у VnStage пропало — поправь якорь теста");
            return null;
        }

        private void Set(string field, object value) => Field(field).SetValue(_stage, value);
    }
}
