#!/usr/bin/env python3
"""Довести картинку до ТЕКСТУРЫ ПОВЕРХНОСТИ: высота, нормали, шероховатость.

Картинка и текстура — разные вещи. Картинку смотрят целиком и один раз;
текстура повторяется по земле десятками копий, освещается сценой и обязана
отвечать на свет рельефом. Между «красивым квадратом» и пригодной текстурой
лежит несколько шагов, и все они механические — значит, должны делаться не
руками.

ГЛАВНАЯ ЛОВУШКА, из-за которой большинство самодельных нормалей выглядят
неправильно: высоту НЕЛЬЗЯ брать прямо из яркости. Тёмный корень тогда
становится ямой, светлый камешек — горой, а мокрое пятно — впадиной, хотя ни
то, ни другое, ни третье не имеет отношения к рельефу. Яркость несёт две
разные вещи сразу: собственный цвет вещества (низкие частоты) и светотень на
неровностях (высокие). Разделить их можно по масштабу — и здесь высота
собирается из ПОЛОС ЧАСТОТ с разными весами: широкие пятна почти не влияют,
мелкая зернистость влияет сильно.

Что делает:

  1. ВЫСОТА из полос частот, с подавлением цветовых вариаций.
  2. НОРМАЛИ из высоты (а не из яркости), с заворачиванием по краям.
  3. ALBEDO приводится к рабочей яркости БЕЗ обрезания светов и теней:
     ночную сцену делает освещение, а не чёрная земля.
  4. ШЕРОХОВАТОСТЬ по эвристике: влажное (тёмное и гладкое) отражает резче,
     сухое рассеивает.
  5. ПРОВЕРКИ: стык краёв у каждой карты и разброс яркости по квантилям —
     средняя одна ничего не говорит.

Запуск:
    python3 tools/texture-prep.py вход.png content/textures/земля
    python3 tools/texture-prep.py вход.png out/камень --target 0.24 --strength 1.4
"""
import argparse
import os
import sys

import numpy as np
from PIL import Image, ImageFilter


# ── мелочи ───────────────────────────────────────────────────────────────────

def luminance(rgb: np.ndarray) -> np.ndarray:
    return 0.2126 * rgb[..., 0] + 0.7152 * rgb[..., 1] + 0.0722 * rgb[..., 2]


def to_power_of_two(n: int) -> int:
    p = 256
    while p * 2 <= n:
        p *= 2
    return p


def blur(a: np.ndarray, radius: float) -> np.ndarray:
    """Размытие с ЗАВОРАЧИВАНИЕМ: текстура повторяется, и края должны
    считаться по соседу с противоположной стороны."""
    pad = int(radius * 3) + 1
    wide = np.pad(a, pad, mode="wrap")
    # Через 8 бит, а не через float: размытие с плавающей точкой в PIL
    # поддерживается не везде, а для карты высот четверти процента точности
    # хватает с запасом — она всё равно уйдёт в восьмибитную текстуру.
    lo, hi = float(wide.min()), float(wide.max())
    span = max(hi - lo, 1e-6)
    im = Image.fromarray(((wide - lo) / span * 255).astype(np.uint8), mode="L")
    im = im.filter(ImageFilter.GaussianBlur(radius))
    out = np.asarray(im).astype(np.float32) / 255.0 * span + lo
    return out[pad:-pad, pad:-pad]


def stretch(a: np.ndarray, lo_p=1.0, hi_p=99.0) -> np.ndarray:
    """Растянуть в 0…1 по квантилям, не обрезая хвосты насмерть.

    Обычная нормализация по минимуму и максимуму заложница одного выброса: он
    один задаёт весь диапазон. Квантили устойчивы, а мягкое сжатие за их
    пределами сохраняет и самые тёмные поры, и самые светлые крупинки.
    """
    lo, hi = np.percentile(a, lo_p), np.percentile(a, hi_p)
    if hi - lo < 1e-6:
        return np.zeros_like(a)
    x = (a - lo) / (hi - lo)
    # Хвосты не рубим, а поджимаем: за пределами диапазона наклон падает.
    return np.where(x < 0, x * 0.25, np.where(x > 1, 1 + (x - 1) * 0.25, x)).clip(0, 1)


# ── карты ────────────────────────────────────────────────────────────────────

def build_height(rgb: np.ndarray, scales) -> np.ndarray:
    """Высота из ПОЛОС ЧАСТОТ яркости.

    Каждая полоса — это разность двух размытий, то есть детали определённого
    размера. Крупные полосы почти не участвуют: широкое тёмное пятно — почти
    всегда цвет вещества (корень, влага, лишайник), а не яма. Мелкие участвуют
    в полную силу: зернистость и есть та неровность, ради которой всё
    затевается.
    """
    lum = luminance(rgb) / 255.0

    # Подавление цветовых вариаций: сравниваем яркость не с нулём, а с
    # МЕСТНЫМ фоном. Так участок другого цвета не даёт постоянного сдвига
    # высоты — только его собственная фактура.
    height = np.zeros_like(lum)
    prev = None
    for radius, weight in scales:
        cur = blur(lum, radius)
        band = (prev - cur) if prev is not None else (lum - cur)
        height += band * weight
        prev = cur

    return stretch(height, 2.0, 98.0)


def build_normal(height: np.ndarray, strength: float, invert_green: bool) -> np.ndarray:
    """Нормали ИЗ ВЫСОТЫ (не из яркости), с заворачиванием по краям."""
    def shift(a, dx, dy):
        return np.roll(np.roll(a, dy, axis=0), dx, axis=1)

    # Собель устойчивее простой разности соседей — в сгенерированной картинке
    # шум есть всегда, и по двум точкам наклон получается рваным.
    gx = (shift(height, -1, -1) + 2 * shift(height, -1, 0) + shift(height, -1, 1)
          - shift(height, 1, -1) - 2 * shift(height, 1, 0) - shift(height, 1, 1)) / 8.0
    gy = (shift(height, -1, -1) + 2 * shift(height, 0, -1) + shift(height, 1, -1)
          - shift(height, -1, 1) - 2 * shift(height, 0, 1) - shift(height, 1, 1)) / 8.0

    nx = -gx * strength * 20.0
    ny = -gy * strength * 20.0
    if invert_green:
        ny = -ny        # DirectX-порядок: зелёный вниз
    nz = np.ones_like(height)
    inv = 1.0 / np.sqrt(nx * nx + ny * ny + nz * nz)
    out = np.stack([nx * inv * 0.5 + 0.5, ny * inv * 0.5 + 0.5, nz * inv * 0.5 + 0.5], -1)
    return (out * 255).clip(0, 255).astype(np.uint8)


def build_albedo(rgb: np.ndarray, target: float) -> np.ndarray:
    """Привести яркость к рабочей, НЕ обрезая света и тени.

    Тёмный albedo — самая частая ошибка в сгенерированных текстурах: на экране
    «богато», а в сцене исчезает. Темноту в ночном кадре должен создавать свет,
    а не чёрная земля: у настоящего грунта отражательная способность
    процентов двадцать, и именно её мы восстанавливаем.

    Множитель, а не сложение: сложение съедает контраст и делает поверхность
    молочной, умножение сохраняет отношения — то есть саму фактуру. Там, где
    множитель выбил бы света за белое, они мягко поджимаются.
    """
    cur = luminance(rgb).mean() / 255.0
    if cur < 1e-4 or target <= 0:
        return rgb
    x = rgb.astype(np.float32) / 255.0 * (target / cur)
    # Мягкое плечо у самой единицы: без него светлые крупинки слипаются в
    # одно белое пятно и фактура в светах пропадает.
    knee = 0.85
    x = np.where(x > knee, knee + (x - knee) / (1.0 + (x - knee) / (1.0 - knee)), x)
    return (x * 255).clip(0, 255).astype(np.uint8)


def build_roughness(rgb: np.ndarray, height: np.ndarray) -> np.ndarray:
    """Шероховатость по эвристике: тёмное и гладкое — влажное, светлое — сухое.

    Прямых данных о материале в картинке нет, но есть устойчивая связь: вода
    заполняет поры, поэтому мокрые места одновременно ТЕМНЕЕ и РОВНЕЕ. Сухая
    рыхлая земля наоборот — светлее и рельефнее. Этого хватает, чтобы лужи и
    утоптанная тропа отзывались на свет иначе, чем рыхлый грунт.
    """
    lum = luminance(rgb) / 255.0
    rough = 0.45 + 0.45 * stretch(lum, 5, 95) + 0.15 * stretch(height, 5, 95)
    return (rough.clip(0, 1) * 255).astype(np.uint8)


# ── проверки ─────────────────────────────────────────────────────────────────

def seam(a: np.ndarray) -> str:
    def d(x, y):
        return float(np.abs(x.astype(np.float32) - y.astype(np.float32)).mean())
    h = d(a[:, 0], a[:, -1])
    v = d(a[0, :], a[-1, :])
    mid = a.shape[1] // 2
    base = d(a[:, mid], a[:, mid + 1])
    ok = max(h, v) <= base * 1.6
    return f"стык {h:.1f}/{v:.1f} при обычной {base:.1f} → {'бесшовно' if ok else 'ШОВ'}"


def levels(a: np.ndarray) -> str:
    """Квантили, а не средняя: средняя не отличает ровный серый от текстуры,
    где половина в чёрном, а половина в белом."""
    lum = luminance(a) if a.ndim == 3 else a.astype(np.float32)
    p5, p50, p95 = np.percentile(lum, [5, 50, 95]) / 255.0
    return f"p5={p5:.3f} p50={p50:.3f} p95={p95:.3f}"


# ── сборка ───────────────────────────────────────────────────────────────────

def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("source")
    ap.add_argument("out", help="путь без расширения")
    ap.add_argument("--target", type=float, default=0.22,
                    help="целевая средняя яркость цвета (0.20–0.25 для грунта)")
    ap.add_argument("--strength", type=float, default=1.0, help="сила рельефа нормалей")
    ap.add_argument("--invert-green", action="store_true",
                    help="зелёный канал вниз (DirectX-порядок)")
    ap.add_argument("--size", type=int, default=0)
    ap.add_argument("--quality", type=int, default=92)
    ap.add_argument("--maps", default="color,normal",
                    help="что сохранить: color,normal,height,rough")
    args = ap.parse_args()

    im = Image.open(args.source).convert("RGB")
    side = args.size or to_power_of_two(min(im.size))
    if im.size != (side, side):
        im = im.resize((side, side), Image.LANCZOS)
    rgb = np.asarray(im)

    print(f"  вход {os.path.basename(args.source)} → {side}×{side}")
    print(f"  исходный цвет:  {levels(rgb)}   {seam(rgb)}")

    # Полосы частот: широкая почти не влияет (это цвет вещества), узкие — в
    # полную силу (это фактура).
    scales = ((24.0, 0.15), (8.0, 0.5), (2.5, 1.0), (1.0, 1.0))
    height = build_height(rgb, scales)
    albedo = build_albedo(rgb, args.target)
    normal = build_normal(height, args.strength, args.invert_green)
    rough = build_roughness(rgb, height)

    print(f"  цвет после:     {levels(albedo)}")
    print(f"  высота:         {levels((height * 255).astype(np.uint8))}   "
          f"{seam((height * 255).astype(np.uint8))}")
    print(f"  нормали:        {seam(normal)}")

    os.makedirs(os.path.dirname(os.path.abspath(args.out)) or ".", exist_ok=True)
    want = {m.strip() for m in args.maps.split(",")}
    saved = []
    if "color" in want:
        p = args.out + ".jpg"
        Image.fromarray(albedo).save(p, quality=args.quality); saved.append(p)
    if "normal" in want:
        p = args.out + "-n.jpg"
        Image.fromarray(normal).save(p, quality=args.quality); saved.append(p)
    if "height" in want:
        p = args.out + "-h.jpg"
        Image.fromarray((height * 255).astype(np.uint8)).save(p, quality=args.quality); saved.append(p)
    if "rough" in want:
        p = args.out + "-r.jpg"
        Image.fromarray(rough).save(p, quality=args.quality); saved.append(p)
    for p in saved:
        print(f"  сохранено: {p} ({os.path.getsize(p) // 1024} КБ)")

    # Как эти карты попадут в движок. В рантайме их раскладывает LvnTextures
    # (повтор, уровни детализации, анизотропия, нормали в линейном
    # пространстве). Если класть их в проект Unity ассетами, настройки
    # импорта придётся выставить руками — печатаем какие.
    print()
    print("  в Unity, если класть ассетами: Wrap=Repeat, цвет sRGB,")
    print("  нормаль Type=Normal map, высота и шероховатость БЕЗ sRGB.")
    print("  В рантайме это уже делает LvnTextures — руками ничего не нужно.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
