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

        private Camera _cam;
        private RenderTexture _rt;
        private GameObject _set;

        // Current framing, and where a tween is taking it.
        private Vector3 _pos, _posTo;
        private Vector3 _rot, _rotTo;   // euler: pitch (x), yaw (y)
        private float _fov = 60f, _fovTo = 60f;
        private float _speed;           // 1/seconds; 0 = snap

        /// <summary>The filmed frame — hand it to the stage background.</summary>
        public RenderTexture Texture => _rt;

        /// <summary>True while a set is loaded and filming.</summary>
        public bool Active => _set != null;

        /// <summary>Attach a backdrop renderer to <paramref name="parent"/>.</summary>
        public static Lvn3DBackdrop Ensure(Transform parent)
        {
            var go = new GameObject("lvn-3d-backdrop");
            go.transform.SetParent(parent, false);
            return go.AddComponent<Lvn3DBackdrop>();
        }

        /// <summary>Build <paramref name="prefab"/> as the current set, replacing
        /// any previous one. Passing null tears the set down (the stage then goes
        /// back to flat backgrounds).</summary>
        public void SetSet(GameObject prefab)
        {
            if (_set != null)
            {
                Kill(_set);
                _set = null;
            }
            if (prefab == null)
            {
                Release();
                return;
            }
            EnsureCamera();
            _set = Instantiate(prefab, Far, Quaternion.identity);
            _set.name = "lvn-3d-set:" + prefab.name;
            // A set authored around its own origin lands around Far; the camera
            // frames it in the set's local space, so authors keep thinking in
            // ordinary coordinates.
            _cam.enabled = true;
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
            if (_set != null) { Kill(_set); _set = null; }
            if (_cam != null) _cam.enabled = false;
            if (_rt != null)
            {
                if (_cam != null) _cam.targetTexture = null;
                _rt.Release();
                Kill(_rt);
                _rt = null;
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
            if (_cam != null) return;
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
            EnsureTarget();
        }

        private void EnsureTarget()
        {
            // No graphics device (headless server, batch tests) — there is nothing
            // to film into and asking for a frame buffer would take the process
            // down. The set still stands and framing still records, so a headless
            // run stays honest about state without touching the GPU.
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;

            int w = Mathf.Max(256, Screen.width);
            int h = Mathf.Max(256, Screen.height);
            if (_rt != null && _rt.width == w && _rt.height == h) return;
            if (_rt != null)
            {
                _cam.targetTexture = null;
                _rt.Release();
                Kill(_rt);
            }
            _rt = new RenderTexture(w, h, 24, RenderTextureFormat.Default)
            {
                name = "lvn-3d-frame",
                antiAliasing = QualitySettings.antiAliasing > 0 ? QualitySettings.antiAliasing : 1,
            };
            _cam.targetTexture = _rt;
        }

        private void Apply()
        {
            if (_cam == null) return;
            _cam.transform.position = Far + _pos;
            _cam.transform.rotation = Quaternion.Euler(_rot.x, _rot.y, 0f);
            _cam.fieldOfView = Mathf.Clamp(_fov, 5f, 120f);
        }

        private void Update()
        {
            if (_cam == null) return;
            EnsureTarget(); // a rotated device changes the frame size
            if (_speed <= 0f) return;

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
            }
        }

        private void OnDestroy() => Release();
    }
}
