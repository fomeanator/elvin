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
        // Осмотр сцены удержанием: палец водит по 3D-набору, авторский кадр
        // остаётся на месте, а взгляд отпускается обратно. Живёт только там,
        // где есть что осматривать — на плоском фоне вращать нечего.
        private bool _looking;
        private Vector2 _lookPos;
        private const float LookDegPerPx = 0.12f;
        // Ходьба тачем: ЛЕВАЯ половина экрана — виртуальный стик (тянешь от
        // точки нажатия — идёшь в ту сторону), правая — осмотр. Разделение по
        // половинам вместо нарисованного джойстика: палец не ищет элемент,
        // а кладётся куда попало — так делают все шутеры на телефоне.
        private bool _walking;
        private Vector2 _walkOrigin;
        private const float StickRadiusPx = 140f;
        private IVisualElementScheduledItem _longPress;

        /// <summary>Raised when the long-press art view hides/shows the chrome —
        /// the host mirrors it onto its own HUD.</summary>
        public event Action<bool> ChromeHiddenChanged;

        private void SetChromeHidden(bool hidden)
        {
            if (_chromeHidden == hidden) return;
            _chromeHidden = hidden;
            var vis = hidden ? Visibility.Hidden : Visibility.Visible;
            if (_dialogue != null) _dialogue.style.visibility = vis;
            if (_choices != null) _choices.style.visibility = vis;
            if (_labelLayer != null) _labelLayer.style.visibility = vis;
            if (_menu != null) _menu.style.visibility = vis;
            ChromeHiddenChanged?.Invoke(hidden);
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
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
            _longPress = _uiRoot?.schedule.Execute(() =>
            {
                if (!_pressTracking || _dragId != null) return;
                _suppressTap = true;      // this press is an art view, not a tap
                SetChromeHidden(true);
            });
            _longPress?.ExecuteLater(LongPressMs);
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (!_pressTracking) return;
            if (_dragId != null) { DragMove(evt.position); return; }
            if (_walking) { WalkMove(evt.position); return; }
            if (_looking) { LookMove(evt.position); return; }
            if (((Vector2)evt.position - _pressPos).sqrMagnitude <= PressDriftSq) return;
            _suppressTap = true; // a drag is neither a tap nor a hold
            _longPress?.Pause();
            if (_dragCandidate != null) { DragBegin(_dragCandidate, evt.position); return; }
            // Ничего перетаскиваемого под пальцем — значит игрок ОСМАТРИВАЕТСЯ.
            // Долгое нажатие (просмотр арта) отменяем: оно про то же намерение,
            // но осмотр богаче — он двигает саму сцену.
            if (_renderer != null && _renderer.Has3DSet)
            {
                _longPress?.Pause();
                SetChromeHidden(false);
                // Левая половина ведёт, правая смотрит.
                bool leftHalf = _uiRoot != null && _uiRoot.worldBound.width > 1f
                                && _pressPos.x < _uiRoot.worldBound.width * 0.5f;
                if (leftHalf && _walkEnabled)
                {
                    _walking = true;
                    _walkOrigin = _pressPos;
                    WalkMove(evt.position);
                }
                else
                {
                    _looking = true;
                    _lookPos = evt.position;
                }
            }
        }

        /// <summary>Включена ли ходьба — ставится скриптом через `bg3d walk=1`.
        /// Без неё левая половина работает как осмотр, чтобы не отнимать
        /// привычный жест у сцен, где ходить некуда.</summary>
        private bool _walkEnabled;

        public void SetWalkEnabled(bool on)
        {
            _walkEnabled = on;
            if (!on) { _walking = false; _renderer?.WalkStick3D(Vector2.zero); }
        }

        // Палец от точки нажатия — вектор движения. Дальше радиуса не растёт:
        // это стик, а не пращa.
        private void WalkMove(Vector2 pos)
        {
            var d = pos - _walkOrigin;
            // Экранный Y растёт вниз, а «вперёд» — это вверх.
            var v = new Vector2(d.x / StickRadiusPx, -d.y / StickRadiusPx);
            _renderer?.WalkStick3D(v);
        }

        // Экранные пиксели → градусы. Осмотр идёт ЗА ПАЛЬЦЕМ: тянешь вверх —
        // кадр едет вверх, будто сцену двигают рукой. Так же ведут себя карты
        // и фотографии, и для сенсорного экрана это привычнее, чем инверсия из
        // шутеров: там мышью управляют головой, а здесь пальцем — картинкой.
        private void LookMove(Vector2 pos)
        {
            var d = pos - _lookPos;
            _lookPos = pos;
            _renderer?.Look3D(d.y * LookDegPerPx, d.x * LookDegPerPx);
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            bool wasTracking = _pressTracking;
            _pressTracking = false;
            _longPress?.Pause();
            _dragCandidate = null;

            if (_dragId != null) { DragEnd(evt.position); return; }
            if (_walking)
            {
                _walking = false;
                _renderer?.WalkStick3D(Vector2.zero);   // отпустил — встал
                return;
            }
            if (_looking)
            {
                // Отпустил — кадр ОСТАЁТСЯ там, куда его увели. Пружинный
                // возврат к авторскому ракурсу мешал: осмотреться толком не
                // выходило, сцена выдёргивалась из рук на полудвижении. Взгляд
                // возвращает сам автор — любая команда `bg3d`, и постановка
                // нового набора тоже.
                _looking = false;
                return;
            }
            if (_chromeHidden) { SetChromeHidden(false); return; } // release restores, swallows the tap
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
            if (_looking) _looking = false; // жест прерван — кадр остаётся где был
            if (_walking) { _walking = false; _renderer?.WalkStick3D(Vector2.zero); }
            if (_dragId != null) DragEnd(_pressPos);
            _longPress?.Pause();
            SetChromeHidden(false);
        }

        private void HandleTap(Vector2 pos)
        {
            if (InputBlocked) return;
            if (EntryGatePending) return; // the chapter-title card owns the screen
            if (_player == null || _player.Finished) return;
            if (_awaitingInput) return;
            // Same exception as OnPointerDown: a timed hotspot screen stays
            // clickable through the wait.
            if (_awaitingWait && _hotspots.Count == 0) return;

            // Canvas-scene hotspots: there's no uGUI raycaster, so a tap is routed
            // here. Test it against each obj's normalized placement rect (top-left
            // origin, matching both placement.Y and UITK's y-down). Topmost
            // (last-placed) wins; a hit fires its on_click and swallows the advance.
            // A point-and-click screen (the Canvas scene has registered hotspots):
            // only hotspots act. A hit fires its on_click; a miss is IGNORED (it must
            // not advance/re-print the room). Hotspots win over tap-to-advance.
            // ТЕЛА 3D-СЦЕНЫ. Проверяются раньше плоских точек нажатия: они
            // стоят В МИРЕ, и попадание по ним однозначнее — экранный
            // прямоугольник спрайта может перекрывать половину кадра, а
            // габариты тела в сцене нет.
            if (_uiRoot != null && _renderer != null && _renderer.Has3DSet)
            {
                float w3 = _uiRoot.layout.width, h3 = _uiRoot.layout.height;
                if (w3 > 1f && h3 > 1f)
                {
                    // Доля кадра, снизу вверх: камера набора живёт в координатах
                    // вьюпорта, а панель — в экранных с началом сверху.
                    var vp = new Vector2(pos.x / w3, 1f - pos.y / h3);
                    var label = _renderer.Pick3D(vp);
                    if (!string.IsNullOrEmpty(label))
                    {
                        LvnPlayer.Log?.Invoke($"[click {pos.x:0},{pos.y:0}] → тело сцены → :{label}");
                        if (_dialogue.IsRevealing) _dialogue.Complete();
                        _player?.GoTo(label);
                        _player?.Advance();
                        return;
                    }
                }
            }

            if (_hotspots.Count > 0 && _uiRoot != null)
            {
                float pw = _uiRoot.layout.width, ph = _uiRoot.layout.height;
                var hit = HotspotAt(pos, pw, ph);
                if (hit != null)
                {
                    LvnPlayer.Log?.Invoke($"[click {pos.x:0},{pos.y:0} of {pw:0}x{ph:0}] → HOTSPOT");
                    // Hotspots stay armed (no clear): clicking another object jumps
                    // straight to it (its on_click GoTo overrides the cursor), so no
                    // phantom "dismiss" tap is needed. A MISS falls through to the
                    // normal tap-advance below — so descriptions and the ending are
                    // still dismissable by tapping empty space.
                    if (_dialogue.IsRevealing) _dialogue.Complete();
                    hit();
                    return;
                }
                LvnPlayer.Log?.Invoke($"[click {pos.x:0},{pos.y:0} of {pw:0}x{ph:0}] → miss → advance");
                // A timed hotspot screen: a miss must neither advance nor
                // complete the line — the wait keeps ticking until hit/timeout.
                if (_awaitingWait) return;
                // fall through to tap-to-advance
            }

            if (_dialogue.IsRevealing) { PlayUiSound(_sndClick); _dialogue.Complete(); return; }
            if (_awaitingTap)
            {
                PlayUiSound(_sndClick);
                _awaitingTap = false;
                _player.Advance();
            }
        }

        // The hotspot under a pointer — topmost (last-placed) first; null if none.
        // Works from the EVENT position (not Input.mousePosition, which is dead in
        // the Device Simulator / touch). Both the pointer and each actor's real
        // on-screen rect are normalized to 0..1 top-left, so it's independent of
        // pixel scale and aspect (and panel-vs-canvas coordinate differences).
        private System.Action HotspotAt(Vector2 panelPos, float panelW, float panelH)
        {
            if (_renderer == null || panelW <= 0f || panelH <= 0f) return null;
            float nx = panelPos.x / panelW, ny = panelPos.y / panelH; // UITK: top-left, y-down
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
