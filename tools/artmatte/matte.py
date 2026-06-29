#!/usr/bin/env python3
"""Обтравка спрайтов персонажей (alpha-matting) для импортированного articy-арта.

articy экспортирует портреты RGBA со сплошной альфой на белом фоне — в движке
они смотрятся прямоугольными плашками. Этот инструмент вырезает фон флуд-филлом
от краёв: удаляется только СВЯЗНЫЙ с границей почти-белый фон, поэтому интерьерный
белый (рубашки, блики) и римлайт на силуэте остаются нетронутыми.

Использование:
    python3 matte.py <input-dir> <output-dir> [--thresh 46] [--white 222]

Зависимости: Pillow, numpy.
"""
import argparse
import glob
import os
import sys

from PIL import Image, ImageDraw
import numpy as np

# Маловероятный цвет-маркер заливки (не встречается в арте).
KEY = (0, 254, 1)


def matte(src: str, dst: str, thresh: int, white: int) -> float:
    """Вырезает фон из одного спрайта. Возвращает долю вырезанного (%)."""
    base = Image.open(src).convert("RGB")
    flood = base.copy()
    w, h = flood.size
    px = flood.load()
    step = max(2, min(w, h) // 60)
    seeds = (
        [(x, 0) for x in range(0, w, step)]
        + [(x, h - 1) for x in range(0, w, step)]
        + [(0, y) for y in range(0, h, step)]
        + [(w - 1, y) for y in range(0, h, step)]
    )
    for s in seeds:
        r, g, b = px[s]
        if min(r, g, b) > white:  # сеем заливку только из почти-белых краёв
            ImageDraw.floodfill(flood, s, KEY, thresh=thresh)

    fk = np.array(flood)
    rgb = np.array(base)
    bg = (fk[:, :, 0] == KEY[0]) & (fk[:, :, 1] == KEY[1]) & (fk[:, :, 2] == KEY[2])
    alpha = np.where(bg, 0, 255).astype("uint8")
    out = np.dstack([rgb, alpha])
    Image.fromarray(out, "RGBA").save(dst)
    return 100.0 * float(bg.mean())


def main() -> int:
    ap = argparse.ArgumentParser(description="Обтравка спрайтов articy (alpha-matting)")
    ap.add_argument("input", help="папка с PNG-спрайтами")
    ap.add_argument("output", help="папка для обтравленных PNG")
    ap.add_argument("--thresh", type=int, default=46, help="допуск флуд-филла по цвету")
    ap.add_argument("--white", type=int, default=222, help="порог «почти-белого» для сидов")
    args = ap.parse_args()

    os.makedirs(args.output, exist_ok=True)
    files = sorted(glob.glob(os.path.join(args.input, "*.png")))
    if not files:
        print(f"нет PNG в {args.input}", file=sys.stderr)
        return 1

    for f in files:
        name = os.path.basename(f)
        pct = matte(f, os.path.join(args.output, name), args.thresh, args.white)
        print(f"{name}: вырезано фона {pct:.0f}%")
    print(f"готово: {len(files)} спрайт(ов)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
