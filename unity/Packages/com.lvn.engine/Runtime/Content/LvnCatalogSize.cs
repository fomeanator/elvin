using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Lvn.Content
{
    /// <summary>
    /// СКОЛЬКО ВЕСИТ ИГРА — и почём каждая ступень качества.
    ///
    /// <para>Загрузчик обязан отвечать на два вопроса игрока: сколько всего
    /// качать и сколько осталось. Без размеров он мог только считать файлы, а
    /// файл файлу рознь — от значка в килобайт до фона в десять мегабайт.</para>
    ///
    /// <para>Размеры приносит сервер (<c>/content/asset-sizes.json</c>). Он же
    /// отдаёт КОЭФФИЦИЕНТЫ ступеней: варианты качества создаются лениво, и
    /// пока файла нет на диске сервера, его вес известен только как доля от
    /// исходника. Поэтому объём ступени — оценка, и называть её надо оценкой,
    /// а не точным числом («качество 1к — 360 МБ, 1.4к — 700 МБ, 2к — 1.5 гига»
    /// — Илья 10.09: именно такой выбор игрок и должен видеть).</para>
    /// </summary>
    public static class LvnCatalogSize
    {
        private static Dictionary<string, long> _files;
        private static Dictionary<string, float> _ratios;

        /// <summary>Знаем ли мы вес каталога. Пока нет — загрузчик показывает
        /// счёт файлов, а не мегабайты: выдуманные мегабайты хуже их
        /// отсутствия.</summary>
        public static bool Known => _files != null && _files.Count > 0;

        /// <summary>Забрать таблицу весов с сервера. Молча уходит ни с чем при
        /// любой беде: объём — украшение разговора, а не условие загрузки.</summary>
        public static async Task LoadAsync(ContentLoader loader, CancellationToken ct = default)
        {
            if (loader == null || Known) return;
            try
            {
                var text = await loader.DownloadTextOnce(LvnAssetPath.Under("asset-sizes.json"), ct);
                if (string.IsNullOrEmpty(text)) return;
                var root = JObject.Parse(text);
                var files = new Dictionary<string, long>();
                if (root["files"] is JObject fo)
                    foreach (var kv in fo)
                        files[kv.Key] = kv.Value?.Value<long>() ?? 0;
                var ratios = new Dictionary<string, float>();
                if (root["ratios"] is JObject ro)
                    foreach (var kv in ro)
                        ratios[kv.Key] = kv.Value?.Value<float>() ?? 0f;
                _files = files;
                _ratios = ratios;
            }
            catch { /* нет таблицы — считаем файлами */ }
        }

        /// <summary>Вес одного адреса в БАЙТАХ: точный, если файл у сервера
        /// есть; оценка по коэффициенту ступени, если вариант ещё не создан;
        /// ноль, если о таком адресе не знаем ничего.</summary>
        public static long Of(string url)
        {
            if (!Known || string.IsNullOrEmpty(url)) return 0;
            var rel = LvnAssetPath.Relative(url);
            if (rel == null) return 0;
            if (_files.TryGetValue(rel, out var exact)) return exact;

            // Вариант качества ещё не сделан — берём исходник и его долю.
            foreach (var v in DownloadPolicy.QualityVariants)
            {
                if (!rel.Contains(v + ".")) continue;
                var src = rel.Replace(v + ".", ".");
                if (!_files.TryGetValue(src, out var full)) return 0;
                return _ratios != null && _ratios.TryGetValue(v, out var k) && k > 0f
                    ? (long)(full * k) : full;
            }
            return 0;
        }

        /// <summary>Сколько весит набор адресов на ЗАДАННОЙ ступени качества.
        /// Повторы не считаются дважды: один файл — один вес.</summary>
        public static long Total(IEnumerable<string> urls, string quality)
        {
            if (!Known || urls == null) return 0;
            var seen = new HashSet<string>();
            long sum = 0;
            foreach (var url in urls)
            {
                if (string.IsNullOrEmpty(url)) continue;
                var at = DownloadPolicy.WithVariant(url, DownloadPolicy.SuffixFor(quality)) ?? url;
                if (!seen.Add(at)) continue;
                sum += Of(at);
            }
            return sum;
        }
    }
}
