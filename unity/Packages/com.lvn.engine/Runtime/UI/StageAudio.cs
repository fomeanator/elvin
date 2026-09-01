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
        /// <summary>
        /// КАНАЛ — ОДНА ЗАПИСЬ, А НЕ ПЯТЬ ПАМЯТЕЙ.
        ///
        /// <para>Про каждый канал знали пятеро врозь: три именованных поля с
        /// источниками, три поля с авторской громкостью, словарь «что сейчас
        /// звучит», словарь поколений и словарь живых затуханий. Одно и то же
        /// «музыка» приходилось искать в пяти местах, а завести шестой канал
        /// значило дописать шесть.</para>
        ///
        /// <para>Хуже была не цена правки, а её незаметность. Соответствие
        /// «канал → источник» стояло дважды слово в слово, и в одной копии
        /// забыли озвучку — голос звучал мимо своего ползунка. Уборка кадра
        /// снимала печать и голос, а музыку не снимал никто: её просто не
        /// было в списке того, что уносит уходящая глава, и трек главы играл
        /// в меню поверх витринного («выходишь из главы, музыка
        /// дублируется», 01.09).</para>
        ///
        /// <para>Теперь различия каналов — ДАННЫЕ (какой ползунок, ведёт ли
        /// непрерывный трек, слышит ли авторские команды), а работа с ними
        /// одна на всех.</para>
        /// </summary>
        private sealed class Channel
        {
            public string Name;        // как канал зовут авторские команды
            public AudioSource Src;
            public string Slider;      // каким ползунком его масштабирует игрок
            public bool Loops;         // ведёт непрерывный трек (иначе одиночный звук)
            public bool Authorable;    // слышит команды `audio` из сценария
            public bool Sustained;     // громкостью источника владеем мы, а не каждый пуск

            public float Authored = 1f; // что попросил сценарий
            public string PlayingUrl;   // что на нём сейчас звучит
            public int Gen;             // поколение команды: поздняя отменяет раннюю
            public Coroutine Fade;      // живое затухание, если идёт
        }

        private readonly System.Collections.Generic.List<Channel> _all
            = new System.Collections.Generic.List<Channel>();
        private readonly System.Collections.Generic.Dictionary<string, Channel> _byName
            = new System.Collections.Generic.Dictionary<string, Channel>();

        /// <summary>Канал по имени. НЕИЗВЕСТНОЕ ИМЯ — звук: новая команда не
        /// должна звучать мимо настроек только потому, что её не внесли в
        /// таблицу. Правило одно и живёт здесь.</summary>
        private Channel Of(string channel)
            => channel != null && _byName.TryGetValue(channel, out var c) ? c : _byName[LvnVolumes.Sfx];

        /// <summary>Канал, которому адресована АВТОРСКАЯ команда. Не всякий
        /// канал сценарию слышен: у печати, озвучки и интерфейса свой ведущий,
        /// и «audio channel=voice» обязан звучать звуком, а не перебивать
        /// реплику, — ровно так это и работало, пока имена искали ветвлением.
        /// Правило переехало сюда целиком, чтобы таблица не сделала слышимым
        /// то, что слышимым не задумано.</summary>
        private Channel Addressed(string channel)
        {
            var c = Of(channel);
            return c.Authorable ? c : _byName[LvnVolumes.Sfx];
        }

        private Channel Add(string name, string slider, bool loops, bool authorable, bool sustained)
        {
            var src = gameObject.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.loop = loops;
            var ch = new Channel
            {
                Name = name, Src = src, Slider = slider,
                Loops = loops, Authorable = authorable, Sustained = sustained,
            };
            _all.Add(ch);
            _byName[name] = ch;
            return ch;
        }

        /// <summary>Чем кончается затухание. Флага «останавливать ли» не хватило:
        /// печать по концу не останавливают, а СТАВЯТ НА ПАУЗУ (позиция записи
        /// живёт до следующей строки) — и ради одного лишнего исхода рядом
        /// вырос второй, почти такой же механизм затухания со своей корутиной,
        /// своей отменой и своим Lerp.</summary>
        private enum FadeEnd { Keep, Stop, Pause }

        private void StartFade(string channel, AudioSource src, float from, float to, float seconds, FadeEnd end)
        {
            var ch = Of(channel);
            if (ch.Fade != null) StopCoroutine(ch.Fade);
            ch.Fade = StartCoroutine(FadeAudio(src, from, to, seconds, end));
        }

        private void Awake()
        {
            // ТАБЛИЦА КАНАЛОВ — единственное место, где они перечислены.
            // Различия — данные: ползунок, непрерывность, слышит ли сценарий,
            // владеем ли громкостью источника.
            Add(LvnVolumes.Music,   LvnVolumes.Music,   loops: true,  authorable: true,  sustained: true);
            Add(LvnVolumes.Ambient, LvnVolumes.Ambient, loops: true,  authorable: true,  sustained: true);
            Add(LvnVolumes.Sfx,     LvnVolumes.Sfx,     loops: false, authorable: true,  sustained: true);
            Add(LvnVolumes.Voice,   LvnVolumes.Voice,   loops: false, authorable: false, sustained: true);
            // Печать — свой источник: PlayOneShot не остановить, а стук живёт
            // ровно столько, сколько проявляется строка. Канал интерфейса не
            // годится — щелчок по нему прозвучал бы прямо поверх печати.
            Add(TypingChannel,      LvnVolumes.Ui,      loops: true,  authorable: false, sustained: true);
            // Интерфейс — одиночные пуски: громкость даётся каждому выстрелу,
            // и домножать её ещё и на источник значило бы применить ползунок
            // дважды.
            Add(LvnVolumes.Ui,      LvnVolumes.Ui,      loops: false, authorable: false, sustained: false);
            LvnPrefs.Changed += ApplyUserVolumes;
        }

        private void OnDestroy() => LvnPrefs.Changed -= ApplyUserVolumes;

        // Пересчитать живые источники, когда игрок двинул ползунок или щёлкнул
        // общим тумблером. Затухание в полёте держит свою цель (щёлкнет на
        // следующей команде) — для правки настроек это верно.
        private void ApplyUserVolumes()
        {
            // Авторская громкость (что просил сценарий) × пользовательская
            // (что выставил игрок). Вторая половина — у Звукорежиссёра.
            //
            // ОБХОД, А НЕ СПИСОК. Списком тут дважды забывали строку: сперва
            // голос (звучал мимо своего ползунка), потом печать (выключенный
            // посреди реплики звук стучал до её конца). Канал, добавленный в
            // таблицу, пересчитывается сам.
            foreach (var ch in _all)
                if (ch.Sustained && ch.Src != null)
                    ch.Src.volume = ch.Authored * LvnVolumes.Of(ch.Slider);
        }

        /// <summary>True while a voice-over line is speaking — the stage mutes the
        /// typewriter blip under it.</summary>
        public bool VoicePlaying => Voice.Src != null && Voice.Src.isPlaying;

        private Channel Voice => _byName[LvnVolumes.Voice];

        /// <summary>Voice the line on screen: stop the previous one (voice never
        /// overlaps itself) and play the clip at the player's voice volume. A null/
        /// missing url or a failed load is silence — unvoiced novels no-op. The
        /// generation guard drops a slow load that finishes after the NEXT line
        /// already started (or stopped) its own voice.</summary>
        public async Task PlayVoiceAsync(string url, ILvnAssets assets, CancellationToken ct)
        {
            var voice = Voice;
            int gen = ++voice.Gen;
            if (voice.Src != null) voice.Src.Stop();
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
            if (clip == null && voice.Src != null && gen == voice.Gen)
                Lvn.Content.ContentLoader.NoteAssetUnusable(url, "озвучка не стала клипом");
            if (clip == null || voice.Src == null || gen != voice.Gen) return;
            voice.Src.clip = clip;
            // ЧЕРЕЗ ЗВУКОРЕЖИССЁРА, а не с ползунка напрямую: здесь терялся
            // общий тумблер, и при выключенном звуке реплика всё равно
            // звучала — до ближайшего пересчёта громкостей.
            voice.Src.volume = LvnVolumes.Of(voice.Slider);
            voice.Src.Play();
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
            // ОБХОД, А НЕ СПИСОК — ровно та строка, которой здесь не было.
            // Непрерывное гаснет плавно (обрыв на полутакте слышен как сбой),
            // короткий звук обрывается: доигрывать нечему.
            foreach (var ch in _all)
                if (ch.Authorable) Silence(ch.Name, ch.Loops ? fade : 0f);
        }

        private void Silence(string channel, float fade)
        {
            var ch = Of(channel);
            var src = ch.Src;
            if (src == null) return;
            ch.Gen++;                    // команда в полёте теряет право на канал
            ch.PlayingUrl = null;
            if (fade > 0f && src.isPlaying) StartFade(channel, src, src.volume, 0f, fade, FadeEnd.Stop);
            else { CancelFade(channel); src.Stop(); }
        }

        public void StopVoice()
        {
            var voice = Voice;
            voice.Gen++;
            if (voice.Src != null) voice.Src.Stop();
        }

        /// <summary>Play a UI one-shot (tap / choice / typewriter blip) on a channel
        /// of its own, so a blip never cuts a story sfx. Scaled by the player's SFX
        /// preference; a null clip no-ops (a novel without UI audio stays silent).</summary>
        public void PlayUi(AudioClip clip, float volume = 1f)
        {
            var ui = _byName[LvnVolumes.Ui];
            if (clip == null || ui.Src == null || !LvnPrefs.SoundOn) return;
            // Через дом громкости: прямой ползунок не знал про общий тумблер,
            // и «звук выключен» глушил историю, но не интерфейс.
            ui.Src.PlayOneShot(clip, Mathf.Clamp01(volume) * LvnVolumes.Of(ui.Slider));
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
        private Channel Typing => _byName[TypingChannel];
        // Авторская громкость печати живёт в её канале — чтобы ползунок и
        // тумблер домножались на неё ЖИВЬЁМ: раньше она вычислялась один раз
        // на старте строки, и выключенный посреди длинной реплики звук стучал
        // до её конца.
        private const string TypingChannel = "typing";

        /// <summary>Клавиатура стучит, пока строка печатается: продолжение с
        /// места паузы, фейд-ин. Идемпотентно для уже стучащего лупа.</summary>
        public void PlayTypingLoop(AudioClip clip, float volume = 1f)
        {
            if (clip == null || !LvnPrefs.SoundOn) return;
            var typing = Typing;
            var src = typing.Src;
            if (src == null) return;
            if (src.clip != clip)
            {
                src.clip = clip;
                src.Stop();
                src.volume = 0f;
            }
            if (!src.isPlaying)
            {
                src.UnPause();                    // с места паузы…
                if (!src.isPlaying) src.Play();   // …или первый запуск
            }
            typing.Authored = Mathf.Clamp01(volume);
            StartFade(TypingChannel, src, src.volume,
                      typing.Authored * LvnVolumes.Of(typing.Slider), 0.09f, FadeEnd.Keep);
        }

        /// <summary>Строка допечаталась (или её докрутили тапом) — фейд-аут и
        /// ПАУЗА: позиция записи сохраняется до следующей печати.</summary>
        public void StopTypingLoop()
        {
            var src = Typing.Src;
            if (src == null || !src.isPlaying) return;
            StartFade(TypingChannel, src, src.volume, 0f, 0.18f, FadeEnd.Pause);
        }

        /// <summary>Apply one <c>audio</c> command. Missing audio is silent — a host
        /// that ships no sound simply no-ops. <paramref name="ct"/> cancels the
        /// in-flight clip load with the chapter.</summary>
        public async Task ApplyAsync(JObject cmd, ILvnAssets assets, CancellationToken ct)
        {
            var ch = Addressed((string)cmd["channel"]);
            var channel = ch.Name;
            var src = ch.Src;
            float fade = NumOr(cmd["fade"], 0f);
            int gen = ++ch.Gen; // this command now owns the channel

            if ((string)cmd["action"] == "stop")
            {
                ch.PlayingUrl = null;
                if (fade > 0f) StartFade(channel, src, src.volume, 0f, fade, FadeEnd.Stop);
                else { CancelFade(channel); src.Stop(); }
                return;
            }

            var url = (string)cmd["url"];
            if (assets == null || string.IsNullOrEmpty(url)) return;

            float volume = NumOr(cmd["volume"], 1f);
            ch.Authored = volume;
            float effective = volume * LvnVolumes.Of(ch.Slider);

            // Idempotent for looping channels: the same track already playing (a
            // load/rollback replay) keeps its position — only the volume updates.
            // «Непрерывный» — свойство канала, а не «всё, кроме звука»: второе
            // читается как правило про sfx и разъезжается, как только каналов
            // становится больше трёх.
            if (ch.Loops && src.isPlaying && ch.PlayingUrl == url)
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
            if (ch.Gen != gen) return;

            if (ch.Loops)
            {
                src.loop = BoolOr(cmd["loop"], true);
                ch.PlayingUrl = url;
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
            var ch = Of(channel);
            if (ch.Fade != null) StopCoroutine(ch.Fade);
            ch.Fade = null;
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
