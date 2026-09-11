using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using Newtonsoft.Json.Linq;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// РЕАКЦИИ ГЕРОИНИ ВИТРИНЫ (TR-66) — кто поднимает события и кто их играет.
    ///
    /// <para>Заказ Ильи: «управление эмоциями героини по событиям», «эмоции и
    /// тайминги настраиваются манифестом». Сами реакции написаны обычным
    /// сценарием (метки <c>on_store</c>, <c>on_idle</c>, <c>on_purchase</c>…),
    /// правило выбора живёт в <see cref="LvnMenuMood"/>, исполнение — в
    /// <see cref="LvnMoodClip"/>. Здесь только проводка: оболочка знает, что
    /// игрок пришёл в комнату, купил пакет или молчит, — и говорит об этом.</para>
    ///
    /// <para>Нет сценария в манифесте — ничего не происходит и ничего не
    /// грузится: движок эмоций по именам не знает, и знать не должен.</para>
    /// </summary>
    public partial class NovelApp
    {
        private LvnMenuMood _mood;
        private LvnMoodClip _moodClip;

        /// <summary>Поднять сценарий реакций из манифеста. Зовётся при подъёме
        /// меню; пустое поле — тишина.</summary>
        private void WireMood(LvnManifest manifest)
        {
            var url = manifest?.ui?.browse?.reactions;
            _mood = null; _moodClip = null;
            if (string.IsNullOrEmpty(url) || _assets == null || Stage == null) return;
            _mood = new LvnMenuMood
            {
                IdleAfter = manifest.ui.browse.idle_after ?? 10f,
            };
            LvnAsync.Fire(LoadMoodAsync(url), "MoodScript");
        }

        private async Task LoadMoodAsync(string url)
        {
            string text = null;
            try { text = await _assets.LoadTextAsync(url, default); }
            catch { /* нет файла — витрина живёт как жила */ }
            if (string.IsNullOrEmpty(text) || Stage == null || _mood == null) return;
            LvnDocument doc = null;
            try { doc = LvnDocument.Parse(text); }
            catch (System.Exception e)
            {
                LvnLog.Warn("[lvn-mood] сценарий реакций не разобран: " + e.Message);
                return;
            }
            var stage = Stage;
            _moodFaceAxis = FaceAxisOf(Stage.Prima.Id);
            _moodClip = new LvnMoodClip(doc?.Script, stage, cmd =>
            {
                // РЕАКЦИЯ МЕНЯЕТ ЛИЦО, А НЕ КОМАНДУЕТ СЦЕНОЙ. `actor id emotion=…`
                // без геометрии — это выражение (LvnFace): оно не трогает
                // положение фигуры (команда без x ставила её в конец переезда
                // сразу — «героиня прыгает»), не записывается в облик и уступает
                // выбору игрока в гардеробе (сценарная ось перебивала фишки —
                // «эмоции работают непонятно как»). Всё остальное — как раньше,
                // сценой.
                if (IsFaceOnly(cmd) && !string.IsNullOrEmpty(_moodFaceAxis))
                {
                    var id = (string)cmd["id"];
                    LvnFace.Hold(id, _moodFaceAxis, (string)cmd["emotion"]);
                    stage.RefreshActor(id);
                    return true;
                }
                stage.ApplyStage(cmd, LvnSender.Menu);
                return true;
            });
            _mood.Known = _moodClip.Labels();
            LvnLog.Trace($"[lvn-mood] реакции подняты: меток {_mood.Known.Count}, ось лица «{_moodFaceAxis ?? "—"}»");
            StartMoodTicking();
            Raise("on_launch", act: true);
            RoomMood(_shell?.Tab ?? LvnTabs.Home);
        }

        /// <summary>Комната сменилась — настроение места. Зовёт переезд
        /// вкладок, он же единственный, кто знает, куда игрок приехал.</summary>
        private void RoomMood(int tab)
        {
            if (_mood == null) return;
            var label = "on_" + (LvnTabs.NameOf(tab) ?? "home");
            Play(_mood.EnterRoom(label));
        }

        /// <summary>Событие игрока: покупка, наряд, касание героини, возврат из
        /// главы. Метки нет — ничего не происходит.</summary>
        private void Raise(string label, bool act)
        {
            if (_mood == null) return;
            Play(act ? _mood.Act(label) : _mood.EnterRoom(label));
        }

        /// <summary>
        /// ТИК БЕЗДЕЙСТВИЯ И КАСАНИЕ. Полсекунды — достаточная точность для
        /// десятисекундной тишины, и это дешевле, чем считать каждый кадр.
        /// </summary>
        private void StartMoodTicking()
        {
            var root = _shell?.Document?.rootVisualElement;
            if (root == null || _moodTicking) return;
            _moodTicking = true;
            root.schedule.Execute(() =>
            {
                if (_mood == null || _moodClip == null || InChapter) return;
                Play(_mood.Tick(MoodTickSeconds, "on_idle"));
            }).Every(500);
            // ЛЮБОЕ КАСАНИЕ — ПРИЗНАК ЖИЗНИ. Ловим на корне и не мешаем:
            // TrickleDown видит тап раньше кнопок, а обработку мы не трогаем.
            root.RegisterCallback<UnityEngine.UIElements.PointerDownEvent>(
                _ => Wake(),
                UnityEngine.UIElements.TrickleDown.TrickleDown);
        }

        private const float MoodTickSeconds = 0.5f;
        private bool _moodTicking;

        private void Play(string label)
        {
            if (string.IsNullOrEmpty(label) || _moodClip == null) return;
            if (!_moodClip.Play(label, OnClipEnded))
                LvnLog.Trace($"[lvn-mood] метки «{label}» в сценарии нет");
        }

        /// <summary>Клип доиграл: действие и скука возвращаются к настроению
        /// комнаты, а если комнаты нет — снимают своё лицо, чтобы «сон» после
        /// покупки не остался на героине навсегда. Комната по концу ничего не
        /// снимает: её лицо и есть покой.</summary>
        private void OnClipEnded()
        {
            if (_mood == null) return;
            bool wasRoom = _mood.PlayingKind == LvnMenuMood.Kind.Room;
            var next = _mood.Ended();
            if (!string.IsNullOrEmpty(next)) Play(next);
            else if (!wasRoom) ReleaseFace();
        }

        /// <summary>Ось лица героя по манифесту — та же, по которой гардероб
        /// показывает фишки эмоций. Нет оси — реакции идут сценой, как раньше.</summary>
        private string FaceAxisOf(string id)
        {
            if (string.IsNullOrEmpty(id) || _manifest?.sprites == null
                || !_manifest.sprites.TryGetValue(id, out var def) || def?.axes == null) return null;
            foreach (var kv in def.axes)
                if (Lvn.UI.LvnWardrobeStage.IsEmotion(kv.Key) && kv.Value != null && kv.Value.Count > 1)
                    return kv.Key;
            return null;
        }

        /// <summary>Команда реакции — «только лицо»: актёр с эмоцией и без
        /// геометрии. Стоит автору написать x= или show=false — это уже
        /// постановка, и она идёт сценой.</summary>
        private static bool IsFaceOnly(Newtonsoft.Json.Linq.JObject cmd)
        {
            if (cmd == null || (string)cmd["op"] != "actor") return false;
            if (string.IsNullOrEmpty((string)cmd["id"]) || cmd["emotion"] == null) return false;
            foreach (var key in new[] { "x", "y", "position", "width", "height", "z", "enter", "exit",
                                        "transition_duration", "flip", "mirror", "scale" })
                if (cmd[key] != null) return false;
            return Lvn.LvnBool.Of(cmd["show"], true);
        }

        /// <summary>
        /// КАСАНИЕ БУДИТ. Скука кончается сном, и сон держится, пока игрок не
        /// тронет экран: раньше касание лишь сбрасывало таймер, а клип доигрывал
        /// сам — героиня «спала» ещё десять секунд после того, как её позвали.
        /// Будим только со скуки: действие (покупка, примерка) доигрывает, его
        /// касанием не обрывают.
        /// </summary>
        private void Wake()
        {
            if (_mood == null) return;
            bool asleep = _mood.PlayingKind == LvnMenuMood.Kind.Idle && _mood.Playing != null;
            _mood.Touch();
            if (asleep) Play(_mood.Ended());
        }

        private void ReleaseFace()
        {
            var id = Stage?.Prima?.Id;
            if (string.IsNullOrEmpty(id) || LvnFace.Holding(id) == null) return;
            LvnFace.Release(id);
            Stage.RefreshActor(id);
        }

        /// <summary>
        /// НАСТРОЕНИЕ КОМНАТЫ — ПОСЛЕ ПЕРЕЕЗДА, НЕ ВМЕСТЕ С НИМ. Реакция и
        /// перелёт куклы шли в один кадр; вторая команда обгоняла первую, и
        /// фигура вставала в конец пути сразу. Ждём столько, сколько едет
        /// комната (TravelMs), и играем настроение той комнаты, куда приехали
        /// ПОСЛЕДНЕЙ: два быстрых переезда подряд не должны сыграть настроение
        /// промежуточной.
        /// </summary>
        private void RoomMoodAfterTravel(int tab)
        {
            if (_mood == null) return;
            int seq = ++_roomMoodSeq;
            var root = _shell?.Document?.rootVisualElement;
            if (root == null) { RoomMood(tab); return; }
            root.schedule.Execute(() => { if (seq == _roomMoodSeq && !InChapter) RoomMood(tab); })
                .StartingIn(Lvn.UI.LvnMenuStage.TravelMs + RoomMoodSettleMs);
        }

        private int _roomMoodSeq;
        private string _moodFaceAxis;
        /// <summary>Запас после перелёта: последний кадр твина и запись
        /// положения должны успеть раньше, чем лицо попросит пересборку.</summary>
        private const int RoomMoodSettleMs = 80;
    }
}
