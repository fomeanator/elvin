using System.Collections.Generic;
using Lvn.Content;
using Lvn.UI;
using Lvn.UI.Screens;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sandbox
{
    /// <summary>
    /// Витрина темы «киберпанк» — но НЕ нарисованная руками.
    ///
    /// <para>Первая версия этого файла собирала экран сама: свои панели, свои
    /// цвета, свои отступы. Она отвечала на вопрос «можно ли на UI Toolkit
    /// сделать красиво» и отвечала «да», но ничего не говорила о продукте:
    /// красивым был файл в песочнице, а не движок.</para>
    ///
    /// <para>Теперь здесь поднимается НАСТОЯЩИЙ хаб оболочки
    /// (<see cref="BrowseHub"/>) — тот самый, что показывается игроку, — и ему
    /// передаётся ровно одна строка настройки: <c>theme = "cyber"</c>. Всё
    /// остальное на экране рисует движок. Это и есть проверка переноса: если
    /// экран выглядит как задумано, значит тема живёт в оболочке, а не в
    /// витрине.</para>
    /// </summary>
    public sealed class CyberHub : MonoBehaviour
    {
        // ПОСЛЕ загрузки сцены, а не до: объект, созданный до неё, погибает
        // вместе с её загрузкой, и Start у него не доживает.
        // ВИТРИНА ВЫКЛЮЧЕНА: она рисуется поверх приложения и перекрывает
        // собой игру. Включать точечно, когда нужно посмотреть на тему, —
        // вернуть [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)].
        private static void Boot()
        {
            var go = new GameObject("CyberHub");
            DontDestroyOnLoad(go);
            go.AddComponent<CyberHub>();
        }

        private void Start()
        {
            var doc = gameObject.AddComponent<UIDocument>();
            var ps = ScriptableObject.CreateInstance<PanelSettings>();
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            // Опорное разрешение оболочки, а не витрины: размеры внутри хаба
            // подобраны под него, и брать другое значит смотреть на другой
            // экран, чем увидит игрок.
            ps.referenceResolution = new Vector2Int(720, 1280);
            ps.match = 1f;   // тянемся по высоте: экран телефона вертикальный
            ps.sortingOrder = 900;
            // Панели, созданной в коде, обязательно нужна тема: без неё у текста
            // нет даже шрифта, и панель молча рисует пустоту.
            var theme = Resources.Load<ThemeStyleSheet>("UI/AppLoading/UnityDefaultRuntimeTheme")
                     ?? Resources.Load<ThemeStyleSheet>("UnityDefaultRuntimeTheme");
            if (theme != null) ps.themeStyleSheet = theme;
            doc.panelSettings = ps;

            var cfg = new BrowseConfig
            {
                layout = "hub",
                theme = "cyber",          // ← ЕДИНСТВЕННОЕ, что отличает вид
                title = "ELEMENTAL CHRONICLES",
                subtitle = "Выбери линию",
                nav_home = "Хаб",
                nav_store = "Магазин",
                nav_wardrobe = "Гардероб",
                nav_gallery = "Архив",
                nav_profile = "Профиль",
            };

            var hub = new BrowseHub(cfg, null)
            {
                PlayerName = "Виктория",
                PlayerLevel = 7,
            };
            hub.SetData(Collections(), Titles());
            doc.rootVisualElement.Add(hub);
            _ = hub.PickTitleAsync();
            Debug.Log("[cyber-hub] хаб оболочки поднят с темой cyber");
        }

        // ── демонстрационные данные ─────────────────────────────────────────
        // Без обложек намеренно: тема обязана держать экран и на пустом
        // каталоге. Если она выглядит прилично без единой картинки, то с
        // картинками будет выглядеть лучше, а не наоборот.
        private static List<LvnCollection> Collections() => new List<LvnCollection>
        {
            new LvnCollection
            {
                id = "lines", name = "Доступные линии",
                subtitle = "Основной сюжет",
                titles = new List<string> { "north", "signal", "vault" },
            },
            new LvnCollection
            {
                id = "side", name = "Побочные ветки",
                titles = new List<string> { "wardrobe", "archive" },
            },
        };

        private static List<LvnTitle> Titles() => new List<LvnTitle>
        {
            T("north",    "Экспедиция «Север»", "Эпизод 01 · сцена 14 из 26"),
            T("signal",   "Потерянный сигнал",  "Эпизод 02 · не начата"),
            T("vault",    "Хранилище",          "Требует уровень 9"),
            T("wardrobe", "Гардероб",           "12 предметов"),
            T("archive",  "Архив",              "5 записей"),
        };

        private static LvnTitle T(string id, string name, string subtitle) =>
            new LvnTitle { id = id, name = name, subtitle = subtitle };
    }
}
