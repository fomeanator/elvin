using UnityEngine;
using UnityEngine.Rendering;

namespace Lvn.UI.World
{
    /// <summary>
    /// The air a 3D set stands in: sky, fog and ambient light. Put this on the
    /// root of a set prefab and <see cref="Lvn3DBackdrop"/> applies it while the
    /// set is standing, restoring what was there when the set is struck.
    ///
    /// <para>Why this exists as its own component: a prefab carries geometry,
    /// materials and lights, but NOT the scene settings that make stylised art
    /// look the way its author intended — a tinted sky, distance fog, coloured
    /// ambient bounce. Import a beautiful kit, drop its trees into a prefab, and
    /// they come out flat and grey against a default sky; nothing is broken and
    /// no shader is missing, the atmosphere simply did not travel with them.
    /// Storing it here makes the atmosphere part of the set, so a novel gets the
    /// kit's own look without touching project-wide lighting settings.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Lvn3DSetEnv : MonoBehaviour
    {
        [Tooltip("Sky material for this set; empty keeps the project's.")]
        public Material skybox;

        [Header("Fog — depth, and most of the mood")]
        public bool fog = true;
        public Color fogColor = new Color(0.83f, 1f, 0.99f, 1f);
        public FogMode fogMode = FogMode.ExponentialSquared;
        public float fogDensity = 0.02f;
        [Tooltip("Linear fog only: where fog starts and where it is total.")]
        public float fogStart = 10f, fogEnd = 120f;

        [Header("Ambient — the light that comes from nowhere in particular")]
        public bool ambient = true;
        public Color ambientSky = Color.white;
        public Color ambientEquator = new Color(0.63f, 0.53f, 1.06f, 1f);
        public Color ambientGround = new Color(1.5f, 0.61f, 0.68f, 1f);

        [Header("Snapshot rendering")]
        [Tooltip("Disable time-driven vegetation wind while this set is used as a novel backdrop. " +
                 "The camera may move, but the captured world and its shadows stay in one pose.")]
        public bool freezeShaderWind = true;

        [Header("Realtime shadows")]
        [Tooltip("Use a compact, set-specific shadow profile while this set is standing.")]
        public bool overrideShadows = true;
        public ShadowQuality shadowQuality = ShadowQuality.All;
        public ShadowResolution shadowResolution = ShadowResolution.Medium;
        [Tooltip("Supported values are 0, 2 and 4; other values are normalized on apply.")]
        public int shadowCascades = 2;
        public float shadowDistance = 15f;
        public float shadowNearPlaneOffset = 2f;

        // What the project looked like before this set stood up.
        private bool _held, _heldShadows;
        private Material _wasSkybox;
        private bool _wasFog;
        private Color _wasFogColor;
        private FogMode _wasFogMode;
        private float _wasFogDensity, _wasFogStart, _wasFogEnd;
        private AmbientMode _wasAmbientMode;
        private Color _wasSky, _wasEquator, _wasGround;
        private ShadowQuality _wasShadowQuality;
        private ShadowResolution _wasShadowResolution;
        private int _wasShadowCascades;
        private float _wasShadowDistance, _wasShadowNearPlaneOffset;

        /// <summary>Put this set's atmosphere in place, remembering the old one.</summary>
        public void Apply()
        {
            if (_held) return;
            _wasSkybox = RenderSettings.skybox;
            _wasFog = RenderSettings.fog;
            _wasFogColor = RenderSettings.fogColor;
            _wasFogMode = RenderSettings.fogMode;
            _wasFogDensity = RenderSettings.fogDensity;
            _wasFogStart = RenderSettings.fogStartDistance;
            _wasFogEnd = RenderSettings.fogEndDistance;
            _wasAmbientMode = RenderSettings.ambientMode;
            _wasSky = RenderSettings.ambientSkyColor;
            _wasEquator = RenderSettings.ambientEquatorColor;
            _wasGround = RenderSettings.ambientGroundColor;
            _wasShadowQuality = QualitySettings.shadows;
            _wasShadowResolution = QualitySettings.shadowResolution;
            _wasShadowCascades = QualitySettings.shadowCascades;
            _wasShadowDistance = QualitySettings.shadowDistance;
            _wasShadowNearPlaneOffset = QualitySettings.shadowNearPlaneOffset;
            _heldShadows = overrideShadows;
            _held = true;

            // Safe to set globally: everything else on screen is canvas UI and
            // sprites, which lighting and fog do not touch.
            if (skybox != null) RenderSettings.skybox = skybox;
            RenderSettings.fog = fog;
            if (fog)
            {
                RenderSettings.fogColor = fogColor;
                RenderSettings.fogMode = fogMode;
                RenderSettings.fogDensity = fogDensity;
                RenderSettings.fogStartDistance = fogStart;
                RenderSettings.fogEndDistance = fogEnd;
            }
            if (ambient)
            {
                RenderSettings.ambientMode = AmbientMode.Trilight;
                RenderSettings.ambientSkyColor = ambientSky;
                RenderSettings.ambientEquatorColor = ambientEquator;
                RenderSettings.ambientGroundColor = ambientGround;
            }
            if (_heldShadows)
            {
                QualitySettings.shadows = shadowQuality;
                QualitySettings.shadowResolution = shadowResolution;
                QualitySettings.shadowCascades =
                    shadowCascades >= 4 ? 4 : shadowCascades >= 2 ? 2 : 0;
                QualitySettings.shadowDistance = Mathf.Max(0f, shadowDistance);
                QualitySettings.shadowNearPlaneOffset =
                    Mathf.Max(0f, shadowNearPlaneOffset);
            }
        }

        /// <summary>Give the project back the air it had.</summary>
        public void Restore()
        {
            if (!_held) return;
            _held = false;
            RenderSettings.skybox = _wasSkybox;
            RenderSettings.fog = _wasFog;
            RenderSettings.fogColor = _wasFogColor;
            RenderSettings.fogMode = _wasFogMode;
            RenderSettings.fogDensity = _wasFogDensity;
            RenderSettings.fogStartDistance = _wasFogStart;
            RenderSettings.fogEndDistance = _wasFogEnd;
            RenderSettings.ambientMode = _wasAmbientMode;
            RenderSettings.ambientSkyColor = _wasSky;
            RenderSettings.ambientEquatorColor = _wasEquator;
            RenderSettings.ambientGroundColor = _wasGround;
            if (_heldShadows)
            {
                QualitySettings.shadows = _wasShadowQuality;
                QualitySettings.shadowResolution = _wasShadowResolution;
                QualitySettings.shadowCascades = _wasShadowCascades;
                QualitySettings.shadowDistance = _wasShadowDistance;
                QualitySettings.shadowNearPlaneOffset = _wasShadowNearPlaneOffset;
            }
            _heldShadows = false;
        }

        private void OnDestroy() => Restore();
    }
}
