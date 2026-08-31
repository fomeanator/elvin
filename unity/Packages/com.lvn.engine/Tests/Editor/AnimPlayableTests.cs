using System.Collections.Generic;
using Lvn.Content;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// <summary>
    /// ЕСТЬ ЛИ ЧТО ИГРАТЬ — <see cref="LvnAnim.Playable"/>.
    ///
    /// <para>Пустая анимация — не ошибка, а обычное дело: автор объявил её и не
    /// заполнил, имя не нашлось в каталоге, переимпорт вычистил дорожки.
    /// Играть её нечем, и вопрос «а есть ли чем» задают перед каждым запуском —
    /// проверка стояла ЧЕТЫРЕЖДЫ дословно, тремя частями каждая, и стоило
    /// одной копии забыть про пустой СПИСОК дорожек, как пустышка занимала
    /// канал.</para>
    ///
    /// <para>Здесь же закреплено, чем «нечего играть» кончается для игрока:
    /// ответ приходит СРАЗУ, а дорожка остаётся свободной.</para>
    /// </summary>
    public class AnimPlayableTests
    {
        [TearDown]
        public void RestoreClock() => LvnAnimSampler.Clock = () => Time.realtimeSinceStartup;

        /// <summary>Фигура, которой правила и проверяются. Раньше здесь стоял
        /// плоский компоновщик UI Toolkit — вторая, неподключённая реализация
        /// анимации; её удалили, и правила переехали на ту фигуру, которой
        /// движок рисует на самом деле.</summary>
        private static Lvn.UI.World.WorldActor NewActor()
            => new GameObject("anim-probe", typeof(RectTransform))
               .AddComponent<Lvn.UI.World.WorldActor>();

        private static LvnAnim WithTracks(params LvnAnimTrack[] tracks) =>
            new LvnAnim { duration = 1f, tracks = new List<LvnAnimTrack>(tracks) };

        private static LvnAnimTrack SomeTrack() => new LvnAnimTrack
        {
            prop = "rotation",
            keys = new List<object[]> { new object[] { 0f, 0f }, new object[] { 1f, 20f } },
        };

        // Три вида пустоты приходят из разных мест и выглядят по-разному: имя
        // не нашлось в каталоге (анимации нет вовсе), поле tracks не записали
        // (переимпорт, ручная правка манифеста), список объявлен и пуст (автор
        // начал и не дописал). Пропусти любую — и дальше по коду её начнут
        // перебирать: пустой перебор занимает канал и никогда не кончается,
        // потому что кончаться в нём нечему.
        [Test]
        public void ТриВидаПустотыОдинаковоНечегоИграть()
        {
            Assert.IsFalse(LvnAnim.Playable(null), "анимации нет вовсе, а играть её собрались");
            Assert.IsFalse(LvnAnim.Playable(new LvnAnim { duration = 1f }), "дорожек не записали, а играть собрались");
            Assert.IsFalse(LvnAnim.Playable(WithTracks()), "список дорожек пуст, а играть собрались");
            Assert.IsTrue(LvnAnim.Playable(WithTracks(SomeTrack())), "настоящую анимацию объявили пустой");
        }

        // Пустышка обязана ответить СРАЗУ и не занять дорожку. На onDone висит
        // продолжение шага: `move` с ожиданием, следующий шаг очереди, снятие
        // канала. Промолчи запуск — новелла встанет насмерть на опечатке в
        // имени анимации, а это самая частая опечатка вообще.
        [Test]
        public void ЗапускПустышкиОтвечаетСразуИНеЗанимаетДорожку()
        {
            LvnAnimSampler.Clock = () => 0f;
            var a = NewActor();
            bool ответил = false;

            a.Play("gesture", null, () => ответил = true);

            Assert.IsTrue(ответил, "запуск пустой анимации промолчал — шаг с ожиданием повис навсегда");
            Assert.IsFalse(a.Has("gesture"), "пустышка заняла дорожку: настоящий жест на неё уже не встанет");
            Assert.IsNull(a.Current("gesture"), "на дорожке лежит анимация, которой нечего играть");
        }

        // Очередь (`mode=queue`) двигается ТОЛЬКО концом предыдущего шага.
        // Пустой шаг, попавший в неё, кончиться не может — очередь встаёт
        // целиком, и следующие за ним настоящие шаги не сыграют никогда.
        [Test]
        public void ПустойШагВОчередьНеПопадает()
        {
            LvnAnimSampler.Clock = () => 0f;
            var a = NewActor();

            a.PlayQueued("gesture", WithTracks());          // свободная дорожка
            Assert.IsFalse(a.Has("gesture"), "пустой шаг занял свободную дорожку");

            a.Play("gesture", WithTracks(SomeTrack()));
            a.PlayQueued("gesture", null);                   // дорожка занята — шаг встал бы в очередь

            a.Tick(2f);                                      // первая анимация доиграла

            Assert.IsFalse(a.Has("gesture"),
                "после доигравшей анимации дорожку занял пустой шаг из очереди — она больше не освободится");
        }
    }
}
