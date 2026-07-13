using System.IO;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Lvn.UI
{
    /// <summary>
    /// Self-bootstrapping PSO (pipeline state object) precook — the grown-up
    /// replacement for "warm pulse" hacks. The first time a shader variant +
    /// render state combination is drawn, the driver compiles a PSO on the
    /// spot: that IS the first-show hitch (first Spine scene, first particle
    /// burst, first blur). Unity 6's <see cref="GraphicsStateCollection"/> can
    /// record every combination a session actually used and precompile the
    /// whole set next launch, before anything is on screen.
    ///
    /// Self-bootstrapping: no collection on disk → this session TRACES and
    /// saves on quit/pause; a collection exists → it warms progressively (a
    /// few PSOs per frame behind the boot/loading screen) and keeps tracing
    /// OFF. The file is keyed by graphics API + quality level, so a device
    /// never warms another backend's states. Failure of any step degrades to
    /// exactly the pre-existing behaviour — first-show compiles.
    /// </summary>
    public static class LvnPsoWarmup
    {
        private static GraphicsStateCollection _collection;
        private static bool _tracing;
        private static bool _booted;

        private static string FilePath =>
            Path.Combine(Application.persistentDataPath,
                $"lvn_pso_{SystemInfo.graphicsDeviceType}_{QualitySettings.names[QualitySettings.GetQualityLevel()]}.graphicsstate");

        /// <summary>Call once at app boot (before first content draws). Safe to
        /// call again — subsequent calls no-op.</summary>
        public static void Boot()
        {
            if (_booted) return;
            _booted = true;
            try
            {
                var path = FilePath;
                _collection = new GraphicsStateCollection();
                bool loaded = File.Exists(path) && _collection.LoadFromFile(path);
                if (loaded && _collection.variantCount > 0)
                {
                    Driver().StartWarmup(_collection);
                    Debug.Log($"[pso] warming {_collection.variantCount} variant(s) / {_collection.totalGraphicsStateCount} state(s) from {Path.GetFileName(path)}");
                }
                else
                {
                    _collection.BeginTrace();
                    _tracing = true;
                    Driver(); // hooks quit/pause saves
                    Debug.Log("[pso] no collection for this device yet — tracing this session");
                }
            }
            catch (System.Exception ex)
            {
                // Experimental API — a platform where it misbehaves just keeps
                // the old first-show compiles. Never let warmup break boot.
                Debug.LogWarning($"[pso] warmup unavailable: {ex.Message}");
                _collection = null;
                _tracing = false;
            }
        }

        /// <summary>Persist the traced collection (quit, mobile pause). Cheap
        /// no-op when not tracing.</summary>
        public static void SaveTrace()
        {
            if (!_tracing || _collection == null) return;
            try
            {
                _collection.EndTrace();
                if (_collection.variantCount > 0 && _collection.SaveToFile(FilePath))
                    Debug.Log($"[pso] traced {_collection.variantCount} variant(s) → {Path.GetFileName(FilePath)}");
                // Keep collecting if the session continues (pause ≠ quit).
                _collection.BeginTrace();
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[pso] trace save failed: {ex.Message}");
                _tracing = false;
            }
        }

        private static LvnPsoWarmupDriver _driver;

        private static LvnPsoWarmupDriver Driver()
        {
            if (_driver == null)
            {
                var go = new GameObject("LvnPsoWarmup") { hideFlags = HideFlags.HideAndDontSave };
                Object.DontDestroyOnLoad(go);
                _driver = go.AddComponent<LvnPsoWarmupDriver>();
            }
            return _driver;
        }

        // Spreads warmup across frames (a handful of PSOs each) so the compile
        // cost hides behind the boot screen instead of becoming its own hitch,
        // and saves the trace at the moments a session can end.
        private sealed class LvnPsoWarmupDriver : MonoBehaviour
        {
            private GraphicsStateCollection _warming;
            private const int StatesPerFrame = 6;

            public void StartWarmup(GraphicsStateCollection c) => _warming = c;

            private void Update()
            {
                if (_warming == null) return;
                if (_warming.isWarmedUp)
                {
                    Debug.Log($"[pso] warmup complete: {_warming.completedWarmupCount} state(s)");
                    _warming = null;
                    return;
                }
                _warming.WarmUpProgressively(StatesPerFrame);
            }

            private void OnApplicationPause(bool paused)
            {
                if (paused) SaveTrace();
            }

            private void OnApplicationQuit() => SaveTrace();
        }
    }
}
