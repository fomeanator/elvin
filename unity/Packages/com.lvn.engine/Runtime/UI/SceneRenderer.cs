using System;
using System.Collections.Generic;
using Lvn.Content;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// The scene seam: everything VnStage asks of "the thing that draws the
    /// background, actors and camera". Two interchangeable renderers implement
    /// it — the UI Toolkit path (BackgroundLayer + ActorLayer + CameraRig) and
    /// the uGUI Canvas path (WorldStage) — so the stage logic stays renderer-
    /// agnostic without a hand-written <c>if (UseCanvasScene)</c> at every call
    /// site. Path-specific behaviour differences live INSIDE the matching
    /// implementation, where they are visible and testable.
    /// </summary>
    internal interface ISceneRenderer
    {
        // ── background ──
        void SetBackground(Sprite sprite);
        /// <summary>Reset the backdrop on a stage wipe. The UITK path clears its
        /// colour layer; the Canvas path keeps its own black board (its historical
        /// behaviour — the next chapter's bg paints over it).</summary>
        void ClearBackground();

        // ── actors ──
        /// <summary>Create + place an actor BEFORE its art has loaded, so the
        /// slot exists for hit-testing/animation immediately. The UITK path
        /// applies placement and art together, so this is a no-op there.</summary>
        void PlaceActor(string id, Placement placement);
        /// <summary>Apply the actor's final state (art layers + placement).
        /// <paramref name="layerDefs"/> (optional, catalog path) carries each
        /// layer's bone metadata — parent joint, pivot, spring.</summary>
        void ApplyActor(string id, IReadOnlyList<Sprite> layers, Placement placement, Action onClick,
            IReadOnlyList<string> layerIds, IReadOnlyList<Vector4> layerRects,
            IReadOnlyList<SpriteCatalog.ResolvedLayer> layerDefs = null);
        /// <summary>The actor's on-screen rect, normalized 0..1 with a top-left
        /// origin — for manual hotspot hit-testing. Null when this renderer does
        /// its own picking (UITK) or the actor doesn't exist.</summary>
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
        /// null tears it down. Only the Canvas path films sets — the UI Toolkit
        /// path keeps flat backgrounds, so a script using `bg3d` degrades to the
        /// background it already had rather than breaking.</summary>
        void Set3DBackdrop(GameObject prefab);
        /// <summary>Move the set's camera — position, look angles, field of view.
        /// Any argument left null keeps its value; seconds > 0 glides.</summary>
        void Frame3D(float? x, float? y, float? z, float? pitch, float? yaw, float? fov, float seconds);
        /// <summary>Force whether the standing set is filmed every frame.</summary>
        void Set3DLive(bool live);

        // ── сцена, собираемая из скрипта ──
        /// <summary>Пустая сцена под `o3d`/`light` — 3D без готового набора.</summary>
        void Build3D();
        /// <summary>Тело сцены: примитив, модель или плоская фигура.</summary>
        bool Body3D(string id, in Lvn.UI.World.Lvn3DBackdrop.Body body);
        void RemoveBody3D(string id);
        /// <summary>Тело реагирует на нажатие переходом на метку.</summary>
        void SetBody3DClick(string id, string label);
        /// <summary>Во что попали в кадре 3D-сцены (точка — доля кадра).</summary>
        string Pick3D(Vector2 viewport);
        /// <summary>Показать/скрыть числа сцены на устройстве.</summary>
        void Stats3D(bool on);
        /// <summary>Глубина резкости: фокус и его толщина в метрах, сила.</summary>
        void Dof3D(float? focus, float? range, float? power);

        void Bloom3D(float? power, float? threshold, float? knee);
        void Shadows3D(float meters);

        /// <summary>Тональная компрессия кадра набора (`bg3d tone= exposure=`).</summary>
        void Tone3D(Lvn.UI.World.Lvn3DPostStack.Tone? tone, float? exposure, float? saturation, float? contrast, float? dither, float? knee, float? white);
        /// <summary>Открыть кадр 3D-сцены, если он придержан на время постройки.</summary>
        void Reveal3DIfHeld();
        /// <summary>Строится ли сцена прямо сейчас (кадр закрыт).</summary>
        bool Building3D { get; }
        /// <summary>Свет и атмосфера: sun / fill / point / fog / sky.</summary>
        void Light3D(string kind, string id, Vector2? angle, Vector3? pos,
            Color? color, float? power, float? range, float? near, float? far,
            Color? top, Color? bottom, bool off, float dur, float flicker);

        /// <summary>Дыхание камеры 3D-набора: амплитуда в градусах (0 —
        /// выключить) и примерные циклы в секунду.</summary>
        void Set3DSway(float? amplitude, float? speed);

        /// <summary>Real gaussian blur of the scene frame, when this renderer
        /// can do one (Canvas path + built-in pipeline + a camera). Returns
        /// false → the stage falls back to the FxLayer veil imitation.</summary>
        bool TryBlur(float strength01, float seconds);

        /// <summary>The `fx` multi-effect stack (vignette/grain/bloom/…): same
        /// camera hook as TryBlur. False → no camera, the op is a no-op.</summary>
        bool TryFx(Newtonsoft.Json.Linq.JObject cmd);

        /// <summary>Погасить все полнокадровые эффекты немедленно — граница сцены.</summary>
        void ClearFx();

        /// <summary>Осмотр 3D-набора удержанием: сдвиг взгляда в градусах.</summary>
        void Look3D(float dPitch, float dYaw);
        /// <summary>Вернуть взгляд к авторскому кадру.</summary>
        void LookReset3D();
        /// <summary>Ходьба по набору: включение и вектор джойстика.</summary>
        void SetWalk3D(bool on);
        void WalkStick3D(Vector2 v);
        /// <summary>Текущий ракурс строкой — для подписи в отладке.</summary>
        string Camera3DInfo();
        /// <summary>Есть ли что осматривать (стоит ли набор).</summary>
        bool Has3DSet { get; }

        /// <summary>Спрайтовые эффекты актёра (op `sfx`: обводка/свечение/
        /// растворение). False → путь без канвас-актёров, no-op.</summary>
        bool TrySpriteFx(string id, Newtonsoft.Json.Linq.JObject cmd);

        /// <summary>Destroy engine-side objects that OUTLIVE the UI panel (the
        /// Canvas scene's GameObjects). UITK elements die with their panel, so
        /// that path is a no-op. Called on stage disable before a rebuild.</summary>
        void Teardown();
    }

    /// <summary>The UI Toolkit scene: a colour/sprite background layer and an
    /// actor layer inside a "vn-world" element, moved by a CameraRig.</summary>
    internal sealed class UitkSceneRenderer : ISceneRenderer
    {
        private readonly BackgroundLayer _bg;
        private readonly ActorLayer _actors;
        private readonly CameraRig _camera;

        public UitkSceneRenderer(BackgroundLayer bg, ActorLayer actors, CameraRig camera)
        {
            _bg = bg;
            _actors = actors;
            _camera = camera;
        }

        public void SetBackground(Sprite sprite) => _bg.SetSprite(sprite);
        public void ClearBackground() => _bg.SetColor(Color.clear);

        public void PlaceActor(string id, Placement placement) { /* placement applies with the art */ }

        public void ApplyActor(string id, IReadOnlyList<Sprite> layers, Placement placement, Action onClick,
            IReadOnlyList<string> layerIds, IReadOnlyList<Vector4> layerRects,
            IReadOnlyList<SpriteCatalog.ResolvedLayer> layerDefs = null)
            => _actors.Apply(id, layers, placement, onClick, layerIds, layerRects, layerDefs);

        public Rect? ActorScreenRect(string id) => _actors.ScreenRect(id); // drag/drop hit-testing

        public void RemoveAll() => _actors.RemoveAll();

        public void SetFrames(string id, Dictionary<string, Dictionary<string, Sprite>> frames) => _actors.SetFrames(id, frames);
        public void EnsureIdle(string id, LvnAnim idle) => _actors.EnsureIdle(id, idle);
        public void EnsureBlink(string id, LvnAnim blink) => _actors.EnsureBlink(id, blink);
        public void PlayGesture(string id, LvnAnim gesture, LvnAnim idle) => _actors.PlayGesture(id, gesture, idle);
        public void PlayAnim(string id, string channel, LvnAnim anim) => _actors.PlayAnim(id, channel, anim);
        public void PlayAnimQueued(string id, string channel, LvnAnim anim) => _actors.PlayAnimQueued(id, channel, anim);
        public void StopAnim(string id, string target) => _actors.StopAnim(id, target);
        public void Talk(string id, LvnAnim talk, bool on) => _actors.Talk(id, talk, on);
        public void HighlightSpeaker(string who) => _actors.HighlightSpeaker(who);

        public void Shake(float amplitude, float seconds) => _camera.Shake(amplitude, seconds);
        public void Zoom(float factor, float seconds) => _camera.Zoom(factor, seconds);
        public void Pan(float x, float y, float seconds) => _camera.Pan(x, y, seconds);
        public void ResetCamera(float seconds) => _camera.Reset(seconds);

        // The UI Toolkit path has no world to film — a script that stands a 3D
        // set keeps whatever background it had.
        public void Set3DBackdrop(GameObject prefab) { }
        public void Frame3D(float? x, float? y, float? z, float? pitch, float? yaw, float? fov, float seconds) { }
        public void Set3DLive(bool live) { }
        public void Build3D() { }
        public bool Body3D(string id, in Lvn.UI.World.Lvn3DBackdrop.Body body) => false;
        public void RemoveBody3D(string id) { }
        public void SetBody3DClick(string id, string label) { }
        public string Pick3D(Vector2 viewport) => null;
        public void Stats3D(bool on) { }
        public void Dof3D(float? focus, float? range, float? power) { }
        public void Bloom3D(float? power, float? threshold, float? knee) { }
        public void Shadows3D(float meters) { }
        public void Tone3D(Lvn.UI.World.Lvn3DPostStack.Tone? tone, float? exposure, float? saturation, float? contrast, float? dither, float? knee, float? white) { }
        public void Reveal3DIfHeld() { }
        public bool Building3D => false;
        public void Light3D(string kind, string id, Vector2? angle, Vector3? pos,
            Color? color, float? power, float? range, float? near, float? far,
            Color? top, Color? bottom, bool off, float dur, float flicker) { }
        public void Set3DSway(float? amplitude, float? speed) { }

        public bool TryBlur(float strength01, float seconds) => false; // UITK path has no camera frame
        public bool TryFx(Newtonsoft.Json.Linq.JObject cmd) => false;  // same: no camera, no frame hook
        public void ClearFx() { }                                       // нечего гасить: кадром не владеем
        public void Look3D(float dPitch, float dYaw) { }                // UITK-путь без набора
        public void LookReset3D() { }
        public void SetWalk3D(bool on) { }
        public void WalkStick3D(Vector2 v) { }
        public string Camera3DInfo() => "";
        public bool Has3DSet => false;

        public bool TrySpriteFx(string id, Newtonsoft.Json.Linq.JObject cmd) => false; // UITK: слои не Image'ы

        public void Teardown() { /* UITK elements die with the panel root */ }
    }

    /// <summary>The uGUI Canvas scene (WorldStage): 60fps sprites/Spine on a
    /// sibling canvas below the UITK chrome.</summary>
    internal sealed class CanvasSceneRenderer : ISceneRenderer
    {
        private readonly World.WorldStage _scene;

        public CanvasSceneRenderer(World.WorldStage scene) => _scene = scene;

        /// <summary>The stage canvas root — hosts the resume veil (see
        /// VnStage.RestoreSnapshot).</summary>
        public GameObject Root => _scene.Root;

        public void SetBackground(Sprite sprite) => _scene.SetBackgroundSprite(sprite);
        // The canvas keeps its black board for flat art (the next bg paints over
        // it), but a 3D set is a live object being filmed — leaving it standing
        // would show the previous novel's room behind the next one's scene.
        public void ClearBackground() => _scene.Set3DBackdrop(null);

        public void PlaceActor(string id, Placement placement)
            => _scene.ApplyActor(id, null, placement, null, null); // create + place now; art follows

        public void ApplyActor(string id, IReadOnlyList<Sprite> layers, Placement placement, Action onClick,
            IReadOnlyList<string> layerIds, IReadOnlyList<Vector4> layerRects,
            IReadOnlyList<SpriteCatalog.ResolvedLayer> layerDefs = null)
        {
            // onClick is intentionally unused: canvas hotspots are hit-tested by the
            // stage (ActorScreenRect), not by per-element handlers. An actor with no
            // loaded art keeps its PlaceActor slot — nothing to re-apply.
            if (layers != null && layers.Count > 0)
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
        public void Build3D() => _scene.Build3D();
        public bool Body3D(string id, in Lvn.UI.World.Lvn3DBackdrop.Body body) => _scene.Body3D(id, body);
        public void RemoveBody3D(string id) => _scene.RemoveBody3D(id);
        public void SetBody3DClick(string id, string label) => _scene.SetBody3DClick(id, label);
        public string Pick3D(Vector2 viewport) => _scene.Pick3D(viewport);
        public void Stats3D(bool on) => _scene.Stats3D(on);
        public void Dof3D(float? focus, float? range, float? power) => _scene.Dof3D(focus, range, power);
        public void Bloom3D(float? power, float? threshold, float? knee) => _scene.Bloom3D(power, threshold, knee);
        public void Shadows3D(float meters) => _scene.Shadows3D(meters);
        public void Tone3D(Lvn.UI.World.Lvn3DPostStack.Tone? tone, float? exposure, float? saturation, float? contrast, float? dither, float? knee, float? white)
            => _scene.Tone3D(tone, exposure, saturation, contrast, dither, knee, white);
        public void Reveal3DIfHeld() => _scene.Reveal3DIfHeld();
        public bool Building3D => _scene.Building3D;
        public void Light3D(string kind, string id, Vector2? angle, Vector3? pos,
            Color? color, float? power, float? range, float? near, float? far,
            Color? top, Color? bottom, bool off, float dur, float flicker)
            => _scene.Light3D(kind, id, angle, pos, color, power, range, near, far, top, bottom, off, dur, flicker);
        public void Set3DSway(float? amplitude, float? speed) => _scene.Set3DSway(amplitude, speed);

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

        public void ClearFx() => _scene.Fx?.ClearAll();
        public void Look3D(float dPitch, float dYaw) => _scene.Look3D(dPitch, dYaw);
        public void LookReset3D() => _scene.LookReset3D();
        public void SetWalk3D(bool on) => _scene.SetWalk3D(on);
        public void WalkStick3D(Vector2 v) => _scene.WalkStick3D(v);
        public string Camera3DInfo() => _scene.Camera3DInfo();
        public bool Has3DSet => _scene.Has3DSet;

        public bool TrySpriteFx(string id, Newtonsoft.Json.Linq.JObject cmd)
        {
            var actor = _scene.ActorFor(id);
            if (actor == null) return false;
            actor.RememberFx(cmd); // переживёт смену позы и догрузку арта
            World.LvnSpriteFxDriver.Apply(actor.gameObject, cmd);
            return true;
        }

        public void Teardown() => _scene.Dispose(); // the canvas GO survives the panel — destroy it
    }
}
