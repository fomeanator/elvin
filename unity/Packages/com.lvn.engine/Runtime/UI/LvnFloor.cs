using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// ЭТАЖИ ПРИЛОЖЕНИЯ — кто над кем рисуется.
    ///
    /// <para>Порядок был написан шестью числами в шести файлах, и каждое
    /// объясняло соседей ПО ПАМЯТИ: «выше вуали (100)», «выше оболочки (30),
    /// ниже загрузочных наложений», «ниже панели (10), чтобы хром лёг сверху».
    /// Комментарий — не связь: подвинь один этаж, и остальные останутся
    /// рассказывать про прежний.</para>
    ///
    /// <para>Ошибка здесь не ловится ни компилятором, ни тестом без экрана: она
    /// выглядит как «панель под меню» или «вуаль не накрыла» — и находится
    /// глазами, на устройстве, обычно у того, кому показывают.</para>
    /// </summary>
    public static class LvnFloor
    {
        /// <summary>Мир сцены: холст 3D и спрайты. Под всем интерфейсом.</summary>
        public const int Scene = 0;

        /// <summary>Документ сцены и общая панель: реплики, выборы, хром главы.</summary>
        public const int Stage = 10;

        /// <summary>Оболочка: витрина, вкладки, магазин, настройки.</summary>
        public const int Shell = 30;

        /// <summary>Отдельная панель поверх оболочки — статы новеллы.</summary>
        public const int Panel = 50;

        /// <summary>Вуаль запуска: накрывает всё до передачи кадра.</summary>
        public const int BootVeil = 100;

        /// <summary>Выбор сервера: единственное, что вправе лечь ПОВЕРХ вуали —
        /// без сервера показывать под ней всё равно нечего.</summary>
        public const int ServerSelect = 110;

        /// <summary>
        /// ОТКРЫТЬ СВОЙ СЛОЙ: объект, документ, общие настройки панели, этаж.
        /// Связка стояла тремя копиями (вуаль, выбор сервера, панель статов), и
        /// забыть в ней можно было каждую часть: без общих настроек слой живёт
        /// в своём масштабе и не совпадает с остальным интерфейсом, без этажа —
        /// оказывается под тем, что должен накрыть.
        /// </summary>
        public static (GameObject go, VisualElement root) Open(string name, int order)
        {
            var go = new GameObject(name);
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = LvnPanel.Shared;
            doc.sortingOrder = order;
            var root = doc.rootVisualElement;
            root.style.flexGrow = 1;
            return (go, root);
        }
    }
}
