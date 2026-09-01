using System;
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

        // Per-channel command generation: a later audio command on the same
        // channel supersedes an earlier one whose clip is still loading, so two
        // music commands replayed on resume (or a stop racing a play) can't let
        // the slower load win and play the wrong/old track.
        private readonly System.Collections.Generic.Dictionary<string, int> _channelGen
            = new System.Collections.Generic.Dictionary<string, int>();

        // The live fade coroutine per channel, so a new command cancels the
        // previous fade instead of letting a fade-out keep lerping the volume
        // down (and Stop()) right over a track the next command just started.
        private readonly System.Collections.Generic.Dictionary<string, Coroutine> _fadeCo
            = new System.Collections.Generic.Dictionary<string, Coroutine>();

        private int BumpChannel(string channel)
        {
            int g = (_channelGen.TryGetValue(channel, out var c) ? c : 0) + 1;
            _channelGen[channel] = g;
            return g;
        }

        /// <summary>Чем кончается затухание. Флага «останавливать ли» не хватило:
        /// печать по концу не останавливают, а СТАВЯТ НА ПАУЗУ (позиция записи
        /// живёт до следующей строки) — и ради одного лишнего исхода рядом
        /// вырос второй, почти такой же механизм затухания со своей корутиной,
        /// своей отменой и своим Lerp.</summary>
        private enum FadeEnd { Keep, Stop, Pause }

        private void StartFade(string channel, AudioSource src, float from, float to, float seconds, FadeEnd end)
        {
            if (_fadeCo.TryGetValue(channel, out var old) && old != null) StopCoroutine(old);
            _fadeCo[channel] = StartCoroutine(FadeAudio(src, from, to, seconds, end));
        }

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

        // Громкость канала знает ЗВУКОРЕЖИССЁР — здесь была своя таблица
        // каналов, и в ней не было озвучки: реплика играла мимо настроек.
        private static float Master => LvnVolumes.Master;
        private static float UserScale(string channel) => LvnVolumes.Of(channel);

        // Re-scale the live sources when the player moves a volume slider or flips
        // the master sound switch. A fade in flight keeps its own target (it snaps
        // on the next command) — fine for a settings tweak.
        private void ApplyUserVolumes()
        {
            // Авторская громкость (что просил сценарий) × пользовательская
            // (что выставил игрок). Вторая половина — у Звукорежиссёра.
            if (_music != null) _music.volume = _authMusic * LvnVolumes.Of(LvnVolumes.Music);
            if (_ambient != null) _ambient.volume = _authAmbient * LvnVolumes.Of(LvnVolumes.Ambient);
            if (_sfx != null) _sfx.volume = _authSfx * LvnVolumes.Of(LvnVolumes.Sfx);
            if (_voice != null) _voice.volume = LvnVolumes.Of(LvnVolumes.Voice);
            // Печать — такой же живой источник: без этой строки выключенный
            // посреди реплики звук продолжал стучать до её конца.
            if (_typing != null && _typing.isPlaying)
                _typing.volume = _authTyping * LvnVolumes.Of(LvnVolumes.Ui);
        }

        private void RememberAuthored(string channel, float v)
        {
            if (channel == LvnVolumes.Music) _authMusic = v;
            else if (channel == LvnVolumes.Ambient) _authAmbient = v;
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
            catch (OperationCanceledException) { return; }   // реплику сменили — это не отказ
            catch (System.Exception ex)
            {
                // «Хост не поставляет озвучку» — это ПУСТОЙ url, и он отсеян
                // строкой выше. Сюда попадает другое: автор озвучку ЗАДАЛ, а она
                // не зазвучала. Для игрока это немая реплика там, где обещан
                // голос, и молчать об этом нельзя — ровно как с пропавшей
                // картинкой.
                Lvn.Content.ContentLoader.NoteAssetUnusable(url, "озвучка: " + ex.GetType().Name);
            }
            if (clip == null && _voice != null && gen == _voiceGen)
                Lvn.Content.ContentLoader.NoteAssetUnusable(url, "озвучка не стала клипом");
            if (clip == null || _voice == null || gen != _voiceGen) return;
            _voice.clip = clip;
            // ЧЕРЕЗ ЗВУКОРЕЖИССЁРА, а не с ползунка напрямую: здесь терялся
            // общий тумблер, и при выключенном звуке реплика всё равно
            // звучала — до ближайшего пересчёта громкостей.
            _voice.volume = LvnVolumes.Of(LvnVolumes.Voice);
            _voice.Play();
        }

        /// <summary>Cut the voice line (scene reset / chapter end).</summary>
        /// <summary>
        /// ЗАМОЛЧАТЬ ВМЕСТЕ С ГЛАВОЙ — всё звучание, которое ей принадлежит.
        ///
        /// <para>Уборка кадра снимала печать и голос, а музыку с эмбиентом
        /// НЕТ: трек главы продолжал играть в меню, и поверх него отпускалась
        /// музыка витрины — «выходишь из главы, музыка дублируется» (живой
        /// репорт Ильи 01.09). Каждый снимал своё, а музыку не снимал никто:
        /// её просто не было в списке того, что уносит уходящая глава.</para>
        ///
        /// <para>Гаснет плавно: обрыв на полутакте слышен как сбой, а не как
        /// конец. Музыка МЕНЮ живёт мимо этого дома и не задета — её ведёт
        /// хост.</para>
        /// </summary>
        public void SilenceChapter(float fade = 0.35f)
        {
            StopVoice();
            StopTypingLoop();
            Silence(LvnVolumes.Music, fade);
            Silence(LvnVolumes.Ambient, fade);
            Silence(LvnVolumes.Sfx, 0f);   // короткий звук доигрывать нечему — обрываем
        }

        /// <summary>
        /// КАКОЙ ИСТОЧНИК ВЕДЁТ ЭТОТ КАНАЛ. Соответствие стояло дважды, слово в
        /// слово, и оба раза литералами — при том что имена каналов объявлены
        /// домом громкостей и сопровождены там прямым напоминанием: «те же, что
        /// в авторских командах звука».
        ///
        /// <para>Таблица каналов сцены уже расходилась с той: канал озвучки в
        /// ней просто забыли, и голос звучал мимо своего ползунка. Пока
        /// соответствие пишут по месту, забыть его снова ничего не мешает.</para>
        ///
        /// <para>Неизвестный канал — эффект, то же правило, что у громкости:
        /// новая команда звука не должна звучать мимо настроек только потому,
        /// что её не внесли в таблицу.</para>
        /// </summary>
        private AudioSource SourceOf(string channel)
            => channel == LvnVolumes.Music ? _music
             : channel == LvnVolumes.Ambient ? _ambient
             : _sfx;

        private void Silence(string channel, float fade)
        {
            var src = SourceOf(channel);
            if (src == null) return;
            BumpChannel(channel);        // команда в полёте теряет право на канал
            _playingUrl.Remove(channel);
            if (fade > 0f && src.isPlaying) StartFade(channel, src, src.volume, 0f, fade, FadeEnd.Stop);
            else { CancelFade(channel); src.Stop(); }
        }

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
            if (clip == null || _ui == null || !LvnPrefs.SoundOn) return;
            // Через дом громкости: прямой ползунок не знал про общий тумблер,
            // и «звук выключен» глушил историю, но не интерфейс.
            _ui.PlayOneShot(clip, Mathf.Clamp01(volume) * LvnVolumes.Of(LvnVolumes.Ui));
        }

        // ── луп печати (ui.sounds.type) ──────────────────────────────────────
        // Свой источник: PlayOneShot не остановить, а луп живёт ровно столько,
        // сколько строка проявляется. Канал _ui не трогаем — клик по нему
        // может щёлкнуть прямо поверх печати.
        //
        // ОДИН НЕПРЕРЫВНЫЙ «ТРЕК КЛАВИАТУРЫ» (правка Ильи): пауза между
        // репликами не сбрасывает позицию — печать ПРОДОЛЖАЕТ запись с того
        // же места и идёт по кругу (loop). Старт заново на каждой строке
        // звучал как заикание одного и того же начала. Входы/выходы — с
        // короткими фейдами, чтобы стук не рубился на полу-ударе.
        private AudioSource _typing;
        // Что просил автор — чтобы ползунок и тумблер могли домножиться на это
        // ЖИВЬЁМ: раньше громкость печати вычислялась один раз на старте строки,
        // и выключенный посреди длинной реплики звук стучал до её конца.
        private float _authTyping = 1f;
        private const string TypingChannel = "typing";

        /// <summary>Клавиатура стучит, пока строка печатается: продолжение с
        /// места паузы, фейд-ин. Идемпотентно для уже стучащего лупа.</summary>
        public void PlayTypingLoop(AudioClip clip, float volume = 1f)
        {
            if (clip == null || !LvnPrefs.SoundOn) return;
            if (_typing == null)
            {
                _typing = gameObject.AddComponent<AudioSource>();
                _typing.loop = true;
                _typing.playOnAwake = false;
                _typing.volume = 0f;
            }
            if (_typing.clip != clip)
            {
                _typing.clip = clip;
                _typing.Stop();
                _typing.volume = 0f;
            }
            if (!_typing.isPlaying)
            {
                _typing.UnPause();                    // с места паузы…
                if (!_typing.isPlaying) _typing.Play(); // …или первый запуск
            }
            _authTyping = Mathf.Clamp01(volume);
            StartFade(TypingChannel, _typing, _typing.volume,
                      _authTyping * LvnVolumes.Of(LvnVolumes.Ui), 0.09f, FadeEnd.Keep);
        }

        /// <summary>Строка допечаталась (или её докрутили тапом) — фейд-аут и
        /// ПАУЗА: позиция записи сохраняется до следующей печати.</summary>
        public void StopTypingLoop()
        {
            if (_typing == null || !_typing.isPlaying) return;
            StartFade(TypingChannel, _typing, _typing.volume, 0f, 0.18f, FadeEnd.Pause);
        }

        /// <summary>Apply one <c>audio</c> command. Missing audio is silent — a host
        /// that ships no sound simply no-ops. <paramref name="ct"/> cancels the
        /// in-flight clip load with the chapter.</summary>
        public async Task ApplyAsync(JObject cmd, ILvnAssets assets, CancellationToken ct)
        {
            var channel = (string)cmd["channel"] ?? LvnVolumes.Sfx;
            var src = SourceOf(channel);
            float fade = NumOr(cmd["fade"], 0f);
            int gen = BumpChannel(channel); // this command now owns the channel

            if ((string)cmd["action"] == "stop")
            {
                _playingUrl.Remove(channel);
                if (fade > 0f) StartFade(channel, src, src.volume, 0f, fade, FadeEnd.Stop);
                else { CancelFade(channel); src.Stop(); }
                return;
            }

            var url = (string)cmd["url"];
            if (assets == null || string.IsNullOrEmpty(url)) return;

            float volume = NumOr(cmd["volume"], 1f);
            RememberAuthored(channel, volume);
            float effective = volume * UserScale(channel);

            // Idempotent for looping channels: the same track already playing (a
            // load/rollback replay) keeps its position — only the volume updates.
            if (channel != LvnVolumes.Sfx && src.isPlaying
                && _playingUrl.TryGetValue(channel, out var cur) && cur == url)
            {
                src.volume = effective;
                return;
            }

            AudioClip clip = null;
            try { clip = await assets.LoadAudioAsync(url, ct); }
            catch (OperationCanceledException) { return; }   // сцена сменилась — не отказ
            catch (System.Exception ex)
            {
                // Пустой url отсеян выше: сюда попадает музыка или звук,
                // которые автор НАЗВАЛ, а они не зазвучали. Тишина вместо темы
                // — такая же пропажа, как силуэт вместо героини.
                Lvn.Content.ContentLoader.NoteAssetUnusable(url, "звук «" + channel + "»: " + ex.GetType().Name);
            }
            if (clip == null) return;
            // A newer audio command (or a chapter reset that bumps the channel via
            // StopVoice/ResetStage's stop) started on this channel while we loaded
            // — it must win. Without this the slower of two replayed music loads
            // plays last and the wrong track ends up on screen.
            if (!_channelGen.TryGetValue(channel, out var g2) || g2 != gen) return;

            if (channel != LvnVolumes.Sfx)
            {
                src.loop = BoolOr(cmd["loop"], true);
                _playingUrl[channel] = url;
            }
            src.clip = clip;
            if (fade > 0f)
            {
                src.volume = 0f;
                src.Play();
                StartFade(channel, src, 0f, effective, fade, FadeEnd.Keep);
            }
            else
            {
                CancelFade(channel);
                src.volume = effective;
                src.Play();
            }
        }

        private void CancelFade(string channel)
        {
            if (_fadeCo.TryGetValue(channel, out var old) && old != null) StopCoroutine(old);
            _fadeCo.Remove(channel);
        }

        // Tolerant field reads (mirror VnStage's): a malformed value degrades to the
        // default instead of throwing and killing the chapter.
        private static float NumOr(JToken t, float dflt)
        {
            if (t == null) return dflt;
            try { return (float)t; } catch { return dflt; }
        }

        // Читал только настоящее true/false, поэтому «loop=no» тихо давал
        // умолчание — зацикленную музыку там, где автор просил обратного.
        private static bool BoolOr(JToken t, bool dflt) => Lvn.LvnBool.Of(t, dflt);

        private static IEnumerator FadeAudio(AudioSource src, float from, float to, float seconds, FadeEnd end)
        {
            float t = 0f;
            while (t < seconds && src != null)
            {
                t += Time.unscaledDeltaTime;
                src.volume = Mathf.Lerp(from, to, Mathf.Clamp01(t / seconds));
                yield return null;
            }
            if (src == null) yield break;   // источник снесли посреди затухания
            src.volume = to;
            if (end == FadeEnd.Stop) src.Stop();
            else if (end == FadeEnd.Pause) src.Pause();
        }
    }
}
