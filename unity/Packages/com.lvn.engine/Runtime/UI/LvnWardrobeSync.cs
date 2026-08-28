using System;
using System.Collections.Generic;
using Lvn.Content;

namespace Lvn.UI
{
    /// <summary>
    /// СВЯЗНОЙ ГАРДЕРОБА — держит в согласии две записи одного факта.
    ///
    /// <para>Что на героине надето, знают двое: гардероб устройства
    /// (<see cref="LvnWardrobe"/>, переживает главы и запуски) и сюжетная
    /// переменная новеллы (<c>slot.storyVar</c>, по которой автор ветвит
    /// историю: «если на ней медицинский халат…»). Одна правда, две записи —
    /// и переток между ними до сих пор писался руками, в обе стороны и в
    /// разных местах: вход в главу перекладывал надетое в набор переменных,
    /// открытие листа перекладывало переменные обратно в гардероб.</para>
    ///
    /// <para>Что бывает, когда согласие теряется, известно по живому дефекту:
    /// лист гардероба открывается, не находит в списке значение из переменной
    /// и прыгает на первый предмет — игрок видит вспышку случайного наряда
    /// ровно в момент открытия.</para>
    ///
    /// <para>Ответственность: пройти оси каталога, у которых есть
    /// <c>storyVar</c>, и переложить значения в нужную сторону. Откуда брать и
    /// куда класть — говорит вызывающий: у входа в главу это набор посева, у
    /// листа — живой игрок. Само правило «переложить только то, что названо и
    /// не пусто» живёт здесь.</para>
    /// </summary>
    public static class LvnWardrobeSync
    {
        /// <summary>
        /// НАДЕТОЕ → ПЕРЕМЕННЫЕ. Для каждой оси с <c>storyVar</c> кладёт то,
        /// что на сущности надето, туда, где история это прочтёт.
        /// </summary>
        /// <param name="setVar">Куда класть: <c>(имя переменной, значение)</c>.</param>
        public static void ToVars(string entity, Dictionary<string, LvnWardrobeSlot> wardrobe,
                                  Action<string, string> setVar)
        {
            if (string.IsNullOrEmpty(entity) || wardrobe == null || setVar == null) return;
            var worn = LvnWardrobe.Equipped(entity);
            if (worn == null || worn.Count == 0) return;
            foreach (var slot in wardrobe)
            {
                var name = slot.Value?.storyVar;
                if (string.IsNullOrEmpty(name)) continue;
                if (!worn.TryGetValue(slot.Key, out var val) || string.IsNullOrEmpty(val)) continue;
                setVar(name, val);
            }
        }

        /// <summary>
        /// ПЕРЕМЕННЫЕ → НАДЕТОЕ. Обратная сторона: то, что глава успела
        /// поставить сама (свой <c>set</c> в начале, смена костюма по сюжету),
        /// становится надетым — иначе лист откроется в несогласии со сценой.
        /// </summary>
        /// <param name="getVar">Откуда брать: имя переменной → значение или null.</param>
        public static void FromVars(string entity, Dictionary<string, LvnWardrobeSlot> wardrobe,
                                    Func<string, string> getVar)
        {
            if (string.IsNullOrEmpty(entity) || wardrobe == null || getVar == null) return;
            foreach (var slot in wardrobe)
            {
                var name = slot.Value?.storyVar;
                if (string.IsNullOrEmpty(name)) continue;
                var val = getVar(name);
                if (string.IsNullOrEmpty(val)) continue;
                LvnWardrobe.Equip(entity, slot.Key, val);
            }
        }
    }
}
