using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Lvn.Services
{
    /// <summary>
    /// Откуда пришёл игрок: ссылка, по которой он открыл игру.
    ///
    /// <para>Рекламу без этого запускать нельзя — деньги уходят, а какой креатив
    /// привёл платящего, неизвестно. Ловим адрес запуска (диплинк или app link)
    /// и отправляем его на сервер ОДИН раз: там он разбирается и ложится в
    /// профиль игрока как первое касание.</para>
    ///
    /// <para>Клиент намеренно НИЧЕГО не разбирает. Отправить сырую строку — это
    /// одна реализация разбора вместо трёх (Android, iOS, веб) и возможность
    /// починить его без новой сборки: ошибка разбора на клиенте становится
    /// вечной, потому что переписать уже отправленное нельзя.</para>
    ///
    /// <para>Отправляется один раз за установку (флаг в PlayerPrefs). Повтор
    /// безопасен и на сервере — первое касание не переписывается, — но лишний
    /// запрос на каждом запуске не нужен никому.</para>
    /// </summary>
    public static class LvnAttribution
    {
        /// <summary>Забыть, откуда игрок пришёл: источник установки — тоже
        /// его данные, и обряд забвения их не трогал.</summary>
        public static void Forget()
        {
            foreach (var k in new[] { PSent, PPending })
                try { LvnKeep.Drop(k); } catch { /* уже нечего */ }
        }

        private const string PSent = "lvn.svc.attr.sent";
        private const string PPending = "lvn.svc.attr.pending";

        /// <summary>
        /// Адрес, по которому открыли игру уже ПОСЛЕ запуска: пользователь
        /// нажал ссылку, пока приложение висело в фоне. Оболочка может
        /// подписаться, чтобы открыть нужную новеллу или главу.
        /// </summary>
        public static event Action<string> LinkOpened;

        /// <summary>Ссылка запуска, как её видит устройство. Пусто — запустили с иконки.</summary>
        public static string LaunchUrl { get; private set; }

        /// <summary>
        /// Ставится оболочкой на старте. Ловит и холодный запуск по ссылке
        /// (<see cref="Application.absoluteURL"/>), и переход по ссылке в уже
        /// запущенном приложении (<see cref="Application.deepLinkActivated"/>).
        /// </summary>
        public static void Init()
        {
            Application.deepLinkActivated += OnDeepLink;
            var url = Application.absoluteURL;
            if (!string.IsNullOrEmpty(url))
            {
                LaunchUrl = url;
                Remember(url);
            }
        }

        private static void OnDeepLink(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            LaunchUrl = url;
            Remember(url);
            try { LinkOpened?.Invoke(url); }
            catch { /* атрибуция не смеет ронять игру */ }
            LvnAsync.Fire(FlushAsync(), "Flush");
        }

        /// <summary>
        /// Кладёт строку меток, которую добыл кто-то другой — например
        /// Play Install Referrer через нативный плагин. Тот же путь: сохранить
        /// и отправить сырьём.
        /// </summary>
        public static void NoteInstallReferrer(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return;
            Remember(raw);
            LvnAsync.Fire(FlushAsync(), "Flush");
        }

        private static void Remember(string raw)
        {
            // Уже отправленное не перезаписываем: первое касание — это первое
            // касание, и второй запуск по другой ссылке его не отменяет.
            if (LvnKeep.Get(PSent, 0) == 1) return;
            LvnKeep.Put(PPending, raw);
        }

        /// <summary>
        /// Отправляет отложенную метку. Вызывается после входа: без сессии
        /// сервер не знает, чей это канал, и запрос пришлось бы повторять.
        /// </summary>
        public static async Task FlushAsync()
        {
            if (LvnKeep.Get(PSent, 0) == 1) return;
            var raw = LvnKeep.Get(PPending, "");
            if (string.IsNullOrEmpty(raw)) return;
            if (string.IsNullOrEmpty(LvnBackend.BaseUrl)) return;

            var body = new JObject { ["raw"] = raw }.ToString();
            var (code, _) = await LvnBackend.PostAsync("/v1/attribution", body);
            if (!LvnBackend.Ok(code)) return; // не вышло — попробуем на следующем запуске

            using (LvnKeep.Batch())
            {
                LvnKeep.Put(PSent, 1);
                LvnKeep.Drop(PPending);
            }
        }
    }
}
