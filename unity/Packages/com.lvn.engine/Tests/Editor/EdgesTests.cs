using Lvn.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>Кромочник: где у экрана края и сколько от них отступать. Решение
    /// «сколько воздуха» принимали пятеро и каждый по-своему; хуже разнобоя в
    /// числах был разнобой в ПОВОДЕ пересчитать.</summary>
    public class EdgesTests
    {
        [Test]
        public void БезПанелиВырезовНет()
        {
            // До привязки к панели вырезы неизвестны — ноль честнее догадки.
            Assert.AreEqual(Vector2.zero, LvnEdges.Insets(null));
            Assert.AreEqual(Vector2.zero, LvnEdges.Insets(new VisualElement()));
        }

        [Test]
        public void СверхуНеМеньшеМинимума()
        {
            var el = new VisualElement();
            Assert.AreEqual(LvnEdges.HomeTopMin, LvnEdges.Top(el, LvnEdges.HomeTopMin, LvnEdges.PageTopAir), 0.001f,
                "на экране без выреза шапка всё равно стоит на своём минимуме");
            Assert.AreEqual(LvnEdges.PageTopMin, LvnEdges.Top(el, LvnEdges.PageTopMin, LvnEdges.PageTopAir), 0.001f);
        }

        [Test]
        public void БезМинимумаСверхуОстаётсяВоздух()
        {
            Assert.AreEqual(12f, LvnEdges.Top(new VisualElement(), 0f, 12f), 0.001f);
            Assert.AreEqual(0f, LvnEdges.Top(new VisualElement()), 0.001f, "ни минимума, ни воздуха — ноль");
        }

        [Test]
        public void СнизуМинимумаНетТолькоВоздух()
        {
            // У домашней полосы своего минимума нет: она либо есть, либо нет.
            Assert.AreEqual(LvnEdges.NavBottomAir, LvnEdges.Bottom(new VisualElement(), LvnEdges.NavBottomAir), 0.001f);
            Assert.AreEqual(0f, LvnEdges.Bottom(new VisualElement()), 0.001f);
        }

        [Test]
        public void ГлавнаяСтоитВышеВнутреннихСтраниц()
        {
            Assert.Greater(LvnEdges.HomeTopMin, LvnEdges.PageTopMin,
                "шапка главной живёт крупно — это и есть разница между двумя минимумами");
        }

        [Test]
        public void ПокаЭлементаНетВПанелиОтступНеПрименяют()
        {
            // Живой бут падал NullReferenceException в кружке загрузок (28.08):
            // первый вызов шёл из конструктора подписчика, до его достройки, и
            // валил ВЕСЬ бут — исключение из конструктора некому поймать.
            var el = new VisualElement();
            int applied = 0;
            LvnEdges.Follow(el, _ => applied++);
            Assert.AreEqual(0, applied);
        }

        [Test]
        public void СледитьЗаНичемБезопасно()
        {
            Assert.DoesNotThrow(() => LvnEdges.Follow(null, _ => { }));
            Assert.DoesNotThrow(() => LvnEdges.Follow(new VisualElement(), null));
        }
    }
}
