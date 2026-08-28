using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// Press handling on the stage root: a tap advances, a long press hides
    /// the chrome for the art view, drift past the threshold arms a drag
    /// (VnStage.Drag.cs), and Canvas-scene hotspots are hit-tested here.
    /// </summary>
    public sealed partial class VnStage
    {
        // ── press handling: tap advances, a LONG press hides the UI ─────────
        // The genre staple: hold anywhere and the whole chrome (dialogue box,
        // choices, HUD labels, quick menu — and the shell HUD via the event)
        // fades away so the player can admire the art; release brings it back,
        // and that release never counts as a tap. Because a press can now mean
        // two things, the tap action fires on POINTER UP, not down.

        private const long LongPressMs = 450;
        private const float PressDriftSq = 400f; // ~20px of drift cancels tap & hold

        private bool _chromeHidden;
        private bool _pressTracking, _suppressTap;
        private Vector2 _pressPos;
        private IVisualElementScheduledItem _longPress;

        /// <summary>Raised when the long-press art view hides/shows the chrome —
        /// the host mirrors it onto its own HUD.</summary>
        public event Action<bool> ChromeHiddenChanged;

        /// <summary>Просьба убрать интерфейс, с ПРИЧИНОЙ. Решает Режиссёр
        /// (LvnScreenDirector): интерфейс скрыт, пока держит хоть одна причина,
        /// и своя отмена не снимает чужую — катсцена не кончается оттого, что
        /// игрок отпустил палец.</summary>
        internal void HideChrome(string reason)
        {
            LvnScreenDirector.Current.HideChrome(reason);
            ApplyChromeVisibility();
        }

        /// <summary>Причина отпала. Интерфейс вернётся, только если держать его
        /// больше некому.</summary>
        internal void ShowChrome(string reason)
        {
            LvnScreenDirector.Current.ShowChrome(reason);
            ApplyChromeVisibility();
        }

        /// <summary>Снять все причины — сцену убрали, скрытый интерфейс не
        /// имеет права пережить главу, в которой его спрятали.</summary>
        internal void ShowChromeAll()
        {
            LvnScreenDirector.Current.ShowChromeAll();
            ApplyChromeVisibility();
        }

        private void ApplyChromeVisibility()
        {
            bool hidden = LvnScreenDirector.Current.ChromeHidden;
            if (_chromeHidden == hidden) return;
            _chromeHidden = hidden;
            var vis = hidden ? Visibility.Hidden : Visibility.Visible;
            if (_dialogue != null) _dialogue.style.visibility = vis;
            if (_choices != null) _choices.style.visibility = vis;
            if (_labelLayer != null) _labelLayer.style.visibility = vis;
            if (_menu != null) _menu.style.visibility = vis;
            // Слой `ui` — такая же часть интерфейса: в катсцене не должно
            // остаться ни кнопок, ни полос, иначе кадр не «кино», а игра с
            // пропавшим диалогом.
            if (_uiHudHost != null) _uiHudHost.style.visibility = vis;
            if (_uiOverHost != null) _uiOverHost.style.visibility = vis;
            ChromeHiddenChanged?.Invoke(hidden);
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            // РЕЖИМ «ВО ВЕСЬ РОСТ» ОТПУСКАЕТСЯ ЛЮБЫМ КАСАНИЕМ — и проверяется
            // ДО блокировки ввода. Панель открыта, значит ввод заблокирован, и
            // будь эта строка ниже, спрятанный интерфейс было бы уже не вернуть:
            // экран пуст, нажимать нечего. Касание съедается целиком, чтобы
            // возврат не сработал заодно как продвижение реплики.
            if (PanelPeeking) { SetPanelPeek(false); evt.StopPropagation(); return; }
            if (InputBlocked) return; // an overlay (quick menu) owns the screen
            if (_player == null || _player.Finished) return;
            if (_awaitingInput) return;
            // A `wait` swallows input — EXCEPT on a timed hotspot screen (icons +
            // wait), where the click must reach the hotspot and cancel the timer.
            if (_awaitingWait && _hotspots.Count == 0) return;
            if (evt.target is Button) return; // buttons (choices etc.) own their press

            _pressTracking = true;
            _suppressTap = false;
            _pressPos = evt.position;

            // A press on a draggable object arms a drag CANDIDATE: below the
            // drift threshold a release is still a tap (on_click works); past it
            // the object starts following the pointer instead.
            _dragCandidate = DraggableAt(evt.position);

            _longPress?.Pause();
            // Режим разглядывания арта по долгому нажатию — отключаемая часть
            // темы (ui.stage.long_press=false): продукт, где игроки жмут его
            // случайно и «теряют интерфейс», выключает жест данными.
            if (Theme?.LongPressArtView ?? true)
            {
                _longPress = _uiRoot?.schedule.Execute(() =>
                {
                    if (!_pressTracking || _dragId != null) return;
                    _suppressTap = true;      // this press is an art view, not a tap
                    HideChrome(LvnScreenDirector.ArtViewReason);
                });
                _longPress?.ExecuteLater(LongPressMs);
            }
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (!_pressTracking) return;
            if (_dragId != null) { DragMove(evt.position); return; }
            if (((Vector2)evt.position - _pressPos).sqrMagnitude <= PressDriftSq) return;
            _suppressTap = true; // a drag is neither a tap nor a hold
            _longPress?.Pause();
            if (_dragCandidate != null) DragBegin(_dragCandidate, evt.position);
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            bool wasTracking = _pressTracking;
            _pressTracking = false;
            _longPress?.Pause();
            _dragCandidate = null;

            if (_dragId != null) { DragEnd(evt.position); return; }
            // Отпустили палец: снимаем ТОЛЬКО свою причину. Если интерфейс
            // держит ещё и катсцена или «во весь рост» — он остаётся скрытым,
            // а касание всё равно съедается (игрок разглядывал арт).
            if (_chromeHidden)
            {
                bool mine = LvnScreenDirector.Current.HiddenBecause(LvnScreenDirector.ArtViewReason);
                ShowChrome(LvnScreenDirector.ArtViewReason);
                if (mine) return;
            }
            if (!wasTracking || _suppressTap) return;
            if (Skipping) { StopSkip(); return; } // a tap during fast-forward just stops it
            HandleTap(evt.position);
        }

        private void OnPointerCancelled()
        {
            // Touch cancelled / capture lost mid-hold — never strand a hidden UI
            // or a half-dragged object.
            _pressTracking = false;
            _dragCandidate = null;
            if (_dragId != null) DragEnd(_pressPos);
            _longPress?.Pause();
            ShowChrome(LvnScreenDirector.ArtViewReason);
        }

        // Диагностика проглоченных касаний: «ничего не тыкается» на устройстве
        // иначе не разбирается вовсе — все ворота молчаливые. Раз в секунду.
        private float _lastSwallowLog;
        internal float _sayUpSince;

        private void LogSwallow(string reason)
        {
            if (LvnClock.Now() - _lastSwallowLog < 1f) return;
            _lastSwallowLog = LvnClock.Now();
            LvnLog.Trace($"[lvn-input] тап проглочен: {reason} (say={_sayUp} awaitingTap={_awaitingTap})");
        }

        private void HandleTap(Vector2 pos)
        {
            if (InputBlocked) { LogSwallow("InputBlocked (панель/окно держит ввод)"); return; }
            if (EntryGatePending) { LogSwallow("EntryGatePending (карточка главы)"); return; }
            if (_player == null || _player.Finished) return;
            if (_awaitingInput) { LogSwallow("awaitingInput (форма ввода)"); return; }
            // Same exception as OnPointerDown: a timed hotspot screen stays
            // clickable through the wait.
            if (_awaitingWait && _hotspots.Count == 0) { LogSwallow("awaitingWait (оп wait ещё идёт)"); return; }

            // Canvas-scene hotspots: there's no uGUI raycaster, so a tap is routed
            // here. Test it against each obj's normalized placement rect (top-left
            // origin, matching both placement.Y and UITK's y-down). Topmost
            // (last-placed) wins; a hit fires its on_click and swallows the advance.
            // A point-and-click screen (the Canvas scene has registered hotspots):
            // only hotspots act. A hit fires its on_click; a miss is IGNORED (it must
            // not advance/re-print the room). Hotspots win over tap-to-advance.
            if (_hotspots.Count > 0 && _uiRoot != null)
            {
                // Долю сцены считает общий перевод: здесь делили на размер
                // панели своими руками, и нулевой размер (первый layout, поворот
                // экрана) давал NaN — зона клика молча переставала ловить.
                var np = StagePoint(pos);
                var hit = np is Vector2 n ? HotspotAt(n) : null;
                if (hit != null)
                {
                    LvnPlayer.Log?.Invoke($"[click {pos.x:0},{pos.y:0} → {np.Value.x:0.00},{np.Value.y:0.00}] → HOTSPOT");
                    // Hotspots stay armed (no clear): clicking another object jumps
                    // straight to it (its on_click GoTo overrides the cursor), so no
                    // phantom "dismiss" tap is needed. A MISS falls through to the
                    // normal tap-advance below — so descriptions and the ending are
                    // still dismissable by tapping empty space.
                    hit();
                    return;
                }
                LvnPlayer.Log?.Invoke($"[click {pos.x:0},{pos.y:0}] → miss → advance");
                // A timed hotspot screen: a miss must neither advance nor
                // complete the line — the wait keeps ticking until hit/timeout.
                if (_awaitingWait) return;
                // fall through to tap-to-advance
            }

            // ОДНО КАСАНИЕ — ОДНА КАРТОЧКА. Строка ставится целиком, поэтому
            // «дописать её» касание больше не означает: оно всегда закрывает
            // текущий такт. Следующий ShowSay сам ведёт передачу «уход карточки
            // → пауза → приход», и одно физическое касание продвигает ровно на
            // одну читаемую карточку.
            if (_awaitingTap)
            {
                PlayUiSound(_sndClick);
                _awaitingTap = false;
                _player.Advance();
            }
            // САМОИСЦЕЛЕНИЕ: строка давно стоит на экране, а такт «не ждёт
            // касания» — потерянный awaitingTap (гонка барьера видимости с
            // перебитым показом; живой случай: игра глохла на 80% cold-главы
            // до перезахода). Вечная глухота хуже лишнего тапа: продвигаемся
            // и честно жалуемся в лог. Порог 1.5с не даёт сработать на
            // штатной передаче «уход карточки → пауза → приход».
            else if (_sayUp && LvnClock.Now() - _sayUpSince > 1.5f
                     && _clock.Passed(LvnStageClock.ActorVisibilityBarrier))
            {
                LvnLog.Trace("[lvn-input] такт не ждал касания при видимой строке — самоисцеление тапом");
                _player.Advance();
            }
            else LogSwallow("такт не ждёт касания (передача карточки/барьер видимости)");
        }

        // The hotspot under a pointer — topmost (last-placed) first; null if none.
        // Works from the EVENT position (not Input.mousePosition, which is dead in
        // the Device Simulator / touch). Both the pointer and each actor's real
        // on-screen rect are normalized to 0..1 top-left, so it's independent of
        // pixel scale and aspect (and panel-vs-canvas coordinate differences).
        // Точка уже В ДОЛЯХ сцены (StagePoint): перевод — не работа поиска зоны.
        private System.Action HotspotAt(Vector2 np)
        {
            if (_renderer == null) return null;
            float nx = np.x, ny = np.y; // UITK: top-left, y-down
            for (int i = _hotspots.Count - 1; i >= 0; i--)
            {
                // Renderer-normalized rect (0..1, top-left origin); null when the
                // renderer does its own picking or the actor is gone.
                var r = _renderer.ActorScreenRect(_hotspots[i].id);
                if (r == null) continue;
                if (r.Value.Contains(new Vector2(nx, ny))) return _hotspots[i].onClick;
            }
            return null;
        }
    }
}
