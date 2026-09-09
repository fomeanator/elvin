using Lvn.Content;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// <summary>
    /// ЭСКИЗ, КОТОРЫЙ ЕДЕТ НА ДИСК, ОСТАЁТСЯ ЧИТАЕМЫМ.
    ///
    /// <para>Уменьшение кадра по умолчанию отдаёт видеопамять и закрывает
    /// чтение — правильно для картинок, которые только показывают. Но эскиз
    /// сохранения и карточка катсцены следом кодируются в PNG, а тот читает
    /// пиксели процессором и на выгруженной копии отказывает молча: карточки
    /// просто не появлялись («скрин не делается» — Илья 09.09, в логе
    /// устройства «Texture '' is not readable»).</para>
    ///
    /// <para>Держим стражем, потому что ошибка неотличима от работы: снимок
    /// делается, запись не бросает наружу, а файла нет.</para>
    /// </summary>
    public class ThumbnailReadableTests
    {
        [Test]
        public void УменьшеннаяКопияДляДискаКодируетсяВPng()
        {
            var src = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            for (int x = 0; x < 8; x++)
                for (int y = 0; y < 8; y++)
                    src.SetPixel(x, y, Color.magenta);
            src.Apply();

            var small = LvnTexCopy.Rescale(src, 4, 4, readable: true);
            Assert.IsNotNull(small);
            Assert.IsTrue(small.isReadable, "копия для диска остаётся на процессоре");
            Assert.IsNotEmpty(small.EncodeToPNG(), "иначе карточка не запишется, и никто не узнает");

            Object.DestroyImmediate(small);
            Object.DestroyImmediate(src);
        }
    }
}
