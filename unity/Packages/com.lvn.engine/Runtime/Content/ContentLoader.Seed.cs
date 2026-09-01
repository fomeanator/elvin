using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using System.Text;

namespace Lvn.Content
{
    /// <summary>
    /// НАБОР В САМОЙ СБОРКЕ — чтобы первая сцена оделась без сети.
    ///
    /// <para>Первый запуск — единственный момент, когда у игры ещё ничего нет,
    /// а показать надо сразу. Критичные файлы вводной кладутся прямо в APK и
    /// читаются с него, минуя загрузку; всё остальное приходит обычным путём.
    /// Без индекса в сборке сид просто молчит — сборка без него собирается и
    /// играет как раньше.</para>
    /// </summary>
    public sealed partial class ContentLoader
    {
        public void EnableSeed(string seedBase)
        {
            if (string.IsNullOrEmpty(seedBase)) return;
            _seedBase = seedBase.TrimEnd('/');
            _seedLoad = LoadSeedIndexAsync();
        }

        private async Task LoadSeedIndexAsync()
        {
            try
            {
                var raw = await FetchLocalAsync(_seedBase + "/index.json");
                var set = new HashSet<string>();
                if (raw != null)
                {
                    var arr = Newtonsoft.Json.Linq.JArray.Parse(Encoding.UTF8.GetString(raw));
                    foreach (var t in arr)
                    {
                        var s = (string)t;
                        if (!string.IsNullOrEmpty(s)) set.Add(s.TrimStart('/'));
                    }
                }
                _seedIndex = set;
                if (set.Count > 0) LvnLog.Info($"[content] сид первого входа: {set.Count} файлов в APK");
            }
            catch { _seedIndex = new HashSet<string>(); }
        }


        /// <summary>
        /// ПОД КАКИМ КЛЮЧОМ ЭТОТ АДРЕС ЛЕЖИТ В СИДЕ — или null, если его там нет.
        ///
        /// <para>Правило простое, но составное: отрезать всё до <c>content/</c>,
        /// а потом попробовать ДВА ключа — с вариантом качества и без него
        /// («bg/x@2k.jpg» и «bg/x.jpg»): сид собирают до того, как известно, на
        /// каком устройстве его распакуют. Записано оно было дважды — здесь и в
        /// проверке «есть ли файл локально» у транскодера, — и разойдись
        /// половинки, файл из APK перестал бы находиться ровно наполовину
        /// случаев.</para>
        /// </summary>
        private string SeedKey(string url)
        {
            if (_seedIndex == null || _seedIndex.Count == 0 || string.IsNullOrEmpty(url)) return null;
            int at = url.IndexOf(LvnAssetPath.ContentPrefix + "/", StringComparison.Ordinal);
            if (at < 0) return null;
            var rel = url.Substring(at + 1);          // "content/bg/x@2k.jpg"
            if (_seedIndex.Contains(rel)) return rel;
            var baseRel = DownloadPolicy.StripVariant(rel);
            return _seedIndex.Contains(baseRel) ? baseRel : null;
        }
        private async Task<byte[]> TrySeedAsync(string url, string cachePath, CancellationToken ct)
        {
            if (_seedBase == null) return null;
            // Сид мог не прочитаться (нет файла, битый zip) — это не повод
            // валить загрузку: без него просто пойдём в сеть.
            // Посев мог сорваться — нам важно лишь ДОЖДАТЬСЯ его конца, а
            // отказ уже объяснён внутри и повторится по обычному пути загрузки.
            if (_seedLoad != null) { try { await _seedLoad; } catch { /* объяснено внутри */ } _seedLoad = null; }
            string hit = SeedKey(url);
            if (hit == null) return null;
            var bytes = await FetchLocalAsync(_seedBase + "/" + hit);
            if (bytes == null || bytes.Length == 0) return null;
            // Сид может отстать от живого контента (арт обновили, APK старый).
            // При живой сети протухший сид пропускаем — качается свежее; в
            // офлайне старый арт лучше чёрного экрана. Следующая сборка APK
            // перевозит свежий сид сама (его кладёт серверный экспорт).
            var expect = IntegrityVersionFor(url);
            bool stale = expect != null && !Sha256Matches(bytes, expect);
            if (stale && LvnNetworkStatus.IsOnline) return null;
            if (!stale)
            {
                try { await WriteAllBytesAsync(cachePath, bytes, ct); }
                catch { /* кэш — ускорение, не условие */ }
            }
            return bytes;
        }
    }
}
