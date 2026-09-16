using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Lvn.Services
{
    /// <summary>
    /// ЧЁРНЫЙ ЯЩИК НА УСТРОЙСТВЕ (TR-86, идея Ильи 14.09: «данных должно быть
    /// много, но мусора на сервере — нет; устройство хранит всё само,
    /// отправляет отклонения, а подробный лог мы включаем точечно»).
    ///
    /// <para>Полнота без цены: ВСЕ строки — включая Trace, который на сборке
    /// без Verbose никуда не попадал, — пишутся на устройство в файлы по дням
    /// (<c>persistentDataPath/lvn-logs</c>) с потолком по объёму; в памяти
    /// живёт хвост последних строк. На сервер уезжают только отклонения с
    /// этим хвостом (см. LvnLogShip) и подробный лог, пока его просят.</para>
    ///
    /// <para>Быстро: приём строки — это добавление в список под замком и
    /// кольцо в памяти; на диск пишет отдельный поток раз в две секунды.
    /// Ни строк, ни JSON в горячем пути.</para>
    ///
    /// <para>Падение при прошлом запуске узнаётся по файлу-метке рядом с
    /// журналами: чистый уход в фон её снимает, обрыв — нет; на следующем
    /// старте хвост прошлого файла уезжает как отклонение. Не личные данные:
    /// журнал диагностики устройства, без имени игрока и его сохранений.</para>
    /// </summary>
    public static class LvnBlackBox
    {
        private const string SessionMarker = "session.open";   // файл-метка: сессия не закончилась чисто
        private const int RingSize = 200;
        private const long CapBytes = 30L << 20;
        private const int FlushMs = 2000;
        private const int TailBytes = 48 << 10;

        private static readonly string[] _ring = new string[RingSize];
        private static int _ringAt, _ringCount;
        private static readonly List<string> _pending = new List<string>(256);
        private static readonly object _gate = new object();
        private static Thread _writer;
        private static volatile bool _stop;
        private static string _dir;
        private static bool _booted;

        /// <summary>Папка с файлами по дням; пусто до Boot.</summary>
        public static string Dir => _dir;

        public static void Boot()
        {
            if (_booted) return;
            _booted = true;
            try
            {
                _dir = Path.Combine(Application.persistentDataPath, "lvn-logs");
                Directory.CreateDirectory(_dir);
            }
            catch (Exception ex) { LvnLog.Warn("[lvn-blackbox] папка недоступна: " + ex.Message); _dir = null; }

            string broken = ReadMarker();
            string previousTail = broken.Length > 0 ? ReadPreviousTail() : null;

            Application.logMessageReceivedThreaded += OnUnityLog;
            LvnLog.Traced += OnTrace;
            Note("info", "=== session start " + LvnMark.Run + " app " + Application.version
                       + " · " + LvnDeviceProfile.Model + " · " + LvnDeviceProfile.Os);
            MarkOpen();
            TrimToCap();
            if (_dir != null)
            {
                _writer = new Thread(WriterLoop) { IsBackground = true, Name = "lvn-blackbox" };
                _writer.Start();
            }
            if (previousTail != null)
                LvnLogShip.Deviation("[lvn-deviation] прошлый запуск " + broken + " оборвался без ухода в фон", previousTail);
        }

        /// <summary>Уход в фон — чистый конец сессии; возврат — снова метка.</summary>
        public static void Pause(bool paused)
        {
            if (!_booted) return;
            if (paused)
            {
                Note("info", "=== session pause " + LvnMark.Run);
                DropMarker();   // чистый конец сессии
                FlushNow();
            }
            else
            {
                Note("info", "=== session resume " + LvnMark.Run);
                MarkOpen();
            }
        }

        private static string MarkerPath => _dir == null ? null : Path.Combine(_dir, SessionMarker);

        private static void MarkOpen()
        {
            if (MarkerPath == null) return;
            try { File.WriteAllText(MarkerPath, LvnMark.Run); } catch { /* без метки — без детектора обрыва */ }
        }

        private static void DropMarker()
        {
            if (MarkerPath == null) return;
            try { File.Delete(MarkerPath); } catch { /* уже нет */ }
        }

        private static string ReadMarker()
        {
            if (MarkerPath == null) return "";
            try { return File.Exists(MarkerPath) ? File.ReadAllText(MarkerPath).Trim() : ""; }
            catch { return ""; }
        }

        /// <summary>Последние строки — хвост для отклонения.</summary>
        public static string Tail(int lines = RingSize)
        {
            var sb = new StringBuilder();
            lock (_gate)
            {
                int n = Math.Min(lines, _ringCount);
                for (int i = 0; i < n; i++)
                    sb.Append(_ring[(_ringAt - n + i + RingSize) % RingSize]).Append('\n');
            }
            return sb.ToString();
        }

        private static void OnUnityLog(string message, string stack, LogType type)
        {
            string level = type == LogType.Exception ? "exception"
                : type == LogType.Error || type == LogType.Assert ? "error"
                : type == LogType.Warning ? "warning" : "info";
            Note(level, message);
        }

        private static void OnTrace(string message)
        {
            Note("trace", message);
            LvnLogShip.TraceLine(message);   // подробный лог по указанию сервера
        }

        private static void Note(string level, string message)
        {
            if (message == null) return;
            var line = DateTime.UtcNow.ToString("HH:mm:ss.fff") + " " + level[0] + " " + message;
            lock (_gate)
            {
                _ring[_ringAt] = line;
                _ringAt = (_ringAt + 1) % RingSize;
                if (_ringCount < RingSize) _ringCount++;
                if (_dir != null) _pending.Add(line);
            }
        }

        private static void WriterLoop()
        {
            while (!_stop)
            {
                Thread.Sleep(FlushMs);
                FlushNow();
            }
        }

        private static void FlushNow()
        {
            if (_dir == null) return;
            List<string> batch;
            lock (_gate)
            {
                if (_pending.Count == 0) return;
                batch = new List<string>(_pending);
                _pending.Clear();
            }
            try
            {
                var path = Path.Combine(_dir, DateTime.UtcNow.ToString("yyyy-MM-dd") + ".log");
                using var w = new StreamWriter(path, append: true, Encoding.UTF8);
                foreach (var l in batch) w.WriteLine(l);
            }
            catch (Exception ex) { LvnLog.Warn("[lvn-blackbox] запись не удалась: " + ex.Message); }
        }

        /// <summary>Потолок объёма: старые дни удаляются, пока папка не влезет.</summary>
        private static void TrimToCap()
        {
            if (_dir == null) return;
            try
            {
                var files = new List<FileInfo>();
                foreach (var f in new DirectoryInfo(_dir).GetFiles("*.log")) files.Add(f);
                files.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                long total = 0;
                foreach (var f in files) total += f.Length;
                for (int i = 0; total > CapBytes && i < files.Count - 1; i++)
                {
                    total -= files[i].Length;
                    files[i].Delete();
                }
            }
            catch (Exception ex) { LvnLog.Warn("[lvn-blackbox] уборка не удалась: " + ex.Message); }
        }

        /// <summary>
        /// КУСОК КОЛЬЦА ЗА ПЕРИОД — ЗАДНИМ ЧИСЛОМ (TR-86, этап 2). Сервер просит
        /// «с … до …», устройство читает свои файлы по дням, отбирает строки по
        /// времени и высылает пачками по 200 строк уровнем ring с исходным
        /// временем; последняя пачка несёт подтверждение, и сервер снимает
        /// запрос. Не больше 5000 строк на период — дальше просят другой.
        /// </summary>
        internal static async System.Threading.Tasks.Task ShipRangeAsync(string fromIso, string toIso)
        {
            if (_dir == null) return;
            if (!DateTime.TryParse(fromIso, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var from)
                || !DateTime.TryParse(toIso, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var to)
                || to <= from) return;
            FlushNow();
            var lines = new List<(DateTime ts, string text)>();
            await System.Threading.Tasks.Task.Run(() =>
            {
                for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
                {
                    var path = Path.Combine(_dir, day.ToString("yyyy-MM-dd") + ".log");
                    if (!File.Exists(path)) continue;
                    try
                    {
                        foreach (var l in File.ReadLines(path))
                        {
                            if (l.Length < 14 || !TimeSpan.TryParseExact(l.AsSpan(0, 12), @"hh\:mm\:ss\.fff",
                                    System.Globalization.CultureInfo.InvariantCulture, out var t)) continue;
                            var ts = day + t;
                            if (ts < from || ts > to) continue;
                            lines.Add((ts, l.Substring(13)));
                            if (lines.Count >= 5000) return;
                        }
                    }
                    catch (Exception ex) { LvnLog.Warn("[lvn-blackbox] чтение " + path + ": " + ex.Message); }
                }
            });
            var fetched = new Newtonsoft.Json.Linq.JObject { ["from"] = fromIso, ["to"] = toIso };
            if (lines.Count == 0)
                lines.Add((from, "i [lvn-blackbox] за период строк нет"));
            for (int i = 0; i < lines.Count; i += 200)
            {
                var batch = new Newtonsoft.Json.Linq.JArray();
                for (int j = i; j < lines.Count && j < i + 200; j++)
                    batch.Add(new Newtonsoft.Json.Linq.JObject
                    {
                        ["ts"] = lines[j].ts.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                        ["level"] = "ring",
                        ["msg"] = lines[j].text,
                    });
                bool last = i + 200 >= lines.Count;
                long code = await LvnLogShip.SendRawAsync(batch, last ? fetched : null);
                if (!LvnBackend.Ok(code)) { LvnLog.Warn("[lvn-blackbox] кусок кольца не ушёл: " + code); return; }
            }
            LvnLog.Info("[lvn-blackbox] кусок кольца выслан: " + lines.Count + " строк, " + fromIso + " … " + toIso);
        }

        // ── НЕЗАКРЫТАЯ ГЛАВА (Илья 16.09: «а если пользователь приложение закроет
        // или телефон вырубит?») ── на паузе оболочка кладёт сюда, где стоит
        // игрок (глава, строка, секунды по строкам); конец главы или уход это
        // стирают. Если на следующем запуске запись жива, значит, прошлый
        // запуск кончился посреди главы без «ушёл» — оболочка отправляет его
        // сама, той строкой и с тем временем. Файл рядом с журналами, не запись
        // об игроке: без имени и без сохранений.
        private const string OpenChapterFile = "chapter.open";

        private static string OpenChapterPath => _dir == null ? null : Path.Combine(_dir, OpenChapterFile);

        /// <summary>Где стоит игрок сейчас — переписывается на каждой паузе.</summary>
        public static void NoteOpenChapter(string json)
        {
            if (OpenChapterPath == null || string.IsNullOrEmpty(json)) return;
            try { File.WriteAllText(OpenChapterPath, json); } catch { /* без записи — без закрытия задним числом */ }
        }

        /// <summary>Глава закончилась или её покинули штатно — записи больше нет.</summary>
        public static void ClearOpenChapter()
        {
            if (OpenChapterPath == null) return;
            try { File.Delete(OpenChapterPath); } catch { /* уже нет */ }
        }

        /// <summary>Забрать запись прошлого запуска (и стереть): пусто — главы не было.</summary>
        public static string TakeOpenChapter()
        {
            if (OpenChapterPath == null) return null;
            try
            {
                if (!File.Exists(OpenChapterPath)) return null;
                var json = File.ReadAllText(OpenChapterPath);
                File.Delete(OpenChapterPath);
                return string.IsNullOrEmpty(json) ? null : json;
            }
            catch { return null; }
        }

        /// <summary>Хвост последнего файла — что было перед обрывом прошлого запуска.</summary>
        private static string ReadPreviousTail()
        {
            if (_dir == null) return null;
            try
            {
                FileInfo last = null;
                foreach (var f in new DirectoryInfo(_dir).GetFiles("*.log"))
                    if (last == null || string.CompareOrdinal(f.Name, last.Name) > 0) last = f;
                if (last == null || last.Length == 0) return null;
                using var s = last.OpenRead();
                long from = Math.Max(0, s.Length - TailBytes);
                s.Seek(from, SeekOrigin.Begin);
                var buf = new byte[s.Length - from];
                int read = s.Read(buf, 0, buf.Length);
                var text = Encoding.UTF8.GetString(buf, 0, read);
                int nl = from > 0 ? text.IndexOf('\n') : -1;
                return nl >= 0 ? text.Substring(nl + 1) : text;
            }
            catch { return null; }
        }
    }
}
