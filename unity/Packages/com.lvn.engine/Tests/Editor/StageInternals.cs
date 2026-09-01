using System;
using System.Reflection;
using NUnit.Framework;
using Lvn.UI;

namespace Lvn.Tests
{
    /// <summary>
    /// ЗАГЛЯНУТЬ ВНУТРЬ СЦЕНЫ — поле и свойство <see cref="VnStage"/> по имени.
    ///
    /// <para>Сцена почти вся приватна, и правильно: её состояние — не
    /// интерфейс. Но проверять правила про ввод, барьеры и расстановку иначе
    /// нечем, поэтому тесты берут поля отражением.</para>
    ///
    /// <para>Приём был расписан дословно в двух файлах, а отражением в тесты
    /// сцены ходят одиннадцать. Важен здесь не сам вызов, а ДВЕ детали, о
    /// которых легко не подумать с третьего раза: обход по базовым типам
    /// (сцена — partial-класс с наследниками, и поле может лежать выше) и
    /// внятная жалоба вместо <c>NullReferenceException</c>. Переименуют поле —
    /// тест обязан сказать «якорь протух», а не упасть в пустоту.</para>
    /// </summary>
    internal static class Внутренности
    {
        private const BindingFlags Любое =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        /// <summary>Поле сцены по имени. Ищет и у базовых типов.</summary>
        public static FieldInfo Поле(string имя)
        {
            for (Type t = typeof(VnStage); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(имя, Любое);
                if (f != null) return f;
            }
            Assert.Fail($"поле {имя} у VnStage пропало — поправь якорь теста");
            return null;
        }

        /// <summary>Свойство сцены по имени.</summary>
        public static PropertyInfo Свойство(string имя)
        {
            PropertyInfo p = typeof(VnStage).GetProperty(имя, Любое);
            if (p == null) Assert.Fail($"свойство {имя} у VnStage пропало — поправь якорь теста");
            return p;
        }

        /// <summary>Прочитать поле у живой сцены.</summary>
        public static object Достать(object сцена, string имя) => Поле(имя).GetValue(сцена);

        /// <summary>Вложить значение в поле живой сцены.</summary>
        public static void Вложить(object сцена, string имя, object значение) =>
            Поле(имя).SetValue(сцена, значение);
    }
}
