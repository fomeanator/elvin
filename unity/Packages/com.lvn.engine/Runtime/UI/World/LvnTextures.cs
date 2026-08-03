using System.Collections.Generic;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// Текстура ПОВЕРХНОСТИ — не то же самое, что картинка интерфейса.
    ///
    /// Весь остальной арт движка (фоны, актёры, кнопки) — это спрайты: их
    /// показывают целиком, один раз, в лоб. Настройки для них очевидны и
    /// экономны: не повторять по краям (<c>Clamp</c>), не тратить память на
    /// уровни детализации, фильтровать билинейно.
    ///
    /// Текстуру поверхности кладут ИНАЧЕ. Земля тянется на шестьдесят метров,
    /// материал повторяется десятки раз, и смотрят на неё под острым углом —
    /// от самых ног до горизонта. Каждая из «очевидных» настроек спрайта на
    /// такой поверхности даёт свой дефект:
    ///
    ///  • <c>Clamp</c> — за первым повтором выборка упирается в крайний ряд
    ///    пикселей, и он растягивается в бесконечные полосы вдоль взгляда.
    ///    Именно так земля превращается в «расчёску». Нужен <c>Repeat</c>.
    ///  • Нет уровней детализации — вдали на один пиксель экрана приходятся
    ///    сотни текселей, и выбирается случайный. В статике это шум, в
    ///    движении — кипение, которое видно даже краем глаза.
    ///  • Анизотропия 1 — под острым углом выборка берёт квадрат там, где
    ///    нужен вытянутый след, и дальний план мылится в кисель.
    ///
    /// Отдельная плата за это — треть памяти на уровни детализации. Для
    /// материала поверхности она окупается сразу: он один на всю сцену и
    /// повторяется, а не лежит уникальным полотном.
    ///
    /// Карта нормалей идёт тем же путём, но в ЛИНЕЙНОМ пространстве: в ней
    /// лежат не цвета, а направления, и гамма-коррекция их искажает — свет
    /// потом ложится по неправильному рельефу.
    /// </summary>
    public static class LvnTextures
    {
        /// <summary>Резкость выборки под острым углом. Восемь — та точка, где
        /// дальний план перестаёт мылиться; шестнадцать стоит дороже, а разницу
        /// на телефоне уже не видно.</summary>
        private const int Aniso = 8;

        /// <summary>Сколько текстур поверхности держим одновременно. Тридцать
        /// — это примерно полтора десятка материалов с картами нормалей, то
        /// есть заведомо больше, чем в одной сцене.</summary>
        private const int MaxCached = 30;

        // Кэш общий для всех загрузчиков: земля одной новеллы приезжает и из
        // сети, и с диска, и незачем держать две копии в памяти.
        private static readonly Dictionary<string, Texture2D> _cache =
            new Dictionary<string, Texture2D>();

        // Текстуры, в которых есть прозрачные места. Держим ссылками, а не по
        // url: тело получает уже готовый объект и об адресе не знает.
        private static readonly HashSet<Texture> _withAlpha = new HashSet<Texture>();

        /// <summary>Есть ли в текстуре прозрачность — то есть нужно ли её
        /// вырезать порогом, а не рисовать прямоугольником целиком.</summary>
        public static bool HasAlpha(Texture t) => t != null && _withAlpha.Contains(t);

        /// <summary>Готовая текстура из кэша, если её уже собирали.</summary>
        public static Texture2D Cached(string url, bool linear) =>
            _cache.TryGetValue(Key(url, linear), out var t) && t != null ? t : null;

        /// <summary>Собрать текстуру поверхности из байтов файла.
        /// <paramref name="linear"/> — для карт нормалей.</summary>
        public static Texture2D Build(string url, byte[] bytes, bool linear)
        {
            if (bytes == null || bytes.Length == 0) return null;

            var hit = Cached(url, linear);
            if (hit != null) return hit;

            // mipChain и linear задаются ТОЛЬКО в конструкторе: LoadImage
            // подменит размер и содержимое, но эти два свойства сохранит.
            // Поставить их после загрузки нельзя ничем.
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true, linear: linear);
            if (!tex.LoadImage(bytes))
            {
                Object.Destroy(tex);
                return null;
            }

            // ЕСТЬ ЛИ В ТЕКСТУРЕ ПРОЗРАЧНОСТЬ. Узнать это можно только сейчас —
            // через мгновение копия в памяти будет отдана, и пиксели станут
            // недоступны. А знать надо: у карточки листвы прозрачен весь фон,
            // и без вырезания она рисуется чёрным прямоугольником.
            //
            // Смотрим выборочно: у текстуры в миллион пикселей ответ виден по
            // паре тысяч, а полный обход стоил бы заметной паузы на загрузке.
            try
            {
                var px = tex.GetPixels32();
                int clear = 0, step = Mathf.Max(1, px.Length / 4096);
                for (int i = 0; i < px.Length; i += step)
                    if (px[i].a < 250) clear++;
                if (clear * step > px.Length / 100) _withAlpha.Add(tex);
            }
            catch { /* нечитаемая текстура — считаем непрозрачной */ }

            Configure(tex);
            // Уровни детализации считаются здесь, и после этого копия в
            // оперативной памяти не нужна — на телефоне это половина веса.
            tex.Apply(updateMipmaps: true, makeNoLongerReadable: true);

            EnsureAnisotropyAllowed();
            if (!string.IsNullOrEmpty(url))
            {
                // ПОТОЛОК. Кэш нужен, чтобы не качать землю дважды, но расти
                // без предела он не должен: сцена с десятком материалов и их
                // картами нормалей — это уже двадцать текстур, а новелла за
                // вечер проходит десятки сцен. Дойдя до потолка, выбрасываем
                // самую старую: вернуться к ней дешевле, чем не иметь памяти
                // на текущую.
                if (_cache.Count >= MaxCached)
                {
                    foreach (var old in _cache)
                    {
                        if (old.Value != null) { _withAlpha.Remove(old.Value); Object.Destroy(old.Value); }
                        _cache.Remove(old.Key);
                        break;   // словарь помнит порядок вставки — первый и есть самый старый
                    }
                }
                _cache[Key(url, linear)] = tex;
            }
            return tex;
        }

        /// <summary>Разложить уже готовую текстуру по правилам поверхности.
        /// Годится для тех, что пришли из бандла набора.</summary>
        public static void Configure(Texture2D tex)
        {
            if (tex == null) return;
            tex.wrapMode = TextureWrapMode.Repeat;
            // Трилинейная, а не билинейная: иначе переход между уровнями
            // детализации виден на полу отчётливой дугой поперёк кадра.
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = Aniso;
        }

        /// <summary>Выбросить всё: набор сменился, старая земля не нужна.</summary>
        public static void Clear()
        {
            foreach (var t in _cache.Values)
                if (t != null) Object.Destroy(t);
            _cache.Clear();
            _withAlpha.Clear();
        }

        private static string Key(string url, bool linear) => (linear ? "n:" : "c:") + url;

        // Анизотропию можно выключить на весь проект одной настройкой качества,
        // и тогда anisoLevel у текстуры молча ничего не значит. Проверка стоит
        // одного сравнения, а без неё дефект неотличим от «текстура плохая».
        private static bool _anisoChecked;

        private static void EnsureAnisotropyAllowed()
        {
            if (_anisoChecked) return;
            _anisoChecked = true;
            if (QualitySettings.anisotropicFiltering != AnisotropicFiltering.Disable) return;
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.Enable;
            LvnPlayer.Log?.Invoke("[lvn-3d] анизотропия была выключена настройками качества — включил, " +
                                  "иначе поверхности мылятся вдали");
        }
    }
}
