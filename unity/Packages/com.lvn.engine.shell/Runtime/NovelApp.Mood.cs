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
            _moodClip = new LvnMoodClip(doc?.Script, stage, cmd =>
            {
                stage.ApplyStage(cmd, LvnSender.Menu);
                return true;
            });
            _mood.Known = _moodClip.Labels();
            LvnLog.Trace($"[lvn-mood] реакции подняты: меток {_mood.Known.Count}");
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
                _ => _mood?.Touch(),
                UnityEngine.UIElements.TrickleDown.TrickleDown);
        }

        private const float MoodTickSeconds = 0.5f;
        private bool _moodTicking;

        private void Play(string label)
        {
            if (string.IsNullOrEmpty(label) || _moodClip == null) return;
            if (!_moodClip.Play(label, () => Play(_mood.Ended())))
                LvnLog.Trace($"[lvn-mood] метки «{label}» в сценарии нет");
        }
    }
}
