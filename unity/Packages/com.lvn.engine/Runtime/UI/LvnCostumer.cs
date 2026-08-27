using System;
using System.Collections.Generic;

namespace Lvn.UI
{
    /// <summary>
    /// КОСТЮМЕР — единственный, кто отвечает на вопрос «во что этот герой одет
    /// прямо сейчас».
    ///
    /// <para>Вопрос звучал в пяти домах, и каждый отвечал по-своему. Сцена
    /// собирала оси команды, интерполировала <c>{var}</c> и подмешивала
    /// гардероб (<c>VnStage.Actors.Placement.AxesOf</c>). Лист гардероба четыре
    /// раза писал одну и ту же лесенку «примеренное → надетое → дефолт», каждый
    /// раз с чуть другим хвостом. Функция сценария <c>worn()</c> писала её же в
    /// пятый. Прогрев ассетов обходил те же оси своим кодом. Отсюда «героиня
    /// лысая», «не тот наряд», «примерка не доехала до сцены».</para>
    ///
    /// <para>Здесь два разных вопроса, и путать их нельзя:</para>
    /// <list type="bullet">
    ///   <item><see cref="Chosen"/> — что ВИДНО на герое: примерка сильнее
    ///   надетого. Игрок крутит карусель — сцена обязана показывать то, что он
    ///   видит, ещё до всякого «Выбрать».</item>
    ///   <item><see cref="Committed"/> — что ЗАФИКСИРОВАНО: надетое, иначе
    ///   дефолт. По нему решают, есть ли неподтверждённая примерка, и что
    ///   считать «текущим» в витрине.</item>
    /// </list>
    ///
    /// <para>«Снято» (<see cref="LvnWardrobe.NoneValue"/>) — это ОТВЕТ, а не
    /// отсутствие ответа: пустой слот украшения не добирается надетым и не
    /// заполняется дефолтом. Возвращается как есть, а звонящий сам решает,
    /// показать пункт «Нет» или пропустить слой.</para>
    ///
    /// <para>Костюмер решает, ЧТО надето. Как это выглядит файлами — дело
    /// каталога (<c>SpriteCatalog.ResolveLayers</c>): он же чинит чужое
    /// значение оси дефолтом и собирает слои.</para>
    /// </summary>
    public static class LvnCostumer
    {
        /// <summary>Что на оси видно прямо сейчас: примерка → надетое →
        /// дефолт. Пустая строка — на оси ничего нет и добрать неоткуда.</summary>
        /// <param name="defaults">Дефолты набора (гардеробного описания или
        /// каталога), если у звонящего они есть.</param>
        public static string Chosen(string entity, string axis,
            IReadOnlyDictionary<string, string> defaults = null)
        {
            if (string.IsNullOrEmpty(entity) || string.IsNullOrEmpty(axis)) return "";
            if (LvnWardrobe.Previewed(entity).TryGetValue(axis, out var preview)
                && !string.IsNullOrEmpty(preview))
                return preview;   // включая NoneValue: «снял» — тоже выбор
            return Committed(entity, axis, defaults);
        }

        /// <summary>Что на оси зафиксировано: надетое → дефолт. Примерка не
        /// считается — по этому и отличают «игрок что-то поменял» от «просто
        /// открыл гардероб».</summary>
        public static string Committed(string entity, string axis,
            IReadOnlyDictionary<string, string> defaults = null)
        {
            if (string.IsNullOrEmpty(entity) || string.IsNullOrEmpty(axis)) return "";
            if (LvnWardrobe.Equipped(entity).TryGetValue(axis, out var worn)
                && !string.IsNullOrEmpty(worn))
                return worn;
            if (defaults != null && defaults.TryGetValue(axis, out var dflt)
                && !string.IsNullOrEmpty(dflt))
                return dflt;
            return "";
        }

        /// <summary>Носится ли ИМЕННО это значение (примерка считается) — так
        /// подсвечивают карточку в витрине.</summary>
        public static bool Wearing(string entity, string axis, string value,
            IReadOnlyDictionary<string, string> defaults = null)
            => Chosen(entity, axis, defaults) == value;

        /// <summary>Пусто ли на оси: ни надетого, ни примеренного, либо явно
        /// снято. «Ничего не надето» и примерка пункта «Нет» — одно
        /// состояние.</summary>
        public static bool Bare(string value)
            => string.IsNullOrEmpty(value) || value == LvnWardrobe.NoneValue;

        /// <summary>
        /// ПОЛНЫЙ ОБЛИК для сцены: оси, которые назвал сценарий, плюс то, что
        /// добирает гардероб.
        ///
        /// <para>Ось со значением-шаблоном (<c>outfit={Wardrobe.mainCh_Clothes}</c>)
        /// — variable-driven: её ведёт переменная, и живая примерка имеет право
        /// её перебить. Ось, которую автор вписал буквально
        /// (<c>actor hero armor=chain</c>), — сюжетная, и примерка её не
        /// трогает. Ось, чьё значение так и не разрешилось, ВЫБРАСЫВАЕТСЯ: без
        /// значения слой не рисуется, и это законный случай «ничего не
        /// надето», а не ошибка.</para>
        ///
        /// <para>Чистая функция: интерполяцию отдают снаружи
        /// (<paramref name="resolve"/>), поэтому правило проверяется тестом без
        /// сцены и без плеера.</para>
        /// </summary>
        /// <param name="scripted">Оси команды актёра как их написал автор
        /// (изменяется на месте и возвращается).</param>
        /// <param name="resolve">Разворачивает <c>{var}</c> в значение; null —
        /// переменных нет, и шаблоны просто выпадут.</param>
        public static Dictionary<string, string> Look(
            Dictionary<string, string> scripted, string entity, Func<string, string> resolve)
        {
            var axes = scripted ?? new Dictionary<string, string>();
            var templated = new HashSet<string>();
            foreach (var key in new List<string>(axes.Keys))
            {
                var v = axes[key];
                if (!string.IsNullOrEmpty(v) && v.IndexOf('{') >= 0)
                {
                    templated.Add(key);
                    if (resolve != null) v = resolve(v);
                }
                if (string.IsNullOrEmpty(v) || v.IndexOf('{') >= 0) axes.Remove(key);
                else axes[key] = v;
            }
            LvnWardrobe.MergeInto(axes, entity, templated);
            return axes;
        }
    }
}
