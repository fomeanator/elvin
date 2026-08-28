using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    public sealed partial class VnStage
    {
        /// <summary>
        /// ТОЧКА ЭКРАНА В КООРДИНАТАХ СЦЕНЫ — доли 0..1 от её размера.
        ///
        /// <para>Сцена мыслит долями: актёр стоит на 0.35 ширины, зона клика —
        /// прямоугольник в долях, точка сброса — тоже доля. Экран мыслит
        /// пикселями. Перевод между ними — деление на размер панели — писали по
        /// месту ШЕСТЬ раз: три в перетаскивании, один в начале жеста, один в
        /// зонах клика, один в разборе попадания.</para>
        ///
        /// <para>Две копии из шести делили БЕЗ ПРОВЕРКИ размера. Панель бывает
        /// нулевой ровно в тот кадр, когда всё и происходит: первый layout, смена
        /// ориентации, пересборка документа. Деление на ноль даёт не исключение,
        /// а NaN — и объект уезжает в никуда, а зона клика перестаёт ловить
        /// касания. Такое не падает, а «иногда не работает».</para>
        ///
        /// <para>Null означает «сцена ещё не имеет размера»: спрашивающий обязан
        /// проверить, и это честнее, чем вернуть ноль и сделать вид, что точка в
        /// левом верхнем углу.</para>
        /// </summary>
        private Vector2? StagePoint(Vector2 screenPos)
        {
            if (_uiRoot == null) return null;
            float pw = _uiRoot.layout.width, ph = _uiRoot.layout.height;
            if (pw <= 0f || ph <= 0f) return null;
            return new Vector2(screenPos.x / pw, screenPos.y / ph);
        }

        // ── drag & drop ──────────────────────────────────────────────────────
        // The point-and-click inventory verb: `obj … draggable=true
        // on_drop="bag:apple_in_bag pond:apple_lost" [on_drop_miss=label]`.
        // Grab → the object follows the pointer (springs make cloth sway);
        // release over a mapped target → that label runs (the branch hides the
        // item, sets vars, plays its animation — author's script decides);
        // release anywhere else → the object STAYS where it was dropped
        // (per design), optionally firing on_drop_miss.

        private sealed class DragInfo
        {
            public Placement Home;
            public Dictionary<string, string> Drop; // target id → label
            public string MissLabel;
            public bool BoundToScreen = true;       // keep the WHOLE rect on-screen
            public Vector2 Size;                    // measured at drag begin (normalized)
        }
        private readonly Dictionary<string, DragInfo> _draggables = new Dictionary<string, DragInfo>();
        // The actor's last APPLIED placement — the base sticky actor commands merge over.
        private readonly Dictionary<string, Placement> _placements = new Dictionary<string, Placement>();
        private string _dragCandidate, _dragId;
        private Vector2 _dragGrab; // pointer→anchor offset in screen fractions

        /// <summary>Parse the on_drop mapping: <c>"bag:label pond:other"</c>.
        /// Pure — unit-tested; malformed pairs are skipped.</summary>
        internal static Dictionary<string, string> ParseDropMap(string raw)
        {
            var map = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(raw)) return map;
            foreach (var pair in raw.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int c = pair.IndexOf(':');
                if (c <= 0 || c >= pair.Length - 1) continue;
                map[pair.Substring(0, c)] = pair.Substring(c + 1);
            }
            return map;
        }

        private string DraggableAt(Vector2 pos)
        {
            if (_draggables.Count == 0 || _renderer == null) return null;
            if (StagePoint(pos) is not Vector2 np) return null;
            string hit = null; // last (topmost-placed) wins
            foreach (var kv in _draggables)
            {
                var r = _renderer.ActorScreenRect(kv.Key);
                if (r != null && r.Value.Contains(np)) hit = kv.Key;
            }
            return hit;
        }

        private void DragBegin(string id, Vector2 pos)
        {
            if (!_draggables.TryGetValue(id, out var di)) return;
            _dragId = id;
            _suppressTap = true;
            _longPress?.Pause();
            // Захват без проверки размера давал NaN, и объект «прилипал» к
            // курсору со смещением в бесконечность.
            if (StagePoint(pos) is not Vector2 grab) return;
            _dragGrab = new Vector2(grab.x - di.Home.X, grab.y - di.Home.Y);
            var r = _renderer?.ActorScreenRect(id); // rect size for screen bounding
            di.Size = r != null ? new Vector2(r.Value.width, r.Value.height) : Vector2.zero;
            LvnPlayer.Log?.Invoke("drag begin '" + id + "'");
        }

        // Clamp the anchor position so the object's WHOLE rect stays on-screen
        // (drag_bounds=screen, the default). The anchor fractions say how much
        // of the rect hangs on each side of the anchor point.
        private static Vector2 ClampToScreen(Vector2 p, DragInfo di)
        {
            if (!di.BoundToScreen || di.Size.x <= 0f || di.Size.y <= 0f)
                return new Vector2(Mathf.Clamp01(p.x), Mathf.Clamp01(p.y));
            float ax = di.Home.AnchorX, ay = di.Home.AnchorY;
            float minX = ax * di.Size.x, maxX = 1f - (1f - ax) * di.Size.x;
            float minY = ay * di.Size.y, maxY = 1f - (1f - ay) * di.Size.y;
            return new Vector2(
                maxX >= minX ? Mathf.Clamp(p.x, minX, maxX) : 0.5f,
                maxY >= minY ? Mathf.Clamp(p.y, minY, maxY) : 0.5f);
        }

        internal void DragMove(Vector2 pos)
        {
            if (_dragId == null || !_draggables.TryGetValue(_dragId, out var di)) return;
            if (StagePoint(pos) is not Vector2 np) return;
            var p = di.Home;
            var cl = ClampToScreen(new Vector2(np.x - _dragGrab.x, np.y - _dragGrab.y), di);
            p.X = cl.x;
            p.Y = cl.y;
            _renderer?.PlaceActor(_dragId, p);
            _renderer?.ApplyActor(_dragId, null, p, null, null, null); // renderers that place with the art
        }

        private void DragEnd(Vector2 pos)
        {
            var id = _dragId;
            _dragId = null;
            if (id == null || !_draggables.TryGetValue(id, out var di)) return;

            if (StagePoint(pos) is not Vector2 np) return;
            // it stays where it was dropped — remember the spot as the new home
            var clEnd = ClampToScreen(new Vector2(np.x - _dragGrab.x, np.y - _dragGrab.y), di);
            di.Home.X = clEnd.x;
            di.Home.Y = clEnd.y;
            _placements[id] = di.Home; // future actor commands keep the dropped spot

            string label = null;
            foreach (var kv in di.Drop)
            {
                var r = _renderer?.ActorScreenRect(kv.Key);
                if (r != null && r.Value.Contains(np)) { label = kv.Value; break; }
            }
            label ??= di.MissLabel;
            // Log the drop coords in the same 0..1 units the script uses — copy
            // them straight into an `actor x= y=` to pin the object there.
            LvnPlayer.Log?.Invoke("drag end '" + id + "' → x=" + clEnd.x.ToString("0.###")
                + " y=" + clEnd.y.ToString("0.###") + " → " + (label ?? "(stay)"));
            if (label == null || _player == null) return;

            StopWaitingForPlayer();
            _player.GoTo(label);
            _player.Advance();
            AutosaveNow(); // a completed interaction must survive a crash
        }

    }
}
