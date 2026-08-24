using System;
using System.Collections.Generic;
using Lvn.Content;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// The scene seam: everything VnStage asks of "the thing that draws the
    /// background, actors and camera". Одна реализация — uGUI-канвас
    /// (<see cref="CanvasSceneRenderer"/> поверх WorldStage). Шов оставлен
    /// НАМЕРЕННО: он держит логику сцены отдельно от способа рисовать, и
    /// именно по нему когда-то жила вторая реализация на UI Toolkit. Её
    /// снесли — каждую постановочную правку приходилось делать дважды, а
    /// тесты умели зеленеть на мёртвом пути.
    /// </summary>
    internal interface ISceneRenderer
    {
        // ── background ──
        void SetBackground(Sprite sprite);
        /// <summary>Смена фона с растворением прежнего кадра (0 = резко).</summary>
        void SetBackground(Sprite sprite, float crossfadeSeconds);
        /// <summary>Пан по фону: from → to (0=левый край, 1=правый) за seconds.</summary>
        void PanBackground(float from01, float to01, float seconds);
        /// <summary>Reset the backdrop on a stage wipe: the Canvas path keeps
        /// its own black board (its historical behaviour — the next chapter's bg
        /// paints over it).</summary>
        void ClearBackground();

        // ── actors ──
        /// <summary>Create + place an actor BEFORE its art has loaded, so the
        /// slot exists for hit-testing/animation immediately.</summary>
        void PlaceActor(string id, Placement placement);
        /// <summary>Apply the actor's final state (art layers + placement).
        /// <paramref name="layerDefs"/> (optional, catalog path) carries each
        /// layer's bone metadata — parent joint, pivot, spring.</summary>
        void ApplyActor(string id, IReadOnlyList<Sprite> layers, Placement placement, Action onClick,
            IReadOnlyList<string> layerIds, IReadOnlyList<Vector4> layerRects,
            IReadOnlyList<SpriteCatalog.ResolvedLayer> layerDefs = null);
        /// <summary>The actor's on-screen rect, normalized 0..1 with a top-left
        /// origin — for manual hotspot hit-testing. Null when the actor doesn't
        /// exist.</summary>
        Rect? ActorScreenRect(string id);
        void RemoveAll();

        // ── per-actor animation ──
        void SetFrames(string id, Dictionary<string, Dictionary<string, Sprite>> frames);
        void EnsureIdle(string id, LvnAnim idle);
        void EnsureBlink(string id, LvnAnim blink);
        void PlayGesture(string id, LvnAnim gesture, LvnAnim idle);
        void PlayAnim(string id, string channel, LvnAnim anim);
        void PlayAnimQueued(string id, string channel, LvnAnim anim);
        void StopAnim(string id, string target);
        void Talk(string id, LvnAnim talk, bool on);
        void HighlightSpeaker(string who);

        // ── camera ──
        void Shake(float amplitude, float seconds);
        void Zoom(float factor, float seconds);
        void Pan(float x, float y, float seconds);
        void ResetCamera(float seconds);

        // ── 3D set as the background ──
        /// <summary>Stand a built 3D set behind the scene instead of painted art;
        /// null tears it down.</summary>
        void Set3DBackdrop(GameObject prefab);
        /// <summary>Move the set's camera — position, look angles, field of view.
        /// Any argument left null keeps its value; seconds > 0 glides.</summary>
        void Frame3D(float? x, float? y, float? z, float? pitch, float? yaw, float? fov, float seconds);
        /// <summary>Force whether the standing set is filmed every frame.</summary>
        void Set3DLive(bool live);

        /// <summary>Real gaussian blur of the scene frame, when the platform
        /// can do one (built-in pipeline + a camera). Returns false → the stage
        /// falls back to the FxLayer veil imitation.</summary>
        bool TryBlur(float strength01, float seconds);

        /// <summary>The `fx` multi-effect stack (vignette/cinematic/bloom/…): same
        /// camera hook as TryBlur. False → no camera, the op is a no-op.</summary>
        bool TryFx(Newtonsoft.Json.Linq.JObject cmd);

        /// <summary>Спрайтовые эффекты актёра (op `sfx`: обводка/свечение/
        /// растворение). False → у актёра нет материала, op молчит.</summary>
        bool TrySpriteFx(string id, Newtonsoft.Json.Linq.JObject cmd);

        /// <summary>Destroy engine-side objects that OUTLIVE the UI panel (the
        /// Canvas scene's GameObjects). Called on stage disable before a
        /// rebuild.</summary>
        void Teardown();
    }

    /// <summary>The uGUI Canvas scene (WorldStage): 60fps sprites/Spine on a
    /// sibling canvas below the UI Toolkit chrome (окно, выборы, меню).</summary>
    internal sealed class CanvasSceneRenderer : ISceneRenderer
    {
        private readonly World.WorldStage _scene;

        public CanvasSceneRenderer(World.WorldStage scene) => _scene = scene;

        /// <summary>The stage canvas root — hosts the resume veil (see
        /// VnStage.RestoreSnapshot).</summary>
        public GameObject Root => _scene.Root;

        public void SetBackground(Sprite sprite) => _scene.SetBackgroundSprite(sprite);
        public void SetBackground(Sprite sprite, float crossfadeSeconds)
            => _scene.SetBackgroundSprite(sprite, crossfadeSeconds);
        public void PanBackground(float from01, float to01, float seconds)
            => _scene.PanBackground(from01, to01, seconds);
        // The canvas keeps its black board for flat art (the next bg paints over
        // it), but a 3D set is a live object being filmed — leaving it standing
        // would show the previous novel's room behind the next one's scene.
        public void ClearBackground() => _scene.Set3DBackdrop(null);

        public void PlaceActor(string id, Placement placement)
            // Build and place the slot NOW, but do not reveal it yet. The art is
            // asynchronous: starting a fade on this empty slot used to consume
            // the whole entrance before the sprite arrived; ApplyActor below
            // then saw an already-visible actor and snapped the image in.
            => _scene.PlaceActor(id, placement);

        /// <summary>Spine needs a live parent slot before its runtime skeleton is
        /// built. Unlike sprite art, its own bridge owns the reveal.</summary>
        public void PlaceSpineActor(string id, Placement placement)
            => _scene.ApplyActor(id, null, placement, null, null);

        public void ApplyActor(string id, IReadOnlyList<Sprite> layers, Placement placement, Action onClick,
            IReadOnlyList<string> layerIds, IReadOnlyList<Vector4> layerRects,
            IReadOnlyList<SpriteCatalog.ResolvedLayer> layerDefs = null)
        {
            // onClick is intentionally unused: canvas hotspots are hit-tested by the
            // stage (ActorScreenRect), not by per-element handlers. An actor with no
            // loaded art keeps its PlaceActor slot. Hides still run their exit;
            // a re-show may omit URLs and reuse layers already on the actor.
            if (!placement.Show || layers != null && layers.Count > 0 || _scene.HasActorArt(id))
                _scene.ApplyActor(id, layers, placement, layerIds, layerRects, layerDefs);
        }

        /// <summary>The actor's slot RectTransform (placement target).</summary>
        public RectTransform SlotFor(string id) => _scene.ActorFor(id)?.Slot;

        /// <summary>The actor's animated rig — a runtime Spine skeleton mounts
        /// here so anim/move/alpha channels drive it like sprite layers.</summary>
        public RectTransform RigFor(string id) => _scene.ActorFor(id)?.Rig;

        /// <summary>Static placement opacity for a mounted guest.</summary>
        public void SetActorOpacity(string id, float a) => _scene.ActorFor(id)?.SetBaseOpacity(a);

        public Rect? ActorScreenRect(string id)
        {
            var a = _scene.ActorFor(id);
            if (a == null || a.Slot == null) return null;
            float sw = Screen.width, sh = Screen.height;
            if (sw <= 0f || sh <= 0f) return null;
            var c = new Vector3[4];
            a.Slot.GetWorldCorners(c);
            // Overlay canvas → corners already ARE screen pixels; camera canvas
            // (the real-blur path) → corners are world-space points on the
            // canvas plane and must be projected. Getting this wrong silently
            // kills every obj hotspot, so resolve it per-canvas, not globally.
            var cam = a.Slot.GetComponentInParent<Canvas>()?.worldCamera;
            if (cam != null)
                for (int i = 0; i < 4; i++)
                    c[i] = cam.WorldToScreenPoint(c[i]);
            float left = Mathf.Min(c[0].x, c[2].x) / sw, right = Mathf.Max(c[0].x, c[2].x) / sw;
            float top = 1f - Mathf.Max(c[0].y, c[2].y) / sh, bot = 1f - Mathf.Min(c[0].y, c[2].y) / sh;
            return Rect.MinMaxRect(left, top, right, bot); // normalized, top-left origin (y-up source)
        }

        public void RemoveAll() => _scene.RemoveAll();

        public void SetFrames(string id, Dictionary<string, Dictionary<string, Sprite>> frames) => _scene.SetFrames(id, frames);
        public void EnsureIdle(string id, LvnAnim idle) => _scene.EnsureIdle(id, idle);
        public void EnsureBlink(string id, LvnAnim blink) => _scene.EnsureBlink(id, blink);
        public void PlayGesture(string id, LvnAnim gesture, LvnAnim idle) => _scene.PlayGesture(id, gesture, idle);
        public void PlayAnim(string id, string channel, LvnAnim anim) => _scene.PlayAnim(id, channel, anim);
        public void PlayAnimQueued(string id, string channel, LvnAnim anim) => _scene.PlayAnimQueued(id, channel, anim);
        public void StopAnim(string id, string target) => _scene.StopAnim(id, target);
        public void Talk(string id, LvnAnim talk, bool on) => _scene.Talk(id, talk, on);
        public void HighlightSpeaker(string who) => _scene.HighlightSpeaker(who);

        public void Set3DBackdrop(GameObject prefab) => _scene.Set3DBackdrop(prefab);
        public void Frame3D(float? x, float? y, float? z, float? pitch, float? yaw, float? fov, float seconds)
            => _scene.Frame3D(x, y, z, pitch, yaw, fov, seconds);
        public void Set3DLive(bool live) => _scene.Set3DLive(live);

        public void Shake(float amplitude, float seconds) => _scene.Shake(amplitude, seconds);
        public void Zoom(float factor, float seconds) => _scene.Zoom(factor, seconds);
        public void Pan(float x, float y, float seconds) => _scene.Pan(x, y, seconds);
        public void ResetCamera(float seconds) => _scene.ResetCamera(seconds);

        public bool TryBlur(float strength01, float seconds)
        {
            if (_scene.Blur == null) return false;
            _scene.Blur.FadeTo(strength01, seconds);
            return true;
        }

        public bool TryFx(Newtonsoft.Json.Linq.JObject cmd)
        {
            if (_scene.Fx == null) return false;
            _scene.Fx.Apply(cmd);
            return true;
        }

        public bool TrySpriteFx(string id, Newtonsoft.Json.Linq.JObject cmd)
            // Живому актёру — сразу; ещё строящемуся — в очередь до рождения
            // (раньше эффект молча терялся, и герой вспыхивал без силуэта).
            => _scene.ApplySpriteFx(id, cmd);

        public void Teardown() => _scene.Dispose(); // the canvas GO survives the panel — destroy it
    }
}
