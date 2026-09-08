using UnityEngine;

namespace Lvn.UI.World
{
    /// <summary>
    /// Camera effects for the Canvas scene — the uGUI mirror of
    /// <see cref="CameraRig"/>. Shakes / zooms / pans a target
    /// <see cref="RectTransform"/> (the GameRoot holding background + actors) in
    /// the Update loop with unscaled time, so the scene moves while the UITK
    /// dialogue/choice chrome above it stays put. No real camera needed — it is
    /// pure transform work, exactly like the UITK rig.
    /// </summary>
    public sealed class WorldCameraRig : MonoBehaviour
    {
        private RectTransform _t;

        // shake
        private float _shakeAmp, _shakeDur, _shakeStart = -1f;
        // zoom
        private float _zoomFrom = 1f, _zoomTo = 1f, _zoomDur, _zoomStart = -1f, _scale = 1f;
        // КАСТ УХОДИТ ЦЕЛИКОМ — прозрачностью, а не масштабом. На витрине
        // фонов смотрят картину, и героиня там лишняя; уменьшать её нельзя
        // (зум камеры тянет и полотно, а мелкая фигура читается как поломка),
        // убирать со сцены — дорого: облик собран, примерка живёт на нём.
        // Прозрачность снимает её мгновенно и так же мгновенно возвращает.
        private CanvasGroup _cast;
        private float _castFrom = 1f, _castTo = 1f, _castDur, _castStart = -1f, _castAlpha = 1f;
        // pan
        private Vector2 _panFrom, _panTo, _panBase;
        private float _panDur, _panStart = -1f;

        private static float Now => LvnClock.Now();

        public void Bind(RectTransform target) { _t = target; }

        /// <summary>Слой актёров — его и только его гасит <see cref="CastFade"/>.</summary>
        public void BindCast(RectTransform cast)
        {
            if (cast == null) { _cast = null; return; }
            _cast = cast.GetComponent<CanvasGroup>() ?? cast.gameObject.AddComponent<CanvasGroup>();
        }

        internal const string GameRootName = "game-root";
        internal const string CastName = "content";

        /// <summary>ВЕРНУТЬ СЕБЕ СЛОЙ ФИГУР, если ссылка на него пропала.
        ///
        /// <para>Привязка делается один раз, при рождении сцены, и держится
        /// ссылкой на компонент. Ссылка на компонент — вещь хрупкая: слой
        /// живёт в иерархии, а <c>CanvasGroup</c> на нём мог быть снят или
        /// пересоздан кем-то другим, и тогда команда «убрать фигуры» уходила
        /// в пустоту молча (живой лог редактора 08.09: на вкладке «Фон»
        /// героиня не пряталась, в сборке — пряталась).</para>
        ///
        /// <para>Слой при этом НИКУДА НЕ ДЕВАЛСЯ: он на своём месте в
        /// иерархии под ригом. Поэтому не ждём, пока нас привяжут заново, —
        /// находим его сами. Имена те же, которыми сцена его и создаёт.</para></summary>
        private void Recast()
        {
            var root = transform.Find(GameRootName);
            var cast = root == null ? null : root.Find(CastName) as RectTransform;
            if (cast == null) return;
            _cast = cast.GetComponent<CanvasGroup>();
            if (_cast == null) _cast = cast.gameObject.AddComponent<CanvasGroup>();
            _cast.alpha = _castAlpha;   // экран не должен мигать от находки
            Lvn.LvnLog.Trace($"[lvn-cast] слой фигур найден заново ({GameRootName}/{CastName}) — "
                           + "ссылка на него была потеряна");
        }

        /// <summary>Показать/убрать ФИГУРЫ, не трогая полотно (1 — видны, 0 — нет).</summary>
        public void CastFade(float alpha, float seconds)
        {
            if (_cast == null) Recast();
            if (_cast == null)
            {
                // Молчать здесь нельзя: команда уходит, ничего не происходит,
                // и искать обрыв будет негде. Раз даже поиск по иерархии не
                // помог — рассказываем, ЧТО именно видно с этого места.
                var root = transform.Find(GameRootName);
                Debug.LogWarning($"[lvn-cast] слой фигур не привязан — гасить нечего "
                    + $"(канвас «{name}», ригов на нём {GetComponents<WorldCameraRig>().Length}, "
                    + $"{GameRootName}={(root == null ? "НЕТ" : "есть")}, "
                    + $"{CastName}={(root == null || root.Find(CastName) == null ? "НЕТ" : "есть")})");
                return;
            }
            _castFrom = _castAlpha; _castTo = Mathf.Clamp01(alpha); _castAlpha = _castTo;
            Lvn.LvnLog.Trace($"[lvn-cast] фигуры {_castFrom:0.00} → {_castTo:0.00} "
                           + $"за {seconds:0.00}с (слой {_cast.name})");
            if (seconds <= 0f) { _cast.alpha = _castTo; _castStart = -1f; return; }
            _castDur = seconds; _castStart = Now;
        }

        /// <summary>Called every frame with what the rig is doing right now:
        /// the 2D offset in canvas units and the zoom factor. A 3D backdrop
        /// listens so a hit shakes the SET too — without it the sprites jolt
        /// while the world behind them stands perfectly still, which reads as a
        /// painted backdrop no matter how good the geometry is.</summary>
        public System.Action<Vector2, float> Echo;

        public void Shake(float amplitude, float seconds)
        {
            if (_t == null || amplitude <= 0f || seconds <= 0f) return;
            _shakeAmp = amplitude; _shakeDur = seconds; _shakeStart = Now;
        }

        public void Zoom(float factor, float seconds)
        {
            if (_t == null) return;
            _zoomFrom = _scale; _zoomTo = Mathf.Max(0.1f, factor); _scale = _zoomTo;
            if (seconds <= 0f) { _t.localScale = new Vector3(_zoomTo, _zoomTo, 1f); _zoomStart = -1f; return; }
            _zoomDur = seconds; _zoomStart = Now;
        }

        public void Pan(float targetX, float targetY, float seconds)
        {
            if (_t == null) return;
            _panTo = new Vector2(targetX, targetY);
            _panBase = _t.anchoredPosition - ShakeOffset(); // pan base excludes live shake
            _panFrom = _panBase;
            if (seconds <= 0f) { _panBase = _panTo; _panStart = -1f; ApplyPosition(); return; }
            _panDur = seconds; _panStart = Now;
        }

        public void Reset(float seconds)
        {
            _shakeStart = -1f;
            Pan(0f, 0f, seconds);
            Zoom(1f, seconds);
            CastFade(1f, seconds);   // общий план возвращает и фигуры
        }

        // Наезды и паны идут smoothstep'ом: линейный ход читался механическим
        // рывком на старте и стопе (жалоба «прыгает» на зуме гардероба).
        private static float Ease(float p) => p * p * (3f - 2f * p);

        private Vector2 ShakeOffset()
        {
            if (_shakeStart < 0f) return Vector2.zero;
            float k = 1f - Mathf.Clamp01((Now - _shakeStart) / _shakeDur);
            if (k <= 0f) { _shakeStart = -1f; return Vector2.zero; }
            return new Vector2((Random.value * 2f - 1f) * _shakeAmp * k,
                               (Random.value * 2f - 1f) * _shakeAmp * k);
        }

        private void ApplyPosition() => _t.anchoredPosition = _panBase + ShakeOffset();

        private void Update()
        {
            if (_t == null) return;

            if (_panStart >= 0f)
            {
                float p = Mathf.Clamp01((Now - _panStart) / Mathf.Max(0.0001f, _panDur));
                _panBase = Vector2.LerpUnclamped(_panFrom, _panTo, Ease(p));
                if (p >= 1f) _panStart = -1f;
            }

            if (_zoomStart >= 0f)
            {
                float p = Mathf.Clamp01((Now - _zoomStart) / Mathf.Max(0.0001f, _zoomDur));
                float s = Mathf.LerpUnclamped(_zoomFrom, _zoomTo, Ease(p));
                _t.localScale = new Vector3(s, s, 1f);
                if (p >= 1f) _zoomStart = -1f;
            }

            if (_castStart >= 0f && _cast != null)
            {
                float p = Mathf.Clamp01((Now - _castStart) / Mathf.Max(0.0001f, _castDur));
                _cast.alpha = Mathf.LerpUnclamped(_castFrom, _castTo, Ease(p));
                if (p >= 1f) _castStart = -1f;
            }

            // Reapply position every frame while shaking or panning.
            if (_shakeStart >= 0f || _panStart >= 0f) ApplyPosition();
            else if (_t.anchoredPosition != _panBase) _t.anchoredPosition = _panBase;

            Echo?.Invoke(_t.anchoredPosition, _t.localScale.x);
        }
    }
}
