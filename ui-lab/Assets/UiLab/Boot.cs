using UnityEngine;
using UnityEngine.EventSystems;
using Lvn.UI.Screens;

namespace Lvn.UiLab
{
    /// <summary>
    /// ЛАБОРАТОРИЯ ОБОЛОЧКИ — самый короткий путь «клонировал → нажал Play →
    /// вижу магазин и профиль».
    ///
    /// <para>Песочница (<c>sandbox/</c>) для этого не годится по двум причинам,
    /// и обе стоили бы новому человеку первого дня: её манифест ссылается на
    /// пакеты Spine по АБСОЛЮТНОМУ пути с чужой машины (на свежем клоне проект
    /// просто не откроется), и в ней 600 МБ трёхмерного арта, к интерфейсу
    /// отношения не имеющего.</para>
    ///
    /// <para>Здесь только три пакета движка и этот файл. Сцену собирать не
    /// нужно: камера, EventSystem и <see cref="NovelApp"/> поднимаются сами.
    /// </para>
    ///
    /// <para><b>Откуда брать контент — два пути, оба рабочие.</b></para>
    /// <list type="number">
    ///   <item><b>Готовый сервер.</b> Поставить в <see cref="ServerUrl"/> адрес,
    ///   который дал владелец, — приедут живые новеллы, настоящий арт и цены.
    ///   Ничего поднимать не надо, интернета достаточно. Для работы над видом
    ///   это правильнее: правишь на том, что видит игрок.</item>
    ///   <item><b>Свой сервер.</b> <c>tools/dev/serve.sh</c> поднимает
    ///   демо-контент репозитория на <c>127.0.0.1:8077</c>. Нужен, когда правишь
    ///   и контент тоже (панель авторов там же, на <c>/panel/</c>), или когда
    ///   работаешь без сети.</item>
    /// </list>
    ///
    /// <para><b>Число порта здесь и в скрипте обязано совпадать.</b> 01.09 они
    /// разошлись — код 8077, скрипт и документ 8078, — и «пятиминутный старт»
    /// из документации не работал вовсе: приложение молча играло прошлый кэш.
    /// Правки при этом «не видно» при совершенно верном коде.</para>
    /// </summary>
    public static class Boot
    {
        /// <summary>Куда идти за контентом. Локальный сервер по умолчанию;
        /// поставь сюда адрес готового — увидишь живой продукт.</summary>
        public const string ServerUrl = "http://127.0.0.1:8077";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Run()
        {
            if (Object.FindAnyObjectByType<NovelApp>() != null) return;

            if (Object.FindAnyObjectByType<Camera>() == null)
            {
                var camGo = new GameObject("Main Camera");
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                camGo.tag = "MainCamera";
                Object.DontDestroyOnLoad(camGo);
            }

            // Нажатия UI Toolkit (тап по карточке, выбор) без EventSystem не ходят.
            if (Object.FindAnyObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem", typeof(EventSystem),
                                        typeof(StandaloneInputModule));
                Object.DontDestroyOnLoad(es);
            }

            var go = new GameObject("NovelApp");
            var app = go.AddComponent<NovelApp>();
            app.ServerUrl = ServerUrl;
            Object.DontDestroyOnLoad(go);
        }
    }
}
