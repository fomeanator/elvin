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
                if (set.Count > 0) Debug.Log($"[content] сид первого входа: {set.Count} файлов в APK");
            }
            catch { _seedIndex = new HashSet<string>(); }
        }


        private async Task<byte[]> TrySeedAsync(string url, string cachePath, CancellationToken ct)
        {
            if (_seedBase == null) return null;
            // Сид мог не прочитаться (нет файла, битый zip) — это не повод
            // валить загрузку: без него просто пойдём в сеть.
            if (_seedLoad != null) { try { await _seedLoad; } catch { } _seedLoad = null; }
            if (_seedIndex == null || _seedIndex.Count == 0) return null;
            int at = url.IndexOf("/content/", StringComparison.Ordinal);
            if (at < 0) return null;
            var rel = url.Substring(at + 1);          // "content/bg/x@2k.jpg"
            var baseRel = DownloadPolicy.StripVariant(rel);
            string hit = _seedIndex.Contains(rel) ? rel
                : _seedIndex.Contains(baseRel) ? baseRel : null;
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
