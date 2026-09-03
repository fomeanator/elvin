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
            _seedBase = LvnUrl.Base(seedBase);
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
                if (set.Count > 0) LvnLog.Info($"[lvn-content] сид первого входа: {set.Count} файлов в APK");
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
            // Метка версии в адресе («…@2k.jpg?v=3») — про кэш сервера, а не про
            // содержимое: в описи сида её нет и быть не может, и адрес с ней
            // промахивался мимо ЛЮБОГО ключа. Устаревание сида ловится ниже
            // сверкой sha256, так что отрезать метку тут безопасно.
            url = LvnUrl.Bare(url);
            int at = url.IndexOf(LvnAssetPath.ContentPrefix + "/", StringComparison.Ordinal);
            if (at < 0) return null;
            var rel = url.Substring(at + 1);          // "content/bg/x@2k.jpg"
            if (_seedIndex.Contains(rel)) return rel;
            var baseRel = DownloadPolicy.StripVariant(rel);
            if (_seedIndex.Contains(baseRel)) return baseRel;
            return LowerRungInSeed(baseRel, rel);
        }

        /// <summary>
        /// СТУПЕНЬ НИЖЕ ЗАПРОШЕННОЙ — тоже попадание.
        ///
        /// <para>Сид собирают на сервере, не зная устройства, и потому он везёт
        /// НИЖНЮЮ ступень (@1k). Устройство же просит свою: телефон покрупнее —
        /// «@1440», планшет — «@2k». Совпадения нет, и десять мегабайт в APK
        /// лежали мёртвым грузом, пока телефон качал то же самое с сервера.</para>
        ///
        /// <para>Взять ступень ниже можно: это ТОТ ЖЕ КАДР, только мельче — и
        /// он лучше, чем ждать сеть на первом же экране. Полное качество
        /// приезжает следующим заходом, когда связь есть.</para>
        ///
        /// <para>РАСШИРЕНИЕ НЕ МЕНЯЕТСЯ НИКОГДА. Спросили код для видеокарты —
        /// отдать вместо него PNG значит вернуть байты, которые вызвавший
        /// попытается разобрать как ktx2 и не сможет. Поэтому ищется тот же
        /// корень и то же расширение, только с другой ступенью.</para>
        /// </summary>
        private string LowerRungInSeed(string baseRel, string askedRel)
        {
            int dot = baseRel.LastIndexOf('.');
            if (dot <= 0) return null;
            string stem = baseRel.Substring(0, dot), ext = baseRel.Substring(dot);

            // Ступени перечислены сверху вниз (DownloadPolicy.Variants: 2k,
            // 1440, 1k, mini). Начинаем с той, что запрошена: выше неё не
            // поднимаемся — крупнее просимого в APK не кладут, а если и лежит,
            // то это лишний вес и лишний декод.
            int from = 0;
            for (int i = 0; i < DownloadPolicy.Variants.Length; i++)
                if (askedRel.Contains(DownloadPolicy.Variants[i])) { from = i; break; }

            for (int i = from; i < DownloadPolicy.Variants.Length; i++)
            {
                var candidate = stem + DownloadPolicy.Variants[i] + ext;
                if (_seedIndex.Contains(candidate)) return candidate;
            }
            return null;
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
