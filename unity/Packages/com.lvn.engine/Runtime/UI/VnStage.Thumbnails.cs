using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    public sealed partial class VnStage
    {
        // ── save thumbnails ──────────────────────────────────────────────────
        // The Ren'Py convention: the frame on screen when the menu OPENS (before
        // the scrim paints over it) is the thumbnail of any save made from it.

        private Texture2D _pendingThumb;
        private const int ThumbWidth = 320;

        /// <summary>Capture the current clean frame as the pending save
        /// thumbnail, then continue (the menu defers its scrim by one frame).
        /// Headless/batch runs skip the capture — there are no frames.</summary>
        internal void CaptureMenuThumb(Action onDone)
        {
            if (Application.isBatchMode) { onDone?.Invoke(); return; }
            StartCoroutine(CaptureThumbCo(onDone));
        }

        private System.Collections.IEnumerator CaptureThumbCo(Action onDone)
        {
            yield return new WaitForEndOfFrame();
            try
            {
                var shot = ScreenCapture.CaptureScreenshotAsTexture();
                if (shot != null)
                {
                    if (_pendingThumb != null) Destroy(_pendingThumb);
                    _pendingThumb = ScaleToWidth(shot, ThumbWidth);
                    if (!ReferenceEquals(shot, _pendingThumb)) Destroy(shot);
                }
            }
            catch (Exception e) { LvnPlayer.Log?.Invoke("thumb capture failed: " + e.Message); }
            onDone?.Invoke();
        }

        /// <summary>
        /// КАДР ДЛЯ КАРТОЧКИ КАТСЦЕНЫ, когда сцена не сменила фон.
        ///
        /// <para>Сцена вроде «Знакомства с Агентом» идёт на кадре, поставленном
        /// раньше: адреса фона у неё нет, и плитка в галерее осталась пустой
        /// («нет заставки нормальной» — Илья 09.09). Снимаем экран — тем же
        /// способом, каким снимается эскиз сохранения.</para>
        ///
        /// <para>Не сразу: реплика и фигура встают не в тот же кадр, и мгновенный
        /// снимок поймал бы пустую сцену. Ждём пару мгновений — сцена уже идёт,
        /// игрок этого не замечает.</para>
        /// </summary>
        internal void CaptureCutscenePoster(string titleId, string key)
        {
            if (Application.isBatchMode || string.IsNullOrEmpty(key)) return;
            StartCoroutine(CaptureCutscenePosterCo(titleId, key));
        }

        // key — адрес ПРОХОЖДЕНИЯ, а не метки сцены: у каждого показа свой
        // снимок, иначе новый кадр лёг бы поверх прежней карточки.
        private System.Collections.IEnumerator CaptureCutscenePosterCo(string titleId, string key)
        {
            yield return new WaitForSeconds(0.8f);
            // КАДР СНИМАЕМ БЕЗ ИНТЕРФЕЙСА. Карточка — это арт, а снимок экрана
            // приносил на неё реплику, кнопки и полосы: «скрин с элементами
            // интерфейса делаем, надо без них» (Илья 09.09). Прячем хром той
            // же дорогой, какой его прячет сама сцена, — режиссёром и по
            // причине, поэтому чужое скрытие мы не снимем. Видимость наносится
            // сразу, без движения, и кадр уходит нарисованным начисто.
            bool hid = !LvnScreenDirector.Current.ChromeHidden;
            if (hid) HideChrome(LvnScreenDirector.PosterReason);
            yield return new WaitForEndOfFrame();
            try
            {
                var shot = ScreenCapture.CaptureScreenshotAsTexture();
                if (shot != null)
                {
                    var small = ScaleToWidth(shot, ThumbWidth);
                    LvnCutsceneStore.WritePoster(titleId, key, small);
                    if (!ReferenceEquals(shot, small)) Destroy(small);
                    Destroy(shot);
                }
            }
            catch (Exception e) { LvnPlayer.Log?.Invoke("cutscene poster failed: " + e.Message); }
            // Возвращаем интерфейс тем же ключом: снимок кончился, а сцена
            // могла спрятать хром и по своей причине — её мы не трогаем.
            if (hid) ShowChrome(LvnScreenDirector.PosterReason);
        }

        // GPU-resample to the thumbnail width (readable — it gets PNG-encoded).
        private static Texture2D ScaleToWidth(Texture2D tex, int width)
        {
            if (tex.width <= width) return tex;
            int h = Mathf.Max(1, Mathf.RoundToInt((float)tex.height * width / tex.width));
            // ЧИТАЕМУЮ. Уменьшение по умолчанию отдаёт копию видеопамяти и
            // закрывает чтение — это верно для картинок, которые только
            // показывают. Эскиз же едет НА ДИСК: следом его кодируют в PNG, а
            // тот читает пиксели процессором и на выгруженной копии молча
            // отказывает — «Texture '' is not readable» (лог устройства 09.09,
            // «скрин не делается» — Илья). Болели оба эскиза: и карточка
            // катсцены, и снимок сохранения — они делят эту дорогу.
            // Исходник НЕ уничтожаем: эскиз снимают с живого кадра сцены, и он
            // нужен дальше — в отличие от переноса под бюджет памяти.
            return Lvn.Content.LvnTexCopy.Rescale(tex, width, h, readable: true);
        }
    }
}
