using NUnit.Framework;
using Lvn.UI;

namespace Lvn.Tests
{
    /// <summary>
    /// Пружина — единственная часть движения, которую можно проверить числами,
    /// а не глазами. Проверяем три свойства, каждое из которых ломается молча:
    /// приход в цель, проскок (ради него всё и затевалось) и НЕЗАВИСИМОСТЬ ОТ
    /// КАДРА — просадка до 20 fps не должна ни ускорять анимацию, ни взрывать её.
    /// </summary>
    public class LvnMotionTests
    {
        // Прогон пружины на заданной частоте кадров. Возвращает конечное
        // значение и максимум, которого она достигла по пути.
        private static (float final, float peak, int frames) Run(float fps, float damping, float seconds = 3f)
        {
            float v = 0f, vel = 0f, peak = 0f;
            float dt = 1f / fps;
            int frames = 0;
            for (float t = 0; t < seconds; t += dt)
            {
                LvnMotion.Step(ref v, ref vel, 1f, dt, LvnMotion.Stiffness, damping);
                if (v > peak) peak = v;
                frames++;
                if (LvnMotion.AtRest(v, vel, 1f)) break;
            }
            return (v, peak, frames);
        }

        [Test]
        public void ПриходитВЦель()
        {
            var r = Run(60f, LvnMotion.DampingSoft);
            Assert.That(r.final, Is.EqualTo(1f).Within(0.01f),
                "пружина обязана осесть ровно в цель, иначе элемент замрёт в миллиметре от места");
        }

        [Test]
        public void ПроскакиваетЦель_радиЭтогоВсёИЗатевалось()
        {
            var soft = Run(60f, LvnMotion.DampingSoft);
            Assert.That(soft.peak, Is.GreaterThan(1.02f),
                "без проскока движение читается как линейное — то самое «сделано программистом»");
            Assert.That(soft.peak, Is.LessThan(1.20f),
                "проскок больше 20% выглядит клоунадой, а не дороговизной");
        }

        [Test]
        public void ЖёсткаяПружинаНеОтскакивает()
        {
            var firm = Run(60f, LvnMotion.DampingFirm);
            Assert.That(firm.peak, Is.LessThanOrEqualTo(1.005f),
                "то, что уходит с экрана, не должно подпрыгивать на выходе");
        }

        [Test]
        public void НеЗависитОтЧастотыКадров()
        {
            // Одна и та же анимация на трёх частотах должна прийти в одну точку
            // и проскочить одинаково. Наивная пружина на 20 fps РАСХОДИТСЯ —
            // элемент улетает за экран, и ловится это только на слабом телефоне.
            var f20 = Run(20f, LvnMotion.DampingSoft);
            var f60 = Run(60f, LvnMotion.DampingSoft);
            var f120 = Run(120f, LvnMotion.DampingSoft);

            Assert.That(f20.final, Is.EqualTo(1f).Within(0.02f), "20 fps: не пришла в цель");
            Assert.That(f120.final, Is.EqualTo(1f).Within(0.02f), "120 fps: не пришла в цель");
            Assert.That(f20.peak, Is.EqualTo(f60.peak).Within(0.03f),
                "проскок на 20 fps разошёлся с 60 — анимация зависит от железа");
            Assert.That(f120.peak, Is.EqualTo(f60.peak).Within(0.03f),
                "проскок на 120 fps разошёлся с 60 — анимация зависит от железа");
        }

        [Test]
        public void ДлинныйПропускКадраНеВзрываетПружину()
        {
            // Свернули приложение на десять секунд и вернули. Планировщик отдаст
            // один огромный dt; без ограничения подшагов это либо мгновенный
            // телепорт, либо расходимость.
            float v = 0f, vel = 0f;
            LvnMotion.Step(ref v, ref vel, 1f, 10f);
            Assert.That(float.IsNaN(v), Is.False, "пружина взорвалась в NaN");
            Assert.That(v, Is.LessThan(3f).And.GreaterThan(-1f),
                "после долгой паузы значение должно остаться осмысленным, а не улететь");
        }
    }
}
