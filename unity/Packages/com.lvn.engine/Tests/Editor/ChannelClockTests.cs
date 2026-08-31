using System.Collections.Generic;
using Lvn.Content;
using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ЧАСЫ КАНАЛА — где анимация находится ПРЯМО СЕЙЧАС
    /// (<see cref="ActorAnimator.ChannelClock"/> и <c>ClockOf</c>).
    ///
    /// <para>Между «сколько прошло секунд» и «какое значение брать у дорожки»
    /// лежат три решения, и все три — про ВРЕМЯ, а не про то, что
    /// анимируют: закольцована ли анимация (и качается ли туда-обратно),
    /// доиграла ли она, и есть ли у неё ПУТЬ — пара сплайновых дорожек
    /// screen_x/screen_y, вдоль которой фигура обязана ехать с постоянной
    /// СКОРОСТЬЮ, а не с постоянным приростом параметра.</para>
    ///
    /// <para>Эти решения были записаны ДВАЖДЫ дословно — у плоской фигуры и у
    /// трёхмерной. Расхождение в них не падает и даже не видно на глаз:
    /// движение просто идёт «не так» — рывками, не тем концом петли, с
    /// разворотом в другую сторону. Такое ищут неделями, поэтому у времени
    /// канала один дом, и вот его правила.</para>
    /// </summary>
    public class ChannelClockTests
    {
        private static List<object[]> K(params object[][] keys) => new List<object[]>(keys);

        /// <summary>Одна дорожка: свойство, способ соединения ключей и сами
        /// ключи «[время, значение]».</summary>
        private static LvnAnimTrack Tr(string prop, string interp, params object[][] keys)
            => new LvnAnimTrack { prop = prop, interp = interp, keys = K(keys) };

        private static LvnAnim Anim(bool loop, bool yoyo, float dur, params LvnAnimTrack[] tracks)
            => new LvnAnim { loop = loop, yoyo = yoyo, duration = dur, tracks = new List<LvnAnimTrack>(tracks) };

        /// <summary>Часы на свежем канале: таблица длины дуги у него ещё не
        /// построена, как и у только что запущенной анимации.</summary>
        private static ActorAnimator.ChannelClock Clock(LvnAnim anim, float elapsed)
        {
            float[] cache = null;
            return ActorAnimator.ClockOf(anim, elapsed, ref cache);
        }

        /// <summary>Ровный по времени путь из угла в угол — по нему часы
        /// проверяются там, где важна не кривая, а само опознание пары.</summary>
        private static LvnAnim Path(string interpX, string interpY, string layerY = null)
        {
            var x = Tr("screen_x", interpX, new object[] { 0f, 0f }, new object[] { 1f, 1f });
            var y = Tr("screen_y", interpY, new object[] { 0f, 0f }, new object[] { 1f, 1f });
            y.layer = layerY;
            return Anim(false, false, 1f, x, y);
        }

        // ── кольцо, качели и конец ──────────────────────────────────────────

        // Одноразовый жест обязан ЗАКОНЧИТЬСЯ: по этому признаку канал
        // снимается, зовётся onDone и стартует следующий шаг очереди. Не приди
        // он — дорожка занята навсегда: `move` с ожиданием висит, очередь
        // `mode=queue` не двигается, планировщик тикает до конца главы. А время
        // при этом зажимается концом, а не бежит дальше: по нему берут наклон
        // пути для разворота и выправленное время дуги, и за концом обе
        // величины смысла не имеют.
        [Test]
        public void ОдноразоваяЗажимаетсяКонцомИОбъявляетсяДоигравшей()
        {
            var wave = Anim(loop: false, yoyo: false, dur: 1f,
                Tr("rotation", null, new object[] { 0f, 0f }, new object[] { 1f, 20f }));

            Assert.AreEqual(0.4f, Clock(wave, 0.4f).T, 0.001f, "время середины жеста поехало не по стенным часам");
            Assert.IsFalse(Clock(wave, 0.4f).Finished, "жест объявлен доигравшим на середине");

            Assert.IsTrue(Clock(wave, 1f).Finished,
                "ровно конец — уже конец: жест, доигравший точно в кадр, не отпустил канал");
            Assert.AreEqual(1f, Clock(wave, 5f).T, 0.001f,
                "время ушло за конец — фигура берёт наклон пути и дугу там, где анимации уже нет");
            Assert.IsTrue(Clock(wave, 5f).Finished, "давно доигравший жест держит канал занятым");
        }

        // Закольцованная — это дыхание, покой, мигание: она не кончается по
        // замыслу. Объяви её доигравшей — канал снимут, фигура замрёт в
        // случайной точке цикла, а onDone разбудит шаг, который ждал совсем
        // другого.
        [Test]
        public void ЗакольцованнаяНеЗаканчиваетсяНикогда()
        {
            var idle = Anim(loop: true, yoyo: false, dur: 1f,
                Tr("y", null, new object[] { 0f, 0f }, new object[] { 1f, 0.01f }));

            Assert.IsFalse(Clock(idle, 1f).Finished, "покой объявлен доигравшим на первом же круге");
            Assert.IsFalse(Clock(idle, 1000.5f).Finished, "покой объявлен доигравшим — фигура замрёт посреди вдоха");
            Assert.AreEqual(0.5f, Clock(idle, 1000.5f).T, 0.001f,
                "закольцованное время не пошло по кругу: цикл замер на последнем кадре");
        }

        // Качели — способ сделать дыхание одной дорожкой: туда и обратно.
        // Обычное кольцо на её месте каждый круг прыгает из конца в начало —
        // фигура дёргается раз в цикл, и это видно всю сцену.
        [Test]
        public void КачелиИдутНазадАНеРестартом()
        {
            var tracks = new[] { Tr("rotation", null, new object[] { 0f, 0f }, new object[] { 1f, 20f }) };
            var yoyo = Anim(loop: true, yoyo: true, dur: 1f, tracks);
            var plain = Anim(loop: true, yoyo: false, dur: 1f, tracks);

            Assert.AreEqual(0.25f, Clock(yoyo, 0.25f).T, 0.001f, "первый проход качелей идёт вперёд");
            Assert.AreEqual(1f, Clock(yoyo, 1f).T, 0.001f, "верхняя точка качелей — конец дорожки");
            Assert.AreEqual(0.75f, Clock(yoyo, 1.25f).T, 0.001f, "после верхней точки качели обязаны пойти НАЗАД");
            Assert.AreEqual(0.1f, Clock(yoyo, 1.9f).T, 0.001f, "к концу обратного хода качели почти в начале");
            Assert.AreEqual(0f, Clock(yoyo, 2f).T, 0.001f, "полный цикл качелей возвращает в начало");

            Assert.AreEqual(0.9f, Clock(plain, 1.9f).T, 0.001f,
                "обычное кольцо обязано именно рестартовать — иначе качели не отличить от него");
        }

        // yoyo уточняет КОЛЬЦО, а не заменяет его. Начни он качать одноразовый
        // жест — рука поехала бы обратно ровно в те кадры, в которые жест
        // снимают: замах есть, удара нет.
        [Test]
        public void КачелиБезКольцаОстаютсяОдноразовымЖестом()
        {
            var gesture = Anim(loop: false, yoyo: true, dur: 1f,
                Tr("rotation", null, new object[] { 0f, 0f }, new object[] { 1f, 20f }));

            Assert.AreEqual(1f, Clock(gesture, 1.9f).T, 0.001f,
                "незакольцованный жест поехал обратно — фигура дёргается в конце движения");
            Assert.IsTrue(Clock(gesture, 1.9f).Finished, "жест не отпустил канал");
        }

        // На длину ДЕЛЯТ: выправление времени по дуге считает долю t/dur, шаг
        // для наклона пути берут от неё же. Ноль в знаменателе даёт NaN, а NaN,
        // дошедший до стиля, убирает фигуру с экрана целиком — не «поехала не
        // так», а «пропала». Ноль приходит из сценария (мгновенная
        // перестановка) и из каталога, где поле просто не заполнили.
        [Test]
        public void ДлительностьНеМеньшеМгновения()
        {
            var instant = Path("spline", "spline");
            instant.duration = 0f;

            var clock = Clock(instant, 0f);
            Assert.Greater(clock.Duration, 0f, "длина анимации — ноль, и на неё сейчас разделят");
            Assert.IsFalse(float.IsNaN(clock.T) || float.IsInfinity(clock.T), "время канала стало нечислом");
            Assert.IsFalse(float.IsNaN(clock.PathT) || float.IsInfinity(clock.PathT),
                "выправленное время стало нечислом — фигура пропадёт с экрана");
            Assert.IsTrue(Clock(instant, 0.016f).Finished,
                "мгновенная перестановка не заканчивается — канал занят навсегда");
        }

        // ── путь: пара дорожек, а не одна и не любая ────────────────────────

        // Путь — это ОБЕЩАНИЕ: «фигура едет по кривой, и скорость вдоль неё
        // ровная». Опознать его там, где его нет, значит выправить время
        // дорожке, которую автор написал прямой: она поедет не в те моменты, а
        // сглаживание применится дважды. Пропустить настоящий — вернуть рывки.
        // Худший случай — дорожка без ключей: по паре строят таблицу длины
        // выборкой обеих, и пустая роняет всю анимацию.
        [Test]
        public void ПутьОпознаётсяТолькоПоПолнойСплайновойПареБезСлоя()
        {
            Assert.IsTrue(Clock(Path("spline", "spline"), 0.5f).ArcPath,
                "пара сплайновых дорожек экрана — это путь, и он не опознан");

            var прямая = Clock(Path("spline", null), 0.5f);
            Assert.IsFalse(прямая.ArcPath, "путём объявлена пара, где вторая дорожка прямая");

            Assert.IsFalse(Clock(Path(null, null), 0.5f).ArcPath, "путём объявлена пара прямых дорожек");

            Assert.IsFalse(Clock(Path("spline", "spline", layerY: "hair"), 0.5f).ArcPath,
                "дорожка СЛОЯ считана как путь фигуры: слой ездит внутри фигуры, а не по экрану");

            var одна = Anim(false, false, 1f,
                Tr("screen_x", "spline", new object[] { 0f, 0f }, new object[] { 1f, 1f }));
            Assert.IsFalse(Clock(одна, 0.5f).ArcPath, "путём объявлена одна дорожка — вдоль чего мерить длину?");

            var безКлючей = Path("spline", "spline");
            безКлючей.tracks[1].keys = null;
            Assert.IsFalse(Clock(безКлючей, 0.5f).ArcPath,
                "путём объявлена пара с пустой дорожкой — таблицу длины строят по её ключам");

            var нетДорожек = new LvnAnim { duration = 1f, tracks = null };
            Assert.IsFalse(Clock(нетДорожек, 0.5f).ArcPath, "анимация без дорожек объявлена путём");
        }

        // Если пути нет, «выправленного времени» тоже нет: КАЖДАЯ дорожка
        // обязана жить по стенным часам, и разворот «лицом по движению» — тоже.
        // Иначе одна половина анимации идёт по одному времени, другая по
        // другому, и фигура разъезжается сама с собой.
        [Test]
        public void БезПутиВсеДорожкиЖивутПоОдномуВремени()
        {
            var anim = Path("spline", null);
            var clock = Clock(anim, 0.5f);

            Assert.AreEqual(clock.T, clock.PathT, 0.0001f, "выправленное время появилось там, где пути нет");
            Assert.AreEqual(clock.T, clock.OrientT, 0.0001f, "разворот считается по чужому времени");
            foreach (var tr in anim.tracks)
            {
                Assert.IsFalse(clock.OnPath(tr), "дорожка причислена к несуществующему пути");
                Assert.AreEqual(clock.T, clock.TimeOf(tr), 0.0001f, "дорожка поехала по своему времени");
            }
        }

        // Ровно то, ради чего выправление и затевалось. Ключи расставлены
        // неровно (длинный отрезок втиснут в первую десятую времени) — по
        // сырому времени фигура выстреливает и потом ползёт. Дорожки ПУТИ
        // берут выправленное время, все прочие (прозрачность, поворот) —
        // стенное: их автор писал по секундам, и подменять им время нельзя.
        [Test]
        public void ДорожкиПутиЖивутПоВыправленномуВремени_ОстальныеПоСтенному()
        {
            var x = Tr("screen_x", "spline",
                new object[] { 0f, 0f }, new object[] { 0.1f, 0.8f }, new object[] { 1f, 1f });
            var y = Tr("screen_y", "spline",
                new object[] { 0f, 0f }, new object[] { 0.1f, 0f }, new object[] { 1f, 0f });
            var alpha = Tr("alpha", null, new object[] { 0f, 1f }, new object[] { 1f, 0f });
            var clock = Clock(Anim(false, false, 1f, x, y, alpha), 0.5f);

            Assert.IsTrue(clock.OnPath(x) && clock.OnPath(y), "дорожки пути не опознаны");
            Assert.IsFalse(clock.OnPath(alpha), "прозрачность причислена к пути");
            Assert.AreEqual(clock.PathT, clock.TimeOf(x), 0.0001f, "дорожка пути поехала по сырому времени");
            Assert.AreEqual(clock.T, clock.TimeOf(alpha), 0.0001f,
                "прозрачности подменили время дугой — угасание пойдёт не по секундам автора");
            Assert.AreEqual(clock.PathT, clock.OrientT, 0.0001f,
                "разворот берёт наклон в точке, где фигуры уже (или ещё) нет");

            Assert.Greater(ActorAnimator.Sample(x, clock.T, easeless: true), 0.85f,
                "по сырому времени середина времени — это почти конец пути (потому дугу и выправляют)");
            Assert.AreEqual(0.5f, ActorAnimator.Sample(x, clock.PathT, easeless: true), 0.07f,
                "половина ВРЕМЕНИ обязана быть половиной ПУТИ — иначе фигура выстреливает и ползёт");
        }

        // Время вдоль пути обязано быть монотонным и доходить до конца. Пятится
        // оно — фигура на кадр отступает назад (читается как рывок), не доходит
        // — останавливается, не добравшись до места, и следующая команда
        // снимает её оттуда. А таблица длины живёт с каналом: ответ не имеет
        // права зависеть от того, спросили ли по ней впервые.
        [Test]
        public void ВыправленноеВремяНеПятитсяИДоводитДоКонца()
        {
            var anim = Anim(false, false, 2f,
                Tr("screen_x", "spline",
                    new object[] { 0f, 0f }, new object[] { 0.2f, 0.7f }, new object[] { 2f, 1f }),
                Tr("screen_y", "spline",
                    new object[] { 0f, 0f }, new object[] { 0.2f, 0.4f }, new object[] { 2f, 0f }));

            float[] cache = null;
            Assert.AreEqual(0f, ActorAnimator.ClockOf(anim, 0f, ref cache).PathT, 0.0001f,
                "путь начинается не с начала");
            Assert.IsNotNull(cache, "таблица длины дуги не построена — выправлять время нечем");
            var built = cache;

            float prev = 0f;
            for (int i = 1; i <= 20; i++)
            {
                float t = 2f * i / 20f;
                float now = ActorAnimator.ClockOf(anim, t, ref cache).PathT;
                Assert.GreaterOrEqual(now, prev - 0.0001f, $"на {t:0.00}с время вдоль пути попятилось — фигура дёрнулась назад");
                prev = now;
            }
            Assert.AreEqual(2f, prev, 0.001f, "путь не доведён до конца — фигура встала, не дойдя до места");
            Assert.AreSame(built, cache, "таблица длины перестраивается на каждом кадре");

            float[] second = null;
            Assert.AreEqual(ActorAnimator.ClockOf(anim, 1f, ref cache).PathT,
                            ActorAnimator.ClockOf(anim, 1f, ref second).PathT, 0.0001f,
                "тёплая таблица отвечает не то же, что холодная, — движение задрожит на первом кадре");
        }

        // Таблица в 64 точки строится выборкой ОБЕИХ дорожек, то есть сотней
        // вычислений сплайна. Анимации без пути она не нужна вовсе — а строится
        // она по паре, которой у такой анимации нет.
        [Test]
        public void БезПутиТаблицаДлиныНеСтроится()
        {
            var idle = Anim(true, false, 1f, Tr("y", null, new object[] { 0f, 0f }, new object[] { 1f, 0.01f }));

            float[] cache = null;
            ActorAnimator.ClockOf(idle, 0.5f, ref cache);

            Assert.IsNull(cache, "для анимации без пути построена таблица длины дуги");
        }
    }
}
