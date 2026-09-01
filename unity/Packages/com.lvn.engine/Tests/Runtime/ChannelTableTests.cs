using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Lvn.Tests.Runtime
{
    /// <summary>
    /// ТАБЛИЦА КАНАЛОВ ЗВУКА — <see cref="StageAudio"/>.
    ///
    /// <para>Про каждый канал знали пятеро врозь: именованные поля с
    /// источниками, поля авторской громкости, словарь «что звучит», словарь
    /// поколений, словарь затуханий. Цена была не в правке, а в её
    /// незаметности: соответствие «канал → источник» стояло дважды, и в одной
    /// копии забыли озвучку — голос звучал мимо своего ползунка. Уборка кадра
    /// снимала печать и голос, а музыку не снимал никто, и трек главы играл в
    /// меню поверх витринного.</para>
    ///
    /// <para>Проверяется поэтому не устройство, а СЛЫШИМОЕ: что замолкает на
    /// конце главы, что слушается ползунка, куда уходит команда с чужим именем
    /// канала.</para>
    /// </summary>
    public class ChannelTableTests
    {
        private sealed class OneClip : ILvnAssets
        {
            private readonly AudioClip _clip;
            public OneClip(AudioClip clip) { _clip = clip; }
            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct) => Task.FromResult<Sprite>(null);
            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct) => Task.FromResult(_clip);
            public Task PreloadAsync(IReadOnlyList<string> urls, string kind, CancellationToken ct) => Task.CompletedTask;
            public void Unload(string url) { }
            public void UnloadAll() { }
        }

        private GameObject _go;
        private StageAudio _audio;
        private AudioClip _clip;

        private bool _soundWas;

        [SetUp]
        public void Setup()
        {
            _soundWas = LvnPrefs.SoundOn;
            LvnPrefs.SoundOn = true;
            _go = new GameObject("аудио-проба");
            _audio = _go.AddComponent<StageAudio>();
            _clip = AudioClip.Create("проба", 4410, 1, 44100, false);
        }

        [TearDown]
        public void Teardown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            if (_clip != null) Object.DestroyImmediate(_clip);
            LvnPrefs.SoundOn = _soundWas;   // настройка игрока переживает тест
        }

        private IEnumerator Play(string channel, float volume = 1f)
        {
            var cmd = new JObject { ["channel"] = channel, ["url"] = "проба.ogg", ["volume"] = volume };
            var t = _audio.ApplyAsync(cmd, new OneClip(_clip), CancellationToken.None);
            while (!t.IsCompleted) yield return null;
        }

        private static AudioSource Source(GameObject go, AudioClip clip)
        {
            foreach (var s in go.GetComponents<AudioSource>())
                if (s.clip == clip && s.isPlaying) return s;
            return null;
        }

        [UnityTest]
        public IEnumerator Конец_главы_уносит_музыку_а_не_только_печать_с_голосом()
        {
            yield return Play(LvnVolumes.Music);
            Assert.IsNotNull(Source(_go, _clip), "музыка не зазвучала — проверять нечего");

            _audio.SilenceChapter(0f);   // без затухания: результат виден сразу
            yield return null;
            Assert.IsNull(Source(_go, _clip),
                "музыка пережила конец главы — трек будет играть в меню поверх витринного");
        }

        [UnityTest]
        public IEnumerator Команда_с_чужим_именем_канала_звучит_звуком()
        {
            // Сценарию слышны три канала. «voice» ведёт озвучка реплики, и
            // авторская команда не смеет её перебить — она обязана уйти в звук,
            // ровно как любое незнакомое имя.
            yield return Play("voice");
            var src = Source(_go, _clip);
            Assert.IsNotNull(src, "команда никуда не зазвучала");
            Assert.IsFalse(_audio.VoicePlaying,
                "авторская команда захватила канал озвучки — реплику она перебьёт");
        }

        [UnityTest]
        public IEnumerator Ползунок_достаёт_до_каждого_живого_канала()
        {
            yield return Play(LvnVolumes.Music, 0.8f);
            var music = Source(_go, _clip);
            Assert.IsNotNull(music);
            Assert.AreEqual(0.8f, music.volume, 0.001f, "авторская громкость не доехала");

            // Общий тумблер — та самая правка, которую дважды забывали внести
            // строкой: канал, добавленный в таблицу, обязан гаснуть сам.
            LvnPrefs.SoundOn = false;
            yield return null;
            Assert.AreEqual(0f, music.volume, 0.001f,
                "выключенный звук не достал до канала — он не в обходе пересчёта");

            LvnPrefs.SoundOn = true;
            yield return null;
            Assert.AreEqual(0.8f, music.volume, 0.001f,
                "включённый обратно звук не вернул авторскую громкость");
        }

        [UnityTest]
        public IEnumerator Тот_же_трек_не_перезапускается()
        {
            yield return Play(LvnVolumes.Music, 1f);
            var src = Source(_go, _clip);
            Assert.IsNotNull(src);
            src.time = 0.05f;

            yield return Play(LvnVolumes.Music, 0.5f);   // повтор при откате/загрузке
            Assert.AreEqual(0.5f, src.volume, 0.001f, "громкость повтора не применилась");
            Assert.Greater(src.time, 0.01f, "непрерывный трек перезапустился с начала");
        }
    }
}
