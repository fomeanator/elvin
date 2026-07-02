using System;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// Player-facing preferences — text speed, auto-advance, per-channel volume,
    /// reduce-motion, dialogue window opacity. A single static store backed by
    /// PlayerPrefs: setters clamp, persist and raise <see cref="Changed"/>, so
    /// live consumers (StageAudio volumes, the dialogue box, the settings panel)
    /// stay in sync without polling. Game-agnostic — these are the player's
    /// device-level comfort settings, not per-title state.
    /// </summary>
    public static class LvnPrefs
    {
        /// <summary>Raised after any preference changes (already persisted).</summary>
        public static event Action Changed;

        private const string P = "lvn_pref_";

        // Backing fields, loaded once on first touch.
        private static bool _loaded;
        private static float _textSpeed, _autoDelayScale, _volMusic, _volAmbient, _volSfx, _dialogOpacity;
        private static bool _autoAdvance, _reduceMotion;

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            _textSpeed = PlayerPrefs.GetFloat(P + "text_speed", 1f);
            _autoAdvance = PlayerPrefs.GetInt(P + "auto_advance", 0) == 1;
            _autoDelayScale = PlayerPrefs.GetFloat(P + "auto_delay", 1f);
            _volMusic = PlayerPrefs.GetFloat(P + "vol_music", 1f);
            _volAmbient = PlayerPrefs.GetFloat(P + "vol_ambient", 1f);
            _volSfx = PlayerPrefs.GetFloat(P + "vol_sfx", 1f);
            _reduceMotion = PlayerPrefs.GetInt(P + "reduce_motion", 0) == 1;
            _dialogOpacity = PlayerPrefs.GetFloat(P + "dialog_opacity", 1f);
            TypewriterClock.UserSpeedMultiplier = _textSpeed;
        }

        private static void Set(ref float field, string key, float value)
        {
            if (Mathf.Approximately(field, value)) return;
            field = value;
            PlayerPrefs.SetFloat(P + key, value);
            PlayerPrefs.Save();
            Changed?.Invoke();
        }

        private static void Set(ref bool field, string key, bool value)
        {
            if (field == value) return;
            field = value;
            PlayerPrefs.SetInt(P + key, value ? 1 : 0);
            PlayerPrefs.Save();
            Changed?.Invoke();
        }

        /// <summary>Typewriter speed multiplier (0.25×–3×; 1 = author's pace).
        /// Pushed into <see cref="TypewriterClock.UserSpeedMultiplier"/>.</summary>
        public static float TextSpeed
        {
            get { EnsureLoaded(); return _textSpeed; }
            set
            {
                EnsureLoaded();
                var v = Mathf.Clamp(value, 0.25f, 3f);
                TypewriterClock.UserSpeedMultiplier = v;
                Set(ref _textSpeed, "text_speed", v);
            }
        }

        /// <summary>Hands-free reading: advance automatically once a line has
        /// finished revealing and its reading delay has passed.</summary>
        public static bool AutoAdvance
        {
            get { EnsureLoaded(); return _autoAdvance; }
            set { EnsureLoaded(); Set(ref _autoAdvance, "auto_advance", value); }
        }

        /// <summary>Auto-advance delay multiplier (0.5×–2.5×; 1 = default pace).</summary>
        public static float AutoDelayScale
        {
            get { EnsureLoaded(); return _autoDelayScale; }
            set { EnsureLoaded(); Set(ref _autoDelayScale, "auto_delay", Mathf.Clamp(value, 0.5f, 2.5f)); }
        }

        /// <summary>Music channel volume (0–1), multiplied onto authored volume.</summary>
        public static float VolMusic
        {
            get { EnsureLoaded(); return _volMusic; }
            set { EnsureLoaded(); Set(ref _volMusic, "vol_music", Mathf.Clamp01(value)); }
        }

        /// <summary>Ambient channel volume (0–1).</summary>
        public static float VolAmbient
        {
            get { EnsureLoaded(); return _volAmbient; }
            set { EnsureLoaded(); Set(ref _volAmbient, "vol_ambient", Mathf.Clamp01(value)); }
        }

        /// <summary>Sound-effect channel volume (0–1).</summary>
        public static float VolSfx
        {
            get { EnsureLoaded(); return _volSfx; }
            set { EnsureLoaded(); Set(ref _volSfx, "vol_sfx", Mathf.Clamp01(value)); }
        }

        /// <summary>Suppress vestibular triggers: camera shake and full-screen
        /// flashes are skipped when on.</summary>
        public static bool ReduceMotion
        {
            get { EnsureLoaded(); return _reduceMotion; }
            set { EnsureLoaded(); Set(ref _reduceMotion, "reduce_motion", value); }
        }

        /// <summary>Dialogue window background opacity (0.2–1; text stays crisp —
        /// only the panel behind it fades).</summary>
        public static float DialogOpacity
        {
            get { EnsureLoaded(); return _dialogOpacity; }
            set { EnsureLoaded(); Set(ref _dialogOpacity, "dialog_opacity", Mathf.Clamp(value, 0.2f, 1f)); }
        }
    }
}
