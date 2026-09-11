using System.Collections.Generic;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// ЖИВОЙ ПОРТРЕТ ГЕРОЯ (TR-68) — лицо, собранное из того, что игрок сам
    /// надел.
    ///
    /// <para>Аватар в профиле был картинкой из набора и с героем никак не
    /// связан, хотя облик игрок уже собрал в гардеробе. Просьба партнёра:
    /// «чтобы рожа менялась в зависимости от того, как кастомизирована ГГ
    /// сейчас», и следом — «думаю, даже эмоцию можно показывать текущую».</para>
    ///
    /// <para>ПОРТРЕТ — ЭТО ТА ЖЕ КУКЛА, ПРИБЛИЖЕННАЯ К ГОЛОВЕ, а не второй
    /// способ собрать героя. Второй способ разошёлся бы с первым на первой же
    /// новой оси. Слои берутся тем же каталогом, каким их берут сцена и лист
    /// гардероба, и окно на голову объявлено ЗДЕСЬ — один раз на игру: до этого
    /// ростер гардероба вертел свои числа зума, и кружок в шапке спорил бы с
    /// кружком в списке героев.</para>
    ///
    /// <para>Снимком экрана портрет НЕ делается: снимок требует, чтобы герой
    /// был в кадре, и молчит у игрока, не заходившего в гардероб. Слои есть
    /// всегда.</para>
    /// </summary>
    public static class LvnHeroPortrait
    {
        /// <summary>Приближение к голове: во сколько раз слой крупнее кружка.
        /// Слои нарисованы в полный рост, и без зума в кружок попадает живот.</summary>
        public static float Zoom = 2.6f;

        /// <summary>Где в кружке стоит голова: доля высоты СЛОЯ, которая
        /// приходится на центр кружка. 0.13 — голова взрослой фигуры в полный
        /// рост. У героя с иной пропорцией новелла называет своё
        /// (<c>ui.browse.portrait</c>).</summary>
        public static float Anchor = 0.13f;

        /// <summary>Окно портрета: зум и якорь, названные новеллой или движком.</summary>
        public static void Window(LvnManifest manifest, out float zoom, out float anchor)
        {
            zoom = Zoom; anchor = Anchor;
            var p = manifest?.ui?.browse?.portrait;
            if (p == null) return;
            if (p.zoom > 0.1f) zoom = p.zoom;
            if (p.anchor > 0f) anchor = p.anchor;
        }

        /// <summary>Кого показывает портрет — героя гардероба. Нет его — героя
        /// нет и портрета нет: чужое лицо хуже заглушки.</summary>
        public static string HeroOf(LvnManifest manifest) => manifest?.ui?.wardrobe?.entity;

        /// <summary>
        /// СЛОИ ЛИЦА: текущий облик героя плюс текущая эмоция.
        ///
        /// <para>Облик берётся НАДЕТЫЙ, а не примеренный: примерка живёт, пока
        /// открыт лист гардероба, и портрет в шапке менялся бы у игрока под
        /// рукой, пока он просто листает наряды.</para>
        /// </summary>
        /// <param name="emotion">какое лицо у героя СЕЙЧАС — спрашивает у сцены
        /// тот, кто зовёт (<c>VnStage.EmotionOf</c>). Пусто — спокойное лицо по
        /// умолчанию оси.</param>
        public static List<string> Layers(LvnManifest manifest, string emotion = null)
        {
            var hero = HeroOf(manifest);
            if (string.IsNullOrEmpty(hero) || manifest?.sprites == null) return null;
            if (!manifest.sprites.TryGetValue(hero, out var def) || def == null) return null;

            var axes = new Dictionary<string, string>();
            if (def.wardrobe != null)
                foreach (var kv in def.wardrobe)
                {
                    var v = LvnCostumer.Committed(hero, kv.Key, def.defaults);
                    if (!string.IsNullOrEmpty(v)) axes[kv.Key] = v;
                }
            // ЭМОЦИЯ — ТЕКУЩАЯ, А НЕ НАДЕТАЯ. Лицо в гардеробе не надевается
            // (TR-72), поэтому надетого значения у этой оси не бывает вовсе:
            // его знает сцена.
            var mood = emotion;
            if (!string.IsNullOrEmpty(mood) && def.wardrobe != null)
                foreach (var kv in def.wardrobe)
                    if (LvnWardrobeStage.IsEmotion(kv.Key)) { axes[kv.Key] = mood; break; }

            var urls = new SpriteCatalog(manifest.sprites).Resolve(hero, axes);
            return urls != null && urls.Count > 0 ? urls : null;
        }

        /// <summary>
        /// ОДЕТЬ КРУЖОК ЛИЦОМ ГЕРОЯ: слои стопкой, приближенные к голове.
        ///
        /// <para>Слои кладутся В СВОЙ слой-обёртку, а не в сам кружок: у кружка
        /// своя рамка и своя заглушка, и перетирать их портрет не вправе —
        /// иначе, пока арт едет, игрок смотрит в пустоту (риск, названный в
        /// TR-68).</para>
        /// </summary>
        public static VisualElement Face(LvnManifest manifest, ILvnAssets assets, string emotion = null)
        {
            var urls = Layers(manifest, emotion);
            if (urls == null || assets == null) return null;
            Window(manifest, out float zoom, out float anchor);

            var face = new VisualElement { pickingMode = PickingMode.Ignore };
            LvnChrome.Stretch(face);
            face.style.overflow = Overflow.Hidden;
            foreach (var url in urls)
            {
                if (string.IsNullOrEmpty(url)) continue;
                var layer = new VisualElement { pickingMode = PickingMode.Ignore };
                layer.style.position = Position.Absolute;
                layer.style.width = Length.Percent(zoom * 100f);
                layer.style.height = Length.Percent(zoom * 100f);
                layer.style.left = Length.Percent(50f - zoom * 100f * 0.5f);
                layer.style.top = Length.Percent(50f - zoom * 100f * anchor);
                LvnPicture.Fit(layer, cover: false);
                face.Add(layer);
                LvnAsync.Fire(LvnPicture.AssignAsync(layer, url, assets), "HeroPortraitLayer");
            }
            return face;
        }
    }
}
