using UnityEngine;

namespace Lvn
{
    /// <summary>
    /// КАНАЛ И КОММИТ СБОРКИ — что именно запущено. Пишет сборщик
    /// (<c>CliBuild.StampChannel</c>) в <c>Resources/lvn-build.json</c>;
    /// нет файла (редактор, сборка мимо конвейера) — все поля пусты, и строки,
    /// которые их показывают, живут без них.
    /// </summary>
    public static class LvnBuildInfo
    {
        private static bool _loaded;
        private static string _stamp = "", _commit = "", _channel = "";

        /// <summary>Штамп времени сборки, как в версии приложения.</summary>
        public static string Stamp { get { Load(); return _stamp; } }
        /// <summary>Короткий хэш коммита, из которого собрано.</summary>
        public static string Commit { get { Load(); return _commit; } }
        /// <summary>Канал: dev или prod.</summary>
        public static string Channel { get { Load(); return _channel; } }
        /// <summary>Подпись одной строкой: «dev e18dc31d»; пусто, если
        /// сборщик не подписал.</summary>
        public static string Short { get { Load(); return (_channel + " " + _commit).Trim(); } }

        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var ta = Resources.Load<TextAsset>("lvn-build");
                if (ta == null || string.IsNullOrEmpty(ta.text)) return;
                var j = Newtonsoft.Json.Linq.JObject.Parse(ta.text);
                _stamp = (string)j["stamp"] ?? "";
                _commit = (string)j["commit"] ?? "";
                _channel = (string)j["channel"] ?? "";
            }
            catch { /* подписи нет — сборка не с конвейера, это не ошибка */ }
        }
    }
}
