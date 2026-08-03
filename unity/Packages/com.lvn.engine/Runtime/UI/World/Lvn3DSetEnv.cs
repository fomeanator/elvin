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
        public ShadowResolution shadowResolution = ShadowResolution.High;
        /// <summary>Как карта теней подгоняется под кадр. <c>StableFit</c> —
        /// «стабильная подгонка»: карта привязана к сфере вокруг камеры и НЕ
        /// перестраивается от каждого поворота. С <c>CloseFit</c> (значение
        /// Unity по умолчанию) она пересчитывается на любое движение, и края
        /// теней дрожат — заметнее всего при дыхании камеры и осмотре, когда
        /// кадр не стоит на месте ни секунды. Стабильность стоит немного
        /// чёткости, и это правильный размен для новеллы.</summary>
        public ShadowProjection shadowProjection = ShadowProjection.StableFit;
        [Tooltip("Supported values are 0, 2 and 4; other values are normalized on apply.")]
        /// <summary>ДВА каскада. Одного хватало, пока сцена была комнатой на
        /// два десятка метров; кладбище и лес тянутся на сотню, и одна карта
        /// теней на такую глубину размазывается в кашу. Четыре каскада для
        /// новеллы избыточны — их лишние границы дают собственное мерцание на
        /// стыках, а глубины кадра у нас не столько.</summary>
        public int shadowCascades = 2;

        /// <summary>Дальность теней в метрах.
        ///
        /// <para>Было пятнадцать — под ту же «комнату». За этой чертой Unity
        /// тени просто НЕ РИСУЕТ, и предмет, стоящий чуть дальше, теряет их
        /// молча: вблизи склеп отбрасывает тень, отойди на шаг — перестаёт.
        /// В кадре это читается как ошибка света, хотя свет здесь ни при чём.
        /// </para>
        /// <para>Пятьдесят метров покрывают открытую сцену новеллы целиком.
        /// Дальше тень всё равно съедает туман, а карта теней тем грубее, чем
        /// больше площади на неё приходится.</para></summary>
        public float shadowDistance = 50f;
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
        private ShadowProjection _wasShadowProjection;
        private int _wasShadowCascades;
        private float _wasShadowDistance, _wasShadowNearPlaneOffset;

        /// <summary>Туман из скрипта (`light kind=fog`). Линейный, а не
        /// экспоненциальный: автору нужно сказать «отсюда и досюда» в метрах, а
        /// не подбирать плотность на глаз.</summary>
        public void SetFog(bool on, Color? color, float? near, float? far, float dur = 0f)
        {
            fog = on;
            if (dur > 0.01f)
            {
                // Туман переезжает вместе со светом: рассвет — это не только
                // другое солнце, но и другая дымка, и разъезжаться им нельзя.
                _fadeFogFrom = fogColor; _fadeFogTo = color ?? fogColor;
                _fadeNearFrom = fogStart; _fadeNearTo = near ?? fogStart;
                _fadeFarFrom = fogEnd;   _fadeFarTo = far ?? fogEnd;
                _fogFadeTime = 0f; _fogFadeDur = dur;
                if (near != null || far != null) fogMode = FogMode.Linear;
                enabled = true;
                return;
            }
            if (color is Color c) fogColor = c;
            if (near is float n) fogStart = n;
            if (far is float f) fogEnd = f;
            if (near != null || far != null) fogMode = FogMode.Linear;
            Reapply();
        }

        private Color _fadeFogFrom, _fadeFogTo, _fadeSkyFrom, _fadeSkyTo, _fadeGndFrom, _fadeGndTo;
        private float _fadeNearFrom, _fadeNearTo, _fadeFarFrom, _fadeFarTo;
        private float _fogFadeTime, _fogFadeDur, _skyFadeTime, _skyFadeDur;

        private void Update()
        {
            bool busy = false;
            if (_fogFadeDur > 0f)
            {
                _fogFadeTime += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(_fogFadeTime / _fogFadeDur);
                k = k * k * (3f - 2f * k);
                fogColor = Color.Lerp(_fadeFogFrom, _fadeFogTo, k);
                fogStart = Mathf.Lerp(_fadeNearFrom, _fadeNearTo, k);
                fogEnd = Mathf.Lerp(_fadeFarFrom, _fadeFarTo, k);
                if (k >= 1f) _fogFadeDur = 0f;
                busy = true;
            }
            if (_skyFadeDur > 0f)
            {
                _skyFadeTime += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(_skyFadeTime / _skyFadeDur);
                k = k * k * (3f - 2f * k);
                ambientSky = Color.Lerp(_fadeSkyFrom, _fadeSkyTo, k);
                ambientGround = Color.Lerp(_fadeGndFrom, _fadeGndTo, k);
                ambientEquator = Color.Lerp(ambientSky, ambientGround, 0.5f);
                if (k >= 1f) _skyFadeDur = 0f;
                busy = true;
            }
            if (busy) Reapply(); else enabled = false;
        }

        /// <summary>Небо из скрипта (`light kind=sky`): цвет вверху, у горизонта
        /// и общий подсвет. Это не картинка неба, а ГРАДИЕНТ окружающего света —
        /// то, из чего складывается настроение места.</summary>
        public void SetSky(bool on, Color? top, Color? bottom, Color? mid, float dur = 0f)
        {
            ambient = on;
            if (dur > 0.01f)
            {
                _fadeSkyFrom = ambientSky; _fadeSkyTo = top ?? ambientSky;
                _fadeGndFrom = ambientGround; _fadeGndTo = bottom ?? ambientGround;
                _skyFadeTime = 0f; _skyFadeDur = dur;
                enabled = true;
                return;
            }
            // РАССЕЯННЫЙ СВЕТ СВЕРХУ — НЕ ЦВЕТ ЗЕНИТА.
            //
            // В небо смотрит вся верхняя полусфера, а не одна точка над
            // головой: свет, падающий на землю, складывается из всего неба —
            // и тёмного зенита, и светлого горизонта. Приравняв его к зениту,
            // мы получали ночью почти чёрную землю рядом со СВЕТЛОЙ стеной:
            // вертикаль брала цвет горизонта, горизонтальная поверхность —
            // черноту макушки неба. На одном и том же свете два одинаковых
            // камня выглядели по-разному, и это читалось как ошибка текстур.
            //
            // Поэтому сверху берём общий подсвет (`color=`), а если автор его
            // не задал — среднее между зенитом и горизонтом с перевесом в
            // сторону горизонта: оттуда света приходит больше.
            if (mid is Color m) ambientSky = m;
            else if (top is Color t3 && bottom is Color b3) ambientSky = Color.Lerp(t3, b3, 0.6f);
            else if (top is Color t) ambientSky = t;

            if (bottom is Color b) ambientGround = b;

            // Горизонт — между небом и землёй: боковые грани освещены и тем,
            // и другим примерно поровну.
            ambientEquator = Color.Lerp(ambientSky, ambientGround, 0.5f);
            Reapply();
        }

        /// <summary>Переложить настройки, не теряя запомненное «как было».</summary>
        /// <summary>Переложить настройки заново — после того, как их поправил
        /// скрипт.</summary>
        public void Reapply()
        {
            if (!_held) { Apply(); return; }
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
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
                RenderSettings.ambientSkyColor = ambientSky;
                RenderSettings.ambientEquatorColor = ambientEquator;
                RenderSettings.ambientGroundColor = ambientGround;
            }
        }

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
            _wasShadowProjection = QualitySettings.shadowProjection;
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
                QualitySettings.shadowProjection = shadowProjection;
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
                QualitySettings.shadowProjection = _wasShadowProjection;
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
