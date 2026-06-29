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
        private AudioSource _music, _ambient, _sfx;

        private void Awake()
        {
            _music = gameObject.AddComponent<AudioSource>();
            _ambient = gameObject.AddComponent<AudioSource>();
            _sfx = gameObject.AddComponent<AudioSource>();
            foreach (var s in new[] { _music, _ambient, _sfx }) s.playOnAwake = false;
            _music.loop = true;
            _ambient.loop = true;
        }

        /// <summary>Apply one <c>audio</c> command. Missing audio is silent — a host
        /// that ships no sound simply no-ops. <paramref name="ct"/> cancels the
        /// in-flight clip load with the chapter.</summary>
        public async Task ApplyAsync(JObject cmd, ILvnAssets assets, CancellationToken ct)
        {
            var channel = (string)cmd["channel"] ?? "sfx";
            var src = channel == "music" ? _music : channel == "ambient" ? _ambient : _sfx;
            float fade = cmd["fade"] != null ? (float)cmd["fade"] : 0f;

            if ((string)cmd["action"] == "stop")
            {
                if (fade > 0f) StartCoroutine(FadeAudio(src, src.volume, 0f, fade, stopAtEnd: true));
                else src.Stop();
                return;
            }

            var url = (string)cmd["url"];
            if (assets == null || string.IsNullOrEmpty(url)) return;

            AudioClip clip = null;
            try { clip = await assets.LoadAudioAsync(url, ct); }
            catch { /* silent if the host ships no audio */ }
            if (clip == null) return;

            float volume = cmd["volume"] != null ? (float)cmd["volume"] : 1f;
            if (channel != "sfx") src.loop = cmd["loop"] == null || (bool)cmd["loop"];
            src.clip = clip;
            if (fade > 0f)
            {
                src.volume = 0f;
                src.Play();
                StartCoroutine(FadeAudio(src, 0f, volume, fade, stopAtEnd: false));
            }
            else
            {
                src.volume = volume;
                src.Play();
            }
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
