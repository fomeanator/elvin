using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>
    /// ПОБЕЖДАЕТ ПОСЛЕДНЯЯ ПРОСЬБА, А НЕ ПОСЛЕДНИЙ ОТВЕТ.
    ///
    /// <para>Живой дефект: один и тот же элемент просят показать разное
    /// быстрее, чем доезжает первое — игрок листает галерею стрелкой, тапает
    /// свотчи цвета волос, перелистывает карточки. Побеждала не последняя
    /// просьба, а та, что доехала позже: картинка от одной сцены под подписью
    /// от другой. Заметно это только на медленной сети, то есть у игрока и
    /// никогда у того, кто проверял.</para>
    ///
    /// <para>Проверить это можно единственным способом: заставить ПЕРВУЮ
    /// загрузку ответить ПОСЛЕ второй. Отсюда заглушка с ручным ответом —
    /// <see cref="TestAssets"/> отвечает мгновенно и гонку не воспроизводит
    /// вовсе.</para>
    /// </summary>
    public sealed class PictureAssignTests
    {
        /// <summary>Загрузчик, который отвечает ТОГДА, КОГДА СКАЖУТ: просьба
        /// запоминается, ответ отдаётся отдельным вызовом. Это и есть медленная
        /// сеть, только управляемая.</summary>
        private sealed class HeldAssets : ILvnAssets
        {
            private readonly Dictionary<string, TaskCompletionSource<Sprite>> _held
                = new Dictionary<string, TaskCompletionSource<Sprite>>();

            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct)
            {
                var tcs = new TaskCompletionSource<Sprite>(TaskCreationOptions.RunContinuationsAsynchronously);
                _held[url] = tcs;
                return tcs.Task;
            }

            /// <summary>Ответить на просьбу — в любом порядке, хоть задом наперёд.</summary>
            public void Answer(string url, Sprite sprite)
            {
                Assert.IsTrue(_held.ContainsKey(url), $"загрузку «{url}» никто не просил");
                _held[url].TrySetResult(sprite);
            }

            public bool Asked(string url) => _held.ContainsKey(url);

            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct)
                => Task.FromResult<AudioClip>(null);
            public Task PreloadAsync(IReadOnlyList<string> urls, string kind, CancellationToken ct)
                => Task.CompletedTask;
            public void Unload(string url) { }
            public void UnloadAll() { }
        }

        private readonly List<UnityEngine.Object> _spawned = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _spawned)
                if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _spawned.Clear();
        }

        private Sprite Art()
        {
            var tex = new Texture2D(2, 2);
            var sprite = Sprite.Create(tex, new Rect(0, 0, 2, 2), new Vector2(0.5f, 0.5f));
            _spawned.Add(sprite);
            _spawned.Add(tex);
            return sprite;
        }

        private static Sprite Shown(VisualElement el) => el.style.backgroundImage.value.sprite;

        [Test]
        public async Task ПобеждаетПоследняяПросьбаАНеПоследнийОтвет()
        {
            var el = new VisualElement();
            var assets = new HeldAssets();
            Sprite page1 = Art(), page2 = Art();

            // Игрок листнул дважды подряд: две просьбы на один и тот же элемент.
            Task first = LvnPicture.AssignAsync(el, "page-1.png", assets);
            Task second = LvnPicture.AssignAsync(el, "page-2.png", assets);

            // А сеть отдала их ЗАДОМ НАПЕРЁД — вторая доехала раньше первой.
            assets.Answer("page-2.png", page2);
            await second;
            assets.Answer("page-1.png", page1);
            await first;

            Assert.AreSame(page2, Shown(el),
                "элемент показал картинку от ПРОШЛОЙ просьбы: пришедший позже ответ " +
                "на отменённую просьбу не имеет права перекрасить элемент");
        }

        [Test]
        public async Task ЕдинственнаяЗагрузкаДоезжаетИПрименяется()
        {
            // Обратная сторона того же правила: защита от устаревшего ответа не
            // должна съесть ОБЫЧНЫЙ — иначе картинка не появится вообще.
            var el = new VisualElement();
            var assets = new HeldAssets();
            var cover = Art();

            Task load = LvnPicture.AssignAsync(el, "cover.png", assets);
            Assert.IsNull(Shown(el), "картинка встала до того, как её отдали");

            assets.Answer("cover.png", cover);
            await load;

            Assert.AreSame(cover, Shown(el), "единственная загрузка не доехала до элемента");
        }

        [Test]
        public async Task ПропавшийАртОставляетТоЧтоУжеСтоит()
        {
            // Отсутствующий арт — не беда: элемент остаётся с тем, что у него
            // было. Стереть показанное значило бы менять живую картинку на
            // пустое место из-за одного 404.
            var el = new VisualElement();
            var assets = new HeldAssets();
            var cover = Art();

            Task load = LvnPicture.AssignAsync(el, "cover.png", assets);
            assets.Answer("cover.png", cover);
            await load;

            Task missing = LvnPicture.AssignAsync(el, "gone.png", assets);
            assets.Answer("gone.png", null);
            await missing;

            Assert.AreSame(cover, Shown(el), "пропавший арт стёр то, что уже показывали");
        }

        [Test]
        public async Task ПустаяПросьбаНичегоНеТрогаетИНеПадает()
        {
            var el = new VisualElement();
            var assets = new HeldAssets();
            var cover = Art();

            Task load = LvnPicture.AssignAsync(el, "cover.png", assets);
            assets.Answer("cover.png", cover);
            await load;

            await LvnPicture.AssignAsync(el, "", assets);
            await LvnPicture.AssignAsync(el, null, assets);
            await LvnPicture.AssignAsync(null, "cover.png", assets);
            await LvnPicture.AssignAsync(el, "cover.png", null);

            Assert.IsFalse(assets.Asked(""), "пустой адрес ушёл в загрузчик");
            Assert.AreSame(cover, Shown(el), "просьба ни о чём стёрла показанное");
        }
    }
}
