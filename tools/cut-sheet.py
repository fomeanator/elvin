#!/usr/bin/env python3
"""Нарезает лист поз персонажа на отдельные спрайты И ИЗМЕРЯЕТ ПРИВОДКУ.

Зачем измерять. Движок при смене позы просто подменяет картинку на том же месте.
Если в одной позе подошвы на низу холста, а в другой на 80 px выше, персонаж будет
ПРЫГАТЬ по экрану при каждой реплике. Глазами такое не ловится: пока не поставишь
кадры друг на друга, всё выглядит нормально. Поэтому скрипт считает по каждой
клетке границы непрозрачных пикселей и сравнивает с допусками из
docs/ec-hero-poses.md.

    python3 tools/cut-sheet.py sheet.png server/content/sprites/ec_hero
    python3 tools/cut-sheet.py sheet.png out/ --check   # только измерить, не писать

Порядок клеток — из раздела 2 того же ТЗ: слева направо, сверху вниз.
"""

import sys
from pathlib import Path

try:
    from PIL import Image
except ImportError:
    sys.exit("нужен Pillow: pip3 install Pillow")

CELL_W, CELL_H = 700, 1400
COLS, ROWS = 5, 3

# Имена по клеткам, порядок жёсткий — он же в ТЗ.
NAMES = [
    "calm", "angry", "smile", "scared", "tired",
    "guard", "attack", "strike", "hurt", "down",
    "cast", "win", "think", "walk", "talk",
]

# Допуски приводки: (низ от нижнего края, верх головы от верхнего края, центр ±)
FLOOR_MIN, FLOOR_MAX = 2, 10   # почти касаются низа, но НЕ вплотную: см. затекание ниже
HEAD_MIN, HEAD_MAX = 90, 180
CENTER_TOL = 25


def bbox_of(cell):
    """Границы непрозрачного содержимого. None, если клетка пустая."""
    alpha = cell.getchannel("A")
    return alpha.getbbox()


def main():
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    sheet_path, out_dir = Path(sys.argv[1]), Path(sys.argv[2])
    check_only = "--check" in sys.argv

    sheet = Image.open(sheet_path).convert("RGBA")
    want = (CELL_W * COLS, CELL_H * ROWS)
    if sheet.size != want:
        print(f"⚠ лист {sheet.size[0]}×{sheet.size[1]}, ожидалось {want[0]}×{want[1]}")
        print("  нарезка всё равно пойдёт по сетке 700×1400 от левого верхнего угла,")
        print("  но если размер не тот — приводка почти наверняка поехала.")

    if not check_only:
        out_dir.mkdir(parents=True, exist_ok=True)

    problems = 0
    print(f"{'поза':<10}{'подошвы':>9}{'голова':>9}{'центр':>8}   замечания")
    for i, name in enumerate(NAMES):
        col, row = i % COLS, i // COLS
        box = (col * CELL_W, row * CELL_H, (col + 1) * CELL_W, (row + 1) * CELL_H)
        cell = sheet.crop(box)

        bb = bbox_of(cell)
        if bb is None:
            print(f"{name:<10}{'—':>9}{'—':>9}{'—':>8}   ПУСТАЯ КЛЕТКА")
            problems += 1
            continue

        left, top, right, bottom = bb
        floor_gap = CELL_H - bottom          # сколько осталось до низа
        head_gap = top                        # сколько от верха до волос
        center = (left + right) // 2
        center_off = center - CELL_W // 2

        notes = []
        if floor_gap > FLOOR_MAX:
            notes.append(f"подошвы висят на {floor_gap} px")
        elif floor_gap < FLOOR_MIN:
            # Пиксель, лежащий на самой границе, при нарезке достаётся СОСЕДНЕЙ
            # клетке снизу и появляется там полоской у верхнего края.
            notes.append("подошвы вплотную к границе — затечёт в соседнюю клетку")
        if not (HEAD_MIN <= head_gap <= HEAD_MAX):
            notes.append(f"голова вне полосы {HEAD_MIN}–{HEAD_MAX}")
        if abs(center_off) > CENTER_TOL:
            notes.append(f"центр смещён на {center_off:+d} px")
        if left <= 0 or right >= CELL_W:
            notes.append("содержимое упирается в бок — обрежется или залезет к соседу")
        if top == 0:
            notes.append("есть пиксели у самого верха — вероятно затекло из клетки сверху")
        if notes:
            problems += 1

        print(f"{name:<10}{floor_gap:>9}{head_gap:>9}{center_off:>+8}   {'; '.join(notes) or 'ок'}")

        if not check_only:
            cell.save(out_dir / f"pose_{name}.png")

    print()
    if problems:
        print(f"⚠ клеток с замечаниями: {problems} из {len(NAMES)}.")
        print("  Перерисовывать нужно ТОЛЬКО их — лист целиком не переделывают.")
    else:
        print(f"✓ приводка сошлась во всех {len(NAMES)} клетках.")
    if not check_only:
        print(f"✓ записано в {out_dir}")
        print("  Дальше: дописать ec_hero в manifest.json (снипет в docs/ec-hero-poses.md §3).")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
