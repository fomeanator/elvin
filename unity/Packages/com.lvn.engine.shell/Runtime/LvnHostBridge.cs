using System;
using Lvn.Content;
using UnityEngine;

// Тот же namespace, что и у NovelApp: мост живёт рядом с тем, к чему цепляется.
namespace Lvn.UI.Screens
{
    /// <summary>
    /// МОСТ К ХОСТ-ПРИЛОЖЕНИЮ — когда наш движок не сам себе приложение, а
    /// экран внутри чужого (React Native, нативный Android/iOS).
    ///
    /// <para>Unity умеет собираться библиотекой (Unity as a Library): вместо
    /// самостоятельного APK получается модуль, который хост монтирует как свой
    /// экран. Игроку это выглядит как одна программа, а нам даёт разделение
    /// труда — витрина, вход и кошелёк остаются у хоста, а мы отвечаем за
    /// сцену. Но тогда нужен канал: хост должен уметь сказать «открой эту
    /// главу», а мы — ответить «глава пройдена».</para>
    ///
    /// <para><b>Почему делегат, а не прямой вызов чужого класса.</b> Пакеты
    /// вроде <c>@azesmway/react-native-unity</c> кладут в Unity-проект свой
    /// <c>UnityMessageManager</c>, и соблазн — позвать его напрямую. Тогда
    /// движок перестанет компилироваться без этого пакета, то есть публичный
    /// открытый движок будет требовать чужую зависимость ради возможности,
    /// которая нужна одному потребителю. Здесь тот же приём, что и с Spine:
    /// мы объявляем ТОЧКУ, а подключает её хост.</para>
    ///
    /// <code>
    /// // на стороне хоста, один раз при старте:
    /// LvnHostBridge.Send = json => UnityMessageManager.Instance.SendMessageToRN(json);
    /// </code>
    ///
    /// <para>Обратное направление приходит на игровой объект с известным
    /// именем — так устроен <c>UnitySendMessage</c>, единственный способ, каким
    /// нативная сторона умеет дотянуться внутрь Unity.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LvnHostBridge : MonoBehaviour
    {
        /// <summary>Имя объекта, на который хост шлёт команды. Совпадает с
        /// тем, что зашито в RN-обёртке: менять только вместе с ней.</summary>
        public const string ObjectName = "LvnHostBridge";

        /// <summary>Куда отдавать события наружу. Пусто — значит хоста нет
        /// (обычная самостоятельная сборка), и события просто некому слушать.</summary>
        public static Action<string> Send;

        /// <summary>Команда от хоста, ещё не разобранная. Хост-специфичные
        /// команды (свои экраны, своя валюта) разбирает игра, а не движок.</summary>
        public static Action<string, string> Command;

        private static LvnHostBridge _instance;
        private NovelApp _app;

        /// <summary>Поднять мост. Зовётся из игры, когда она собрана
        /// библиотекой; в самостоятельной сборке не зовётся вовсе.</summary>
        public static LvnHostBridge Ensure(NovelApp app)
        {
            if (_instance != null) return _instance;
            var go = new GameObject(ObjectName);
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<LvnHostBridge>();
            _instance.Bind(app);
            return _instance;
        }

        private void Bind(NovelApp app)
        {
            if (app == null) return;
            _app = app;
            _app.ChapterStarted += OnChapterStarted;
            _app.ChapterFinished += OnChapterFinished;
        }

        private void OnDestroy()
        {
            if (_app == null) return;
            _app.ChapterStarted -= OnChapterStarted;
            _app.ChapterFinished -= OnChapterFinished;
        }

        // ── наружу ──────────────────────────────────────────────────────────

        private void OnChapterStarted(LvnTitle t, LvnChapter c) => Emit("chapter_started", t, c);
        private void OnChapterFinished(LvnTitle t, LvnChapter c) => Emit("chapter_finished", t, c);

        private static void Emit(string kind, LvnTitle t, LvnChapter c)
        {
            // Собираем JSON вручную, а не сериализатором: полей три, а вот
            // тащить в сообщение ВСЮ модель главы нельзя — хост получит кусок
            // нашего внутреннего формата и начнёт на него опираться.
            Post($"{{\"type\":\"{kind}\",\"title\":{Str(t?.id)},\"chapter\":{Str(c?.id)}}}");
        }

        /// <summary>Отправить хосту произвольное событие. Игра зовёт это, когда
        /// у неё случилось то, что хосту важно знать: покупка, награда, конец
        /// сюжета.</summary>
        public static void Post(string json)
        {
            var send = Send;
            if (send == null) return;   // хоста нет — тишина, а не исключение
            try { send(json); }
            catch (Exception e) { Debug.LogWarning($"[lvn-host] отправка не удалась: {e.Message}"); }
        }

        // ── внутрь ──────────────────────────────────────────────────────────

        /// <summary>Точка входа для <c>UnitySendMessage</c>. Имя метода —
        /// часть договора с хостом, переименование ломает мост молча.</summary>
        public void OnHostMessage(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            string type = Field(json, "type");
            switch (type)
            {
                case "ping":
                    // Проверка канала: хост узнаёт, что Unity ожил и готов.
                    Post("{\"type\":\"pong\"}");
                    break;
                default:
                    var handler = Command;
                    if (handler != null) handler(type, json);
                    else Debug.Log($"[lvn-host] команда «{type}» никем не разобрана");
                    break;
            }
        }

        // Достаём одно строковое поле без подключения сериализатора: сообщения
        // моста — это две-три пары, а полноценный разбор JSON здесь означал бы
        // зависимость ради ничего.
        private static string Field(string json, string key)
        {
            var needle = "\"" + key + "\"";
            int i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return null;
            i = json.IndexOf(':', i + needle.Length);
            if (i < 0) return null;
            int a = json.IndexOf('"', i + 1);
            if (a < 0) return null;
            int b = json.IndexOf('"', a + 1);
            return b < 0 ? null : json.Substring(a + 1, b - a - 1);
        }

        private static string Str(string s) =>
            s == null ? "null" : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
