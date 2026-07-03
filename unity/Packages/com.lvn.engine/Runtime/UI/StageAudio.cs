using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// Owns the novel's three audio channels — music, ambient, sfx — and applies
    /// <c>audio</c> stage commands: load a clip and play it (optionally fading in),
    /// or stop a channel (optionally fading out). Extracted from <see cref="VnStage"/>
    /// so the stage doesn't carry mixing concerns; it's a small MonoBehaviour
    /// because the cross-fades run as coroutines.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StageAudio : MonoBehaviour
    {
        private AudioSource _music, _ambient, _sfx, _ui, _voice;

        // Track what each looping channel is playing (by url) so a replayed audio
        // command after a load/rollback recognises "this track is already on" and
        // adjusts volume instead of restarting it from the beginning.
        private readonly System.Collections.Generic.Dictionary<string, string> _playingUrl
            = new System.Collections.Generic.Dictionary<string, string>();

        // The author's last set volume per channel — the player's preference
        // multiplies onto it, so "музыка 50%" scales whatever the script asked for
        // instead of overriding it.
        private float _authMusic = 1f, _authAmbient = 1f, _authSfx = 1f;

        private void Awake()
        {
            _music = gameObject.AddComponent<AudioSource>();
            _ambient = gameObject.AddComponent<AudioSource>();
            _sfx = gameObject.AddComponent<AudioSource>();
            _ui = gameObject.AddComponent<AudioSource>();
            _voice = gameObject.AddComponent<AudioSource>();
            foreach (var s in new[] { _music, _ambient, _sfx, _ui, _voice }) s.playOnAwake = false;
            _music.loop = true;
            _ambient.loop = true;
            LvnPrefs.Changed += ApplyUserVolumes;
        }

        private void OnDestroy() => LvnPrefs.Changed -= ApplyUserVolumes;

        private static float UserScale(string channel) =>
            channel == "music" ? LvnPrefs.VolMusic
            : channel == "ambient" ? LvnPrefs.VolAmbient
            : LvnPrefs.VolSfx;

        // Re-scale the live sources when the player moves a volume slider. A fade
        // in flight keeps its own target (it snaps on the next command) — fine for
        // a settings tweak.
        private void ApplyUserVolumes()
        {
            if (_music != null) _music.volume = _authMusic * LvnPrefs.VolMusic;
            if (_ambient != null) _ambient.volume = _authAmbient * LvnPrefs.VolAmbient;
            if (_sfx != null) _sfx.volume = _authSfx * LvnPrefs.VolSfx;
            if (_voice != null) _voice.volume = LvnPrefs.VolVoice;
        }

        private void RememberAuthored(string channel, float v)
        {
            if (channel == "music") _authMusic = v;
            else if (channel == "ambient") _authAmbient = v;
            else _authSfx = v;
        }

        /// <summary>True while a voice-over line is speaking — the stage mutes the
        /// typewriter blip under it.</summary>
        public bool VoicePlaying => _voice != null && _voice.isPlaying;

        /// <summary>Voice the line on screen: stop the previous one (voice never
        /// overlaps itself) and play the clip at the player's voice volume. A null/
        /// missing url or a failed load is silence — unvoiced novels no-op. The
        /// generation guard drops a slow load that finishes after the NEXT line
        /// already started (or stopped) its own voice.</summary>
        private int _voiceGen;
        public async Task PlayVoiceAsync(string url, ILvnAssets assets, CancellationToken ct)
        {
            int gen = ++_voiceGen;
            if (_voice != null) _voice.Stop();
            if (string.IsNullOrEmpty(url) || assets == null) return;
            AudioClip clip = null;
            try { clip = await assets.LoadAudioAsync(url, ct); }
            catch { /* silent if the host ships no voice */ }
            if (clip == null || _voice == null || gen != _voiceGen) return;
            _voice.clip = clip;
            _voice.volume = LvnPrefs.VolVoice;
            _voice.Play();
        }

        /// <summary>Cut the voice line (scene reset / chapter end).</summary>
        public void StopVoice()
        {
            _voiceGen++;
            if (_voice != null) _voice.Stop();
        }

        /// <summary>Play a UI one-shot (tap / choice / typewriter blip) on a channel
        /// of its own, so a blip never cuts a story sfx. Scaled by the player's SFX
        /// preference; a null clip no-ops (a novel without UI audio stays silent).</summary>
        public void PlayUi(AudioClip clip, float volume = 1f)
        {
            if (clip == null || _ui == null) return;
            _ui.PlayOneShot(clip, Mathf.Clamp01(volume) * LvnPrefs.VolSfx);
        }

        /// <summary>Apply one <c>audio</c> command. Missing audio is silent — a host
        /// that ships no sound simply no-ops. <paramref name="ct"/> cancels the
        /// in-flight clip load with the chapter.</summary>
        public async Task ApplyAsync(JObject cmd, ILvnAssets assets, CancellationToken ct)
        {
            var channel = (string)cmd["channel"] ?? "sfx";
            var src = channel == "music" ? _music : channel == "ambient" ? _ambient : _sfx;
            float fade = NumOr(cmd["fade"], 0f);

            if ((string)cmd["action"] == "stop")
            {
                _playingUrl.Remove(channel);
                if (fade > 0f) StartCoroutine(FadeAudio(src, src.volume, 0f, fade, stopAtEnd: true));
                else src.Stop();
                return;
            }

            var url = (string)cmd["url"];
            if (assets == null || string.IsNullOrEmpty(url)) return;

            float volume = NumOr(cmd["volume"], 1f);
            RememberAuthored(channel, volume);
            float effective = volume * UserScale(channel);

            // Idempotent for looping channels: the same track already playing (a
            // load/rollback replay) keeps its position — only the volume updates.
            if (channel != "sfx" && src.isPlaying
                && _playingUrl.TryGetValue(channel, out var cur) && cur == url)
            {
                src.volume = effective;
                return;
            }

            AudioClip clip = null;
            try { clip = await assets.LoadAudioAsync(url, ct); }
            catch { /* silent if the host ships no audio */ }
            if (clip == null) return;

            if (channel != "sfx")
            {
                src.loop = BoolOr(cmd["loop"], true);
                _playingUrl[channel] = url;
            }
            src.clip = clip;
            if (fade > 0f)
            {
                src.volume = 0f;
                src.Play();
                StartCoroutine(FadeAudio(src, 0f, effective, fade, stopAtEnd: false));
            }
            else
            {
                src.volume = effective;
                src.Play();
            }
        }

        // Tolerant field reads (mirror VnStage's): a malformed value degrades to the
        // default instead of throwing and killing the chapter.
        private static float NumOr(JToken t, float dflt)
        {
            if (t == null) return dflt;
            try { return (float)t; } catch { return dflt; }
        }

        private static bool BoolOr(JToken t, bool dflt)
        {
            if (t == null) return dflt;
            try { return (bool)t; } catch { return dflt; }
        }

        private static IEnumerator FadeAudio(AudioSource src, float from, float to, float seconds, bool stopAtEnd)
        {
            float t = 0f;
            while (t < seconds)
            {
                t += Time.unscaledDeltaTime;
                src.volume = Mathf.Lerp(from, to, Mathf.Clamp01(t / seconds));
                yield return null;
            }
            src.volume = to;
            if (stopAtEnd) src.Stop();
        }
    }
}
