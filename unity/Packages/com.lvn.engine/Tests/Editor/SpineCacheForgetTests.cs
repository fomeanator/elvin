using Lvn.UI;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// <summary>
    /// БЕЛЫЕ ПРЯМОУГОЛЬНИКИ НА СКЕЛЕТЕ.
    ///
    /// <para>У спайна свой статический кэш, и он держит не только разобранный
    /// скелет, но и МАТЕРИАЛЫ, построенные вокруг конкретных текстур. Живое
    /// обновление контента заменяет файлы под теми же именами — материал
    /// остаётся с мёртвой текстурой, и Unity рисует такой меш своей белой.</para>
    ///
    /// <para>Правило «чистить кэш вместе с текстурами» в движке было и
    /// применялось на ОДНОМ пути: снос сцены. Живое обновление сцену не
    /// сносит и до уборки не доходило. Замер 07.09 с устройства: две
    /// публикации подряд → «контент сменился — правок 2, каталог тоже» → белые
    /// прямоугольники на фигуре, и НИ ОДНОЙ ошибки загрузки. Их и не могло
    /// быть: файлы приехали, умер материал.</para>
    ///
    /// <para>Проверка для приёмки: уберите вызов ClearCache из ForgetLooks —
    /// упадёт ОбновлениеКонтентаЗабываетКэшСкелетов.</para>
    /// </summary>
    public class SpineCacheForgetTests
    {
        private GameObject _go;
        private VnStage _stage;
        private System.Action _previous;
        private int _cleared;

        [SetUp]
        public void SetUp()
        {
            _previous = LvnSpineBridge.ClearCache;
            _cleared = 0;
            LvnSpineBridge.ClearCache = () => _cleared++;
            _go = new GameObject("spine-cache-forget-test");
            _go.SetActive(false); // OnEnable не должен строить панель в EditMode
            _stage = _go.AddComponent<VnStage>();
        }

        [TearDown]
        public void TearDown()
        {
            LvnSpineBridge.ClearCache = _previous;
            if (_go != null) Object.DestroyImmediate(_go);
        }

        [Test]
        public void ОбновлениеКонтентаЗабываетКэшСкелетов()
        {
            _stage.ForgetLooks();
            Assert.AreEqual(1, _cleared,
                "кэш скелетов пережил обновление контента: материалы остались с мёртвыми "
                + "текстурами, и фигура выйдет белыми прямоугольниками");
        }

        [Test]
        public void БезИнтеграцииСпайнаЗабываниеНеПадает()
        {
            // Необязательный пакет может отсутствовать — делегат тогда пуст, и
            // забывание облика обязано пройти как прежде.
            LvnSpineBridge.ClearCache = null;
            Assert.DoesNotThrow(() => _stage.ForgetLooks(),
                "без спайн-пакета забывание облика не должно падать");
        }

        [Test]
        public void ЗабыванияСчитаютсяКаждое()
        {
            // Обновления идут подряд (у автора это две публикации за минуту) —
            // каждое обязано дойти до уборки, а не только первое.
            _stage.ForgetLooks();
            _stage.ForgetLooks();
            _stage.ForgetLooks();
            Assert.AreEqual(3, _cleared, "второе и третье обновление тоже несут новые текстуры");
        }
    }
}
