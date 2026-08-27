using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Lvn.Tests.Runtime
{
    /// <summary>
    /// ГЕРОЙ ИЗ БЛОКА <c>cast</c> ОДЕВАЕТСЯ ПО ТЕМ ЖЕ ПРАВИЛАМ, что и герой из
    /// каталога.
    ///
    /// <para>Живой дефект, найденный перепроверкой ролей: путь <c>cast</c> брал
    /// СЫРЫЕ оси команды, минуя Костюмера. На такого героя не действовали ни
    /// переменные — <c>{var}</c> уезжал в имя файла как есть, — ни гардероб:
    /// примерка и надетое до него просто не доходили. Два пути одевали героя по
    /// разным правилам, а отличались одной буквой в имени метода.</para>
    ///
    /// <para>Проверяется по запрошенным URL: что именно сцена пошла грузить —
    /// единственный честный ответ на вопрос «во что она его одела».</para>
    /// </summary>
    public class CastDressingTests
    {
        private const string Entity = "test_cast_hero";

        private sealed class UrlSpy : ILvnAssets
        {
            public readonly List<string> Asked = new List<string>();
            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct)
            {
                lock (Asked) Asked.Add(url);
                return Task.FromResult<Sprite>(null);
            }
            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct) => Task.FromResult<AudioClip>(null);
            public Task PreloadAsync(IReadOnlyList<string> urls, string kind, CancellationToken ct)
            {
                if (urls != null) lock (Asked) Asked.AddRange(urls);
                return Task.CompletedTask;
            }
            public void Unload(string url) { }
            public void UnloadAll() { }
        }

        private GameObject _go;
        private VnStage _stage;
        private UrlSpy _spy;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("cast-stage", typeof(UIDocument));
            var doc = _go.GetComponent<UIDocument>();
            doc.panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            _spy = new UrlSpy();
            _stage = _go.AddComponent<VnStage>();
            _stage.Assets = _spy;
            LvnWardrobe.Clear(Entity);
            LvnWardrobe.ClearPreview(Entity);
        }

        [TearDown]
        public void TearDown()
        {
            LvnWardrobe.ClearPreview(Entity);
            LvnWardrobe.Clear(Entity);
            Object.Destroy(_go);
        }

        // Сущность блока `cast`: слой облика собирается из оси, которую сценарий
        // здесь НЕ называет — значит одеть героя может только гардероб.
        private static string CastDoc() => @"{
            ""cast"": { """ + Entity + @""": { ""layers"": [ ""hero/{outfit}.png"" ] } },
            ""script"": [
                {""op"":""actor"",""id"":""" + Entity + @""",""show"":true},
                {""op"":""say"",""text"":""одет""}
            ]}";

        private bool AskedFor(string url)
        {
            lock (_spy.Asked) return _spy.Asked.Contains(url);
        }

        [UnityTest]
        public IEnumerator WardrobeDressesACastHero()
        {
            LvnWardrobe.Equip(Entity, "outfit", "gala");

            _stage.Play(CastDoc());
            yield return new WaitForSecondsRealtime(0.6f);

            Assert.IsTrue(AskedFor("hero/gala.png"),
                "надетое обязано доехать до героя из cast так же, как до героя из каталога");
        }

        [UnityTest]
        public IEnumerator PreviewBeatsEquipped_OnACastHeroToo()
        {
            LvnWardrobe.Equip(Entity, "outfit", "gala");
            LvnWardrobe.Preview(Entity, "outfit", "beach");

            _stage.Play(CastDoc());
            yield return new WaitForSecondsRealtime(0.6f);

            Assert.IsTrue(AskedFor("hero/beach.png"),
                "игрок крутит карусель — сцена показывает примерку");
            Assert.IsFalse(AskedFor("hero/gala.png"),
                "надетое под примеркой рисоваться не должно");
        }

        [UnityTest]
        public IEnumerator AnUnresolvedTokenNeverReachesTheFileName()
        {
            // Ничего не надето и не примерено — ось не разрешается ничем.
            _stage.Play(CastDoc());
            yield return new WaitForSecondsRealtime(0.6f);

            Assert.IsFalse(AskedFor("hero/{outfit}.png"),
                "нераскрытый {токен} в имени файла — это 404 на проде, а не слой");
        }
    }
}
