using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Lvn.Tests.Runtime
{
    /// <summary>
    /// ОДНА ТЕМА НА ДВЕ ГЛАВЫ ЗВУЧИТ ОДНОЙ ТЕМОЙ.
    ///
    /// <para>Главы у нас идут встык: дочитал — и следующая начинается сама,
    /// без экрана между ними («Without it chapters flow seamlessly»). Автор,
    /// написавший в обеих главах одну и ту же музыку, обещает игроку
    /// непрерывность — и ровно её слышно, если обещание не держится: тема
    /// обрывается и начинается с первого такта на каждой границе.</para>
    ///
    /// <para>Проверяется СЛЫШИМОЕ, двумя независимыми мерками: сколько раз
    /// трек был загружен (перезапуск обязан сходить за клипом снова) и с
    /// какого места он звучит после границы. Одной позиции мало: если в
    /// прогоне без звуковой карты время не идёт, позиция врала бы «ноль
    /// против нуля» и объявляла бы разрыв непрерывностью. Поэтому стенд
    /// сначала доказывает, что умеет мерить время, и лишь потом судит.</para>
    /// </summary>
    public class ChapterSeamAudioTests
    {
        private const string Тема = "/content/audio/тема.ogg";

        private static string Глава(string сцена) => @"{""scene"":""" + сцена + @""",""script"":[
            {""op"":""audio"",""channel"":""music"",""url"":""" + Тема + @""",""loop"":true},
            {""op"":""say"",""text"":""реплика""}]}";

        /// <summary>Провайдер, который СЧИТАЕТ походы за треком: перезапуск темы
        /// виден по второму походу, даже когда время в прогоне стоит.</summary>
        private sealed class СчётныйКлип : ILvnAssets
        {
            private readonly AudioClip _clip;
            public int Загрузок;
            public СчётныйКлип(AudioClip clip) { _clip = clip; }
            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct) => Task.FromResult<Sprite>(null);
            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct)
            { Загрузок++; return Task.FromResult(_clip); }
            public Task PreloadAsync(IReadOnlyList<string> urls, string kind, CancellationToken ct) => Task.CompletedTask;
            public void Unload(string url) { }
            public void UnloadAll() { }
        }

        private GameObject _go;
        private PanelSettings _panel;
        private VnStage _stage;
        private AudioClip _clip;
        private СчётныйКлип _assets;
        private bool _soundWas;

        [UnitySetUp]
        public IEnumerator Стенд()
        {
            _soundWas = LvnPrefs.SoundOn;
            LvnPrefs.SoundOn = true;
            _stage = TestStage.Panel("chapter-seam-audio", out _go, out _panel);
            // Пять секунд — чтобы позиция успела заметно уйти от нуля и трек
            // не кончился сам, пока стенд ждёт.
            _clip = AudioClip.Create("тема", 44100 * 5, 1, 44100, false);
            _assets = new СчётныйКлип(_clip);
            _stage.Assets = _assets;
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator Уборка()
        {
            _stage?.ClearStage();
            _stage = null;
            yield return null;
            if (_go != null) Object.Destroy(_go);
            if (_panel != null) Object.Destroy(_panel);
            if (_clip != null) Object.DestroyImmediate(_clip);
            LvnPrefs.SoundOn = _soundWas;
            yield return null;
        }

        private AudioSource Музыка()
        {
            if (_go == null) return null;
            foreach (var s in _go.GetComponentsInChildren<AudioSource>())
                if (s.clip == _clip) return s;
            return null;
        }

        private IEnumerator Ждём(System.Func<bool> готово, float секунд)
        {
            float срок = Time.realtimeSinceStartup + секунд;
            while (Time.realtimeSinceStartup < срок && !готово()) yield return null;
        }

        [UnityTest]
        public IEnumerator ТемаНеНачинаетсяСНачалаНаГраницеГлав()
        {
            _stage.SetSaveContext("стенд-шов-звука", "ch1", "/content/scripts/шов-ch01.lvn");
            _stage.Play(Глава("первая"));
            yield return Ждём(() => Музыка() != null && Музыка().isPlaying, 5f);

            var src = Музыка();
            Assert.IsNotNull(src, "стенд: тема первой главы не зазвучала — мерить нечего");
            int загрузокПосле1 = _assets.Загрузок;

            // Даём теме отойти от нуля. Если время не идёт — судить нечем, и
            // стенд обязан сказать это, а не выдать ноль за непрерывность.
            yield return Ждём(() => src.time > 0.2f, 5f);
            float было = src.time;
            Assert.Greater(было, 0.05f,
                "стенд не смог измерить позицию трека (время стоит) — вердикт о непрерывности "
                + "был бы «ноль против нуля», то есть выдумкой");

            // ── ГРАНИЦА ГЛАВЫ ───────────────────────────────────────────────
            _stage.SetSaveContext("стенд-шов-звука", "ch2", "/content/scripts/шов-ch02.lvn");
            _stage.Play(Глава("вторая"));
            yield return Ждём(() => Музыка() != null && Музыка().isPlaying, 5f);

            var после = Музыка();
            Assert.IsNotNull(после, "после границы тема замолчала совсем");
            float стало = после.time;
            int загрузокПосле2 = _assets.Загрузок;

            // Обе мерки называются В ОДНОМ сообщении: первый же Assert обрывает
            // тест, и вердикт, сложенный из двух чисел, иначе теряет половину.
            string замер = $"походов за треком {загрузокПосле1} → {загрузокПосле2}, "
                         + $"позиция {было:F2} с → {стало:F2} с";
            // Числа называются ВСЕГДА, а не только на красном: вердикт «держит»
            // без замера — это мнение, и через месяц его нечем подтвердить.
            Debug.Log("[lvn-audio-seam] " + замер);
            Assert.AreEqual(загрузокПосле1, загрузокПосле2,
                "та же тема во второй главе была загружена заново — она играет с первого такта; "
                + замер);
            Assert.GreaterOrEqual(стало, было * 0.5f,
                "тема откатилась к началу — игрок слышит обрыв и повтор вступления на каждой "
                + "границе глав; " + замер);
        }

        /// <summary>ВЫХОД В МЕНЮ — ДРУГОЕ ДЕЛО. Непрерывность через границу
        /// глав не имеет права воскресить старую беду: тему главы, звучащую в
        /// меню поверх витринного трека. Стенд держит обе стороны сразу.</summary>
        [UnityTest]
        public IEnumerator ПослеГлавыТемаНеЗвучитВечно()
        {
            _stage.SetSaveContext("стенд-шов-звука", "ch1", "/content/scripts/шов-ch01.lvn");
            _stage.Play(Глава("первая"));
            yield return Ждём(() => Музыка() != null && Музыка().isPlaying, 5f);
            Assert.IsNotNull(Музыка(), "стенд: тема не зазвучала");

            _stage.ClearStage();   // так уходит глава, когда игрок вышел в меню
            yield return Ждём(() => Музыка() == null || !Музыка().isPlaying, 5f);

            var src = Музыка();
            Assert.IsTrue(src == null || !src.isPlaying,
                "тема главы продолжает звучать после выхода — в меню она наложится на витринный трек");
        }
    }
}
