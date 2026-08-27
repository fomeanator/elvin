using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// НИЖНЕЕ ОКНО — общая поверхность под сценой, куда садятся лист истории,
    /// гардероб и прочие панели.
    ///
    /// <para>Окно одно на всех, и в этом весь смысл: панели не спорят за место
    /// и не заводят каждая свой выезд. Отсюда правило — открывающий говорит
    /// ЧТО показать, а КАК оно приезжает и что при этом делает диалог (гаснет,
    /// приподнимается, уходит) знает только этот дом.</para>
    /// </summary>
    public sealed partial class VnStage
    {
        // ── the shared bottom window (VnPanelHost) ───────────────────────────
        // One dialogue-skinned frame on the dialogue layer that hosts ANY
        // content (wardrobe, shop, minigames): showing it fades the dialogue
        // out and slides the frame up; new content cross-fades inside the same
        // frame. Lazily created; dropped with the chrome on rebuild.
        private VnPanelHost _panelHost;

        /// <summary>The stage's shared content window (created on demand, on
        /// the dialogue layer, wearing the dialogue's exact skin).</summary>
        public VnPanelHost PanelHost
        {
            get
            {
                if (_panelHost == null)
                {
                    _panelHost = new VnPanelHost(Theme);
                    var root = GetComponent<UIDocument>()?.rootVisualElement;
                    if (root != null)
                    {
                        int fxIndex = _fx != null ? root.IndexOf(_fx) : -1;
                        root.Insert(fxIndex < 0 ? root.childCount : fxIndex, _panelHost);
                    }
                }
                return _panelHost;
            }
        }

        /// <summary>Show host content in the shared window: the dialogue fades
        /// out and the same-skinned frame takes its place (or cross-fades from
        /// whatever it was already showing).</summary>
        public async Task ShowPanelAsync(VisualElement content)
        {
            // Окно гардероба ПОДНИМАЕТСЯ НАД репликой, а не меняется с ней
            // местами. Диалог и рама — один и тот же полупрозрачный скин в
            // одном месте экрана: гаснущие одновременно, они на ~80 мс роняли
            // суммарную плотность почти вдвое, и сквозь окно разово «вспыхивал»
            // фон (живой репорт «фон мелькнул перед гардеробом»). Рама рисуется
            // над диалогом — пусть встанет плотной, и только потом реплика
            // гаснет, уже полностью укрытая.
            if (_menu != null)
            {
                _menu.Close();
                _menu.style.visibility = Visibility.Hidden;
            }
            await PanelHost.ShowAsync(content);
            await FadeDialogueAsync(true);
        }

        /// <summary>Dismiss the shared window and bring the dialogue back.</summary>
        public async Task HidePanelAsync()
        {
            // Симметрично показу: диалог возвращается ПОД стоящей рамой, и
            // только затем рама уходит — плотность окна не проваливается.
            // PanelOpen/InputBlocked держатся до конца: Resume() зовётся после
            // этой задачи, следующая реплика не стартует под чужим хромом.
            await FadeDialogueAsync(false);
            if (_panelHost != null) await _panelHost.HideAsync();
            ArmPanelInputGuard(0.12f);
        }

        /// <summary>Fade the dialogue box (and choices) out/in — the shared
        /// window replaces it visually, so both never fight for the bottom.</summary>
        public void SetDialogueFaded(bool faded)
            => LvnAsync.Fire(FadeDialogueAsync(faded), "PanelDialogueFade");

        private async Task FadeDialogueAsync(bool faded)
        {
            float to = faded ? 0f : 1f;
            float seconds = VnTheme.Motion(0.18f);
            // The story panel OWNS the screen while it's up (the genre rule):
            // the quick-menu chrome hides with the dialogue — no burger over
            // the wardrobe, no half-working Exit under a held story.
            if (_menu != null)
            {
                if (faded) _menu.Close();
                _menu.style.visibility = faded ? Visibility.Hidden : Visibility.Visible;
            }
            var dialogue = _dialogue != null
                ? ScreenFx.FadeAsync(_dialogue, faded ? 1f : 0f, to, seconds, _cts?.Token ?? default)
                : Task.CompletedTask;
            var choices = _choices != null
                ? ScreenFx.FadeAsync(_choices, faded ? 1f : 0f, to, seconds, _cts?.Token ?? default)
                : Task.CompletedTask;
            await Task.WhenAll(dialogue, choices);
        }

        /// <summary>The platform back pressed while the shared story panel is
        /// open — the panel's OWNER dismisses its content (the wardrobe sheet's
        /// cancel). The stage can't: it only hosts the frame.</summary>
        public Action PanelCancelRequested;

        /// <summary>Close the quick menu if it's open (host screens that take
        /// over from a menu tap call this so the scrim doesn't linger).</summary>
    }
}
