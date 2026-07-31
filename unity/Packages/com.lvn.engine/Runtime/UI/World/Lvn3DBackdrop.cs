using System.Collections.Generic;
using UnityEngine;

namespace Lvn.UI.World
{
    /// <summary>
    /// A live 3D set standing in for the flat background: the scene's art is a
    /// prefab (a room, a street, a car interior), a private camera films it, and
    /// the frame becomes the backdrop texture the stage already knows how to
    /// show. So one built set replaces a folder of painted angles — the script
    /// moves the camera instead of asking an artist for another picture.
    ///
    /// <para>Why a render texture rather than pointing the main camera at the
    /// set: everything above the background — characters, dialogue, the whole
    /// effect stack — is 2D drawn on the stage canvas, and the canvas renders
    /// THROUGH the main camera (see <see cref="WorldStage"/>). Filming the set
    /// separately keeps that arrangement untouched, so 3D backdrops cost the
    /// rest of the pipeline nothing and `fx`/`blur` still grade the whole
    /// frame.</para>
    ///
    /// <para>The set is instantiated far from the origin (see <see cref="Far"/>)
    /// so the main camera can never catch it in frame — no layer bookkeeping,
    /// no project setup the game has to remember.</para>
    /// </summary>
    public sealed class Lvn3DBackdrop : MonoBehaviour
    {
        /// <summary>Where sets are built: far enough that nothing else sees them.</summary>
        private static readonly Vector3 Far = new Vector3(0f, -10000f, 0f);

        /// <summary>Во сколько раз крупнее экрана снимается НЕПОДВИЖНЫЙ кадр.
        ///
        /// <para>Ровно 2, и это принципиально: при показе кадр ужимается
        /// билинейным фильтром, а тот усредняет ЧЕТЫРЕ соседних тексела. Только
        /// при целом двукратном масштабе эта четвёрка ложится точно на один
        /// экранный пиксель — получается честное усреднение. На дробном (1.5)
        /// фильтр берёт соседей вразнобой, и часть лесенки доживает до экрана:
        /// именно это и было видно на устройстве.</para>
        ///
        /// Для неподвижного кадра цена — один рендер, не каждый кадр.</summary>
        private const float Supersample = 2f;

        /// <summary>Сглаживание, пока камера едет: тайловые мобильные GPU берут
        /// за 4x порядка 10–15% кадра — за время проезда это приемлемо.</summary>
        private const int MovingSamples = 4;

        private Camera _cam;
        private RenderTexture _rt;
        private GameObject _set;
        private Lvn3DSetEnv _env;      // the set's own sky/fog/ambient, if it brought any
        private readonly List<Material> _snapshotMaterials = new List<Material>();

        // Current framing, and where a tween is taking it.
        private Vector3 _pos, _posTo;
        private Vector3 _rot, _rotTo;   // euler: pitch (x), yaw (y)
        private float _fov = 60f, _fovTo = 60f;
        private float _speed;           // 1/seconds; 0 = snap
        private bool _live;             // the set animates: film every frame
        private Vector2 _echoRot;       // camera-rig shake/pan, as degrees
        private float _echoZoom = 1f;   // camera-rig zoom, as an fov divisor
        private bool _echoLive;         // the rig moved this frame

        /// <summary>The filmed frame — hand it to the stage background.</summary>
        public RenderTexture Texture => _rt;

        /// <summary>Raised when rotation/resizing replaces the frame buffer, so
        /// the RawImage never keeps displaying a released RenderTexture.</summary>
        public event System.Action<RenderTexture> TextureChanged;

        /// <summary>True while a set is loaded and filming.</summary>
        public bool Active => _set != null;

        /// <summary>Stand up a backdrop renderer that lives as long as
        /// <paramref name="owner"/> does.
        ///
        /// <para>The backdrop is a ROOT object on purpose. Parenting it under the
        /// stage canvas looks tidier and is quietly fatal: a canvas scales its
        /// children to the screen, so the set and its camera would be resized by
        /// whatever device the game runs on — the same shot framed one way in the
        /// editor and another on a phone, usually as an empty sky. Instead the
        /// owner carries a keeper that tears the backdrop down with it, which is
        /// what parenting was for.</para></summary>
        public static Lvn3DBackdrop Ensure(Transform owner)
        {
            var go = new GameObject("lvn-3d-backdrop");
            var backdrop = go.AddComponent<Lvn3DBackdrop>();
            if (owner != null) owner.gameObject.AddComponent<Keeper>().backdrop = backdrop;
            return backdrop;
        }

        /// <summary>Rides on the stage and takes the backdrop down with it.</summary>
        private sealed class Keeper : MonoBehaviour
        {
            public Lvn3DBackdrop backdrop;
            private void OnDestroy()
            {
                if (backdrop != null) Kill(backdrop.gameObject);
            }
        }

        /// <summary>Build <paramref name="prefab"/> as the current set, replacing
        /// any previous one. Passing null tears the set down (the stage then goes
        /// back to flat backgrounds).</summary>
        public void SetSet(GameObject prefab)
        {
            if (_set != null)
            {
                if (_env != null) { _env.Restore(); _env = null; }
                Kill(_set);
                _set = null;
            }
            ReleaseSnapshotMaterials();
            if (prefab == null)
            {
                Release();
                return;
            }
            EnsureCamera();
            // Parent the set to this component (itself a root object): a set left
            // loose in the scene outlives the stage that stood it, and the next
            // novel opens with the previous one's room still being filmed.
            _set = Instantiate(prefab, transform);
            _set.transform.localPosition = Far;
            _set.transform.localRotation = Quaternion.identity;
            _set.name = "lvn-3d-set:" + prefab.name;

            // Does anything in this set MOVE? A still room is filmed once and
            // costs nothing after that; a set with swaying trees, running water
            // or particles has to be filmed every frame like any 3D game. Decide
            // by what the set actually contains, so an author never has to
            // declare it — and never gets a frozen waterfall by forgetting to.
            // A set brings its own air (sky, fog, ambient bounce). Without it a
            // stylised kit renders flat and grey — the geometry and shaders are
            // fine, the atmosphere simply is not project-wide state we may keep.
            _env = _set.GetComponent<Lvn3DSetEnv>();
            if (_env != null) _env.Apply();

            _live = SetAnimates(_set);
            _cam.enabled = _live;

            // A visual-novel backdrop is a composed shot, even while its camera
            // glides to the next mark. Time-driven vendor wind otherwise moves
            // alpha-cutout leaves in BOTH the colour and ShadowCaster passes on
            // every moving frame. On a mobile shadow map that reads as crawling,
            // flickering leaf shadows. Clone only the few wind materials used by
            // this instance and pin them to one pose; source assets and other
            // scenes keep their animation.
            if (_env == null || _env.freezeShaderWind)
                FreezeSnapshotShaderWind(_set);

            // Набор ставится один раз и дальше НЕ двигается — значит его
            // геометрию можно слепить в общие буферы. Без этого каждый камень
            // и каждая ёлка уходят отдельным вызовом отрисовки: у лесного
            // набора это 450 вызовов на кадр, отсюда рывки по секунде.
            try
            {
                StaticBatchingUtility.Combine(_set);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[bg3d] набор не удалось слепить: " + e.Message);
            }

            // A set authored around its own origin lands around Far; the camera
            // frames it in the set's local space, so authors keep thinking in
            // ordinary coordinates.
            Apply(); // one frame, so the set appears immediately
        }

        /// <summary>True when the set has anything that moves on its own.</summary>
        private static bool SetAnimates(GameObject set)
        {
            // Animators and particles are named, not typed: the engine package
            // deliberately does not reference Unity's Animation and ParticleSystem
            // modules (a game that ships neither should not have to carry them),
            // so asking for the type by name keeps this check dependency-free.
            foreach (var c in set.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                switch (c.GetType().Name)
                {
                    case "Animator":
                    case "Animation":
                    case "ParticleSystem":
                    case "VisualEffect":
                        return true;
                }
                // Scrolling UVs, vertex-animated foliage and the like live in
                // scripts the engine can't name; anything the set author attached
                // beyond a plain renderer counts as motion. The engine's own
                // carry-on components (the set's sky/fog card) are NOT motion —
                // counting them filmed every still set per-frame, for nothing.
                if (c is Lvn3DSetEnv) continue;
                if (c is MonoBehaviour) return true;
            }
            return false;
        }

        /// <summary>Pin time-driven vegetation and its shadow caster to one pose.
        /// One clone is shared by every renderer that used the same source
        /// material, so a forest with thousands of instances creates only a
        /// handful of materials.</summary>
        private void FreezeSnapshotShaderWind(GameObject set)
        {
            ReleaseSnapshotMaterials();
            var replacements = new Dictionary<Material, Material>();
            int bindings = 0;
            foreach (var renderer in set.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    var source = materials[i];
                    if (source == null ||
                        (!source.HasProperty("_CUSTOMWIND") &&
                         !source.HasProperty("_CUSTOMWIND1")))
                        continue;

                    if (!replacements.TryGetValue(source, out var frozen))
                    {
                        frozen = Instantiate(source);
                        frozen.name = source.name + " (snapshot)";
                        // Keep the keyword variant the bundle was built with.
                        // Disabling a shader_feature at runtime is unsafe on
                        // Android: Unity may have stripped that unused variant.
                        // Zero speed keeps the authored bend but removes _Time
                        // from subsequent frames. Strength is the fallback for
                        // other compatible vegetation shaders.
                        if (frozen.HasProperty("_WindMovement"))
                            frozen.SetFloat("_WindMovement", 0f);
                        else if (frozen.HasProperty("_WindStrength"))
                            frozen.SetFloat("_WindStrength", 0f);
                        replacements.Add(source, frozen);
                        _snapshotMaterials.Add(frozen);
                    }
                    materials[i] = frozen;
                    changed = true;
                    bindings++;
                }
                if (changed) renderer.sharedMaterials = materials;
            }
            if (replacements.Count > 0)
                Debug.Log($"[bg3d-shadow] frozen wind in {replacements.Count} material(s), " +
                          $"{bindings} renderer binding(s)");
        }

        private void ReleaseSnapshotMaterials()
        {
            foreach (var material in _snapshotMaterials) Kill(material);
            _snapshotMaterials.Clear();
        }

        /// <summary>What the 2D camera rig is doing, so the set moves with it:
        /// a shake becomes a real jolt of the shot, a pan a turn of the head, a
        /// zoom a change of focal length. <paramref name="offset"/> is in canvas
        /// units and <paramref name="width"/> is the canvas' logical width, so
        /// the same op reads the same on every screen.</summary>
        public void Echo(Vector2 offset, float zoom, float width)
        {
            if (width <= 0f) return;
            // The world swings the way the 2D layer slid, scaled by the shot's
            // own field of view — so both layers read as one filmed scene.
            // Horizontal motion maps against the HORIZONTAL fov (the camera
            // stores the vertical one), vertical against the logical height.
            float sw = Mathf.Max(1, Screen.width), sh = Mathf.Max(1, Screen.height);
            float hfov = 2f * Mathf.Atan(Mathf.Tan(_fov * 0.5f * Mathf.Deg2Rad) * sw / sh) * Mathf.Rad2Deg;
            float height = width * sh / sw;
            // Content shifted right (+x) → the camera turns LEFT to carry the
            // world along; shifted up (+y) → the camera dips DOWN. Unity euler:
            // +pitch looks down, +yaw turns right.
            var rot = new Vector2(offset.y / height * _fov, -offset.x / width * hfov);
            bool moved = (rot - _echoRot).sqrMagnitude > 0.000001f
                         || Mathf.Abs(zoom - _echoZoom) > 0.0001f;
            _echoRot = rot;
            _echoZoom = zoom <= 0f ? 1f : zoom;
            if (!moved && !_echoLive) return;
            _echoLive = rot.sqrMagnitude > 0.000001f || Mathf.Abs(_echoZoom - 1f) > 0.0001f;
            Apply();      // re-film with the new offset; a still shot pays only while it moves
        }

        /// <summary>Force the filming mode instead of letting the set decide:
        /// `bg3d live=1` films every frame (a set whose motion the engine can't
        /// detect — a shader that scrolls water, say), `live=0` pins it to a
        /// still shot even if it could animate.</summary>
        public void SetLive(bool live)
        {
            _live = live;
            EnsureCamera();
            if (_cam != null) _cam.enabled = live && _cam.targetTexture != null;
            if (live) enabled = true;
        }

        /// <summary>Frame the shot. Coordinates are the SET's own — as authored in
        /// the prefab — so a script says "stand at the door" the same way the
        /// artist built it. Any argument left null keeps its current value;
        /// <paramref name="seconds"/> above zero glides instead of cutting.</summary>
        public void Frame(float? x, float? y, float? z,
                          float? pitch, float? yaw, float? fov, float seconds)
        {
            EnsureCamera();
            _posTo = new Vector3(x ?? _posTo.x, y ?? _posTo.y, z ?? _posTo.z);
            _rotTo = new Vector3(pitch ?? _rotTo.x, yaw ?? _rotTo.y, 0f);
            _fovTo = fov ?? _fovTo;
            _speed = seconds > 0f ? 1f / seconds : 0f;
            if (_speed <= 0f)
            {
                _pos = _posTo; _rot = _rotTo; _fov = _fovTo;
                Apply();
            }
            enabled = true;
        }

        /// <summary>Tear the set down and free the frame buffer.</summary>
        public void Release()
        {
            if (_set != null)
            {
                if (_env != null) { _env.Restore(); _env = null; }
                Kill(_set);
                _set = null;
            }
            ReleaseSnapshotMaterials();
            if (_cam != null) _cam.enabled = false;
            if (_rt != null)
            {
                if (_cam != null) _cam.targetTexture = null;
                _rt.Release();
                Kill(_rt);
                _rt = null;
                TextureChanged?.Invoke(null);
            }
        }

        /// <summary>Destroy that works outside play mode too: in the editor a
        /// plain Destroy only marks the object and warns, which turns an ordinary
        /// teardown into a test failure.</summary>
        private static void Kill(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

        private void EnsureCamera()
        {
            // The target is ensured OUTSIDE the create-once guard: a painted `bg`
            // tears the backdrop down (Release kills the frame buffer) and the next
            // `bg3d` arrives with the camera still alive. Returning early here left
            // that camera without a targetTexture — and an enabled camera with no
            // target renders straight to the SCREEN, painting the set over every
            // sprite on the stage. One line of ordering, a whole battle invisible.
            if (_cam != null) { EnsureTarget(); return; }
            var go = new GameObject("lvn-3d-camera");
            go.transform.SetParent(transform, false);
            _cam = go.AddComponent<Camera>();
            _cam.clearFlags = CameraClearFlags.Skybox;
            _cam.backgroundColor = Color.black;
            _cam.nearClipPlane = 0.05f;
            _cam.farClipPlane = 500f;
            // Filming only: never contribute to the on-screen frame directly and
            // never listen — the stage's own camera stays the one that hears.
            var listener = go.GetComponent<AudioListener>();
            if (listener != null) Kill(listener);
            // Never renders on its own — see Shoot(). Unity only auto-renders
            // ENABLED cameras, so a disabled one is free until asked.
            _cam.enabled = false;
            EnsureTarget();
        }

        private void EnsureTarget()
        {
            // No graphics device (headless server, batch tests) — there is nothing
            // to film into and asking for a frame buffer would take the process
            // down. The set still stands and framing still records, so a headless
            // run stays honest about state without touching the GPU.
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;

            // Сглаживание задаём САМИ, а не спрашиваем QualitySettings: на
            // Android активен уровень «Medium», где antiAliasing=0 — набор
            // снимался вообще без сглаживания, отсюда лесенка на каждом ребре
            // и мерцание листвы при проездах камеры.
            //
            // Кадр новеллы статичен между движениями камеры, поэтому неподвижный
            // снимаем С ЗАПАСОМ разрешения и ужимаем при показе (суперсэмплинг):
            // платим один раз за кадр, а качество выше любого MSAA. Пока камера
            // едет — обычный размер с MSAA, чтобы не жечь батарею каждый кадр.
            bool moving = _live || _speed > 0f;
            float ss = moving ? 1f : Supersample;
            int aa = moving ? MovingSamples : 1;
            int w = Mathf.Max(256, Mathf.RoundToInt(Screen.width * ss));
            int h = Mathf.Max(256, Mathf.RoundToInt(Screen.height * ss));
            if (_rt != null && _rt.width == w && _rt.height == h && _rt.antiAliasing == aa)
            {
                // A camera without a target draws directly to the display. Even
                // if another component cleared it, restore the invariant here.
                if (_cam.targetTexture != _rt) _cam.targetTexture = _rt;
                return;
            }
            if (_rt != null)
            {
                _cam.targetTexture = null;
                _rt.Release();
                Kill(_rt);
            }
            _rt = new RenderTexture(w, h, 24, RenderTextureFormat.Default)
            {
                name = "lvn-3d-frame",
                antiAliasing = aa,
                filterMode = FilterMode.Bilinear, // мягко ужимаем крупный кадр
            };
            _cam.targetTexture = _rt;
            Debug.Log($"[bg3d-aa] буфер {_rt.width}×{_rt.height} MSAA×{_rt.antiAliasing} " +
                      $"(экран {Screen.width}×{Screen.height}, живой={_live}, едет={_speed > 0f})");
            TextureChanged?.Invoke(_rt);
            Shoot(); // the buffer is fresh and empty — fill it at once
        }

        private void Apply()
        {
            if (_cam == null) return;
            _cam.transform.localPosition = Far + _pos;
            _cam.transform.localRotation =
                Quaternion.Euler(_rot.x + _echoRot.x, _rot.y + _echoRot.y, 0f);
            _cam.fieldOfView = Mathf.Clamp(_fov / _echoZoom, 5f, 120f);
            Shoot();
        }

        /// <summary>Film ONE frame. A visual novel's shot is static between
        /// camera moves — re-filming the identical picture sixty times a second
        /// is the whole cost of the feature for none of the benefit. So the
        /// camera stays disabled and takes a single frame whenever the framing
        /// changes; a still shot then costs nothing at all.</summary>
        private void Shoot()
        {
            if (_cam == null || _rt == null || _cam.targetTexture != _rt)
            {
                if (_cam != null) _cam.enabled = false;
                return;
            }
            // A living set is already being filmed by Unity every frame —
            // rendering again here would double the cost of the feature.
            if (_cam.enabled) return;
            _cam.Render();
        }

        private void Update()
        {
            if (_cam == null) return;
            EnsureTarget(); // rotation/resize and the screen-target safety invariant
            if (_speed <= 0f)
            {
                // A living set keeps filming (Unity renders the enabled camera
                // itself); a still one sleeps until the next `bg3d` wakes it.
                if (!_live) enabled = false;
                return;
            }
            float step = _speed * Time.unscaledDeltaTime;
            _pos = Vector3.Lerp(_pos, _posTo, Mathf.Clamp01(step));
            _rot = Vector3.Lerp(_rot, _rotTo, Mathf.Clamp01(step));
            _fov = Mathf.Lerp(_fov, _fovTo, Mathf.Clamp01(step));
            Apply();

            if ((_pos - _posTo).sqrMagnitude < 0.0001f &&
                (_rot - _rotTo).sqrMagnitude < 0.0001f &&
                Mathf.Abs(_fov - _fovTo) < 0.01f)
            {
                _pos = _posTo; _rot = _rotTo; _fov = _fovTo;
                Apply();
                _speed = 0f;
                enabled = false; // arrived — stop burning frames
            }
        }

        private void OnDestroy() => Release();
    }
}
