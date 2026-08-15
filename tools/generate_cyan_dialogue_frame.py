#!/usr/bin/env python3
"""Generate a cyan sci-fi dialogue frame that is safe for Unity nine-slicing."""

from __future__ import annotations

import math
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter


ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "art" / "dialogue-frame-cyan"
BASE_SIZE = (640, 240)
SLICE = (96, 64, 96, 64)  # left, top, right, bottom
NAME_SIZE = (192, 64)
NAME_SLICE = (32, 16, 32, 8)

INK = (5, 9, 18)
INK_BLUE = (7, 14, 25)
BACKING = (9, 13, 25)
CYAN = (49, 242, 207)
CYAN_HOT = (104, 255, 225)
CYAN_DEEP = (27, 125, 151)


def clamp(value: float) -> int:
    return max(0, min(255, round(value)))


def scaled_points(points: list[tuple[int, int]], scale: int) -> list[tuple[int, int]]:
    return [(x * scale, y * scale) for x, y in points]


def frame_points(width: int, height: int, inset: int, chamfer: int) -> list[tuple[int, int]]:
    return [
        (inset + chamfer, inset),
        (width - inset - chamfer, inset),
        (width - inset, inset + chamfer),
        (width - inset, height - inset - chamfer),
        (width - inset - chamfer, height - inset),
        (inset + chamfer, height - inset),
        (inset, height - inset - chamfer),
        (inset, inset + chamfer),
    ]


def polygon_mask(size: tuple[int, int], points: list[tuple[int, int]]) -> Image.Image:
    mask = Image.new("L", size, 0)
    ImageDraw.Draw(mask).polygon(points, fill=255)
    return mask


def add_polyline(
    image: Image.Image,
    points: list[tuple[int, int]],
    color: tuple[int, int, int],
    alpha: int,
    width: int,
    glow: int = 0,
) -> None:
    if glow:
        bloom = Image.new("RGBA", image.size, (0, 0, 0, 0))
        ImageDraw.Draw(bloom).line(points + [points[0]], fill=(*color, clamp(alpha * 0.65)), width=width + glow)
        bloom = bloom.filter(ImageFilter.GaussianBlur(max(1, glow * 0.65)))
        image.alpha_composite(bloom)
    crisp = Image.new("RGBA", image.size, (0, 0, 0, 0))
    ImageDraw.Draw(crisp).line(points + [points[0]], fill=(*color, alpha), width=width, joint="curve")
    image.alpha_composite(crisp)


def add_plate(
    image: Image.Image,
    points: list[tuple[int, int]],
    scale: int,
    bright: bool = True,
) -> None:
    plate = Image.new("RGBA", image.size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(plate, "RGBA")
    base = CYAN if bright else CYAN_DEEP
    draw.polygon(points, fill=(*base, 235))
    draw.line(points + [points[0]], fill=(*CYAN_HOT, 175), width=max(1, scale))
    # A clipped inner sheen keeps the plate close to the luminous reference.
    y_min = min(y for _, y in points)
    y_max = max(y for _, y in points)
    draw.line(
        [(min(x for x, _ in points) + 3 * scale, y_min + 2 * scale),
         (max(x for x, _ in points) - 4 * scale, y_min + 2 * scale)],
        fill=(*CYAN_HOT, 120),
        width=max(1, scale),
    )
    glow = plate.filter(ImageFilter.GaussianBlur(2.2 * scale))
    glow.putalpha(glow.getchannel("A").point(lambda a: a * 70 // 255))
    image.alpha_composite(glow)
    image.alpha_composite(plate)


def generate(scale: int) -> Image.Image:
    width, height = (BASE_SIZE[0] * scale, BASE_SIZE[1] * scale)
    size = (width, height)
    image = Image.new("RGBA", size, (0, 0, 0, 0))

    outer = frame_points(width, height, 8 * scale, 31 * scale)
    inner = frame_points(width, height, 17 * scale, 25 * scale)
    core = frame_points(width, height, 23 * scale, 20 * scale)
    outer_mask = polygon_mask(size, outer)

    # Restrained dark housing, fully contained in the sprite (no cast shadow).
    backing = Image.new("RGBA", size, (*BACKING, 0))
    backing.putalpha(outer_mask.point(lambda value: value * 220 // 255))
    image.alpha_composite(backing)

    # Homogeneous dialogue interior with only a vertical tonal shift. No center motif.
    interior_mask = polygon_mask(size, core)
    interior = Image.new("RGBA", size, (0, 0, 0, 0))
    pixels = interior.load()
    for y in range(height):
        t = y / max(1, height - 1)
        rgb = tuple(clamp(a + (b - a) * t) for a, b in zip(INK_BLUE, INK))
        for x in range(width):
            pixels[x, y] = (*rgb, 238)
    interior.putalpha(ImageChops.multiply(interior.getchannel("A"), interior_mask))
    image.alpha_composite(interior)

    # Broad low-alpha cyan under-rail, then two crisp rails like the reference.
    add_polyline(image, outer, CYAN_DEEP, 130, 3 * scale, glow=4 * scale)
    add_polyline(image, inner, CYAN, 220, 2 * scale, glow=3 * scale)
    add_polyline(image, core, CYAN_HOT, 165, max(1, scale), glow=2 * scale)

    # Corner technology is entirely inside fixed 96x64 nine-slice zones.
    add_plate(
        image,
        scaled_points([(27, 13), (59, 13), (66, 7), (82, 7), (91, 16), (91, 29), (66, 29), (59, 24), (27, 24)], scale),
        scale,
    )
    add_plate(
        image,
        scaled_points([(554, 14), (619, 14), (624, 19), (624, 29), (554, 29)], scale),
        scale,
        bright=False,
    )
    add_plate(
        image,
        scaled_points([(24, 211), (72, 211), (79, 217), (79, 226), (34, 226), (24, 217)], scale),
        scale,
        bright=False,
    )
    add_plate(
        image,
        scaled_points([(552, 210), (620, 210), (626, 216), (620, 227), (561, 227), (552, 219)], scale),
        scale,
    )

    # Three short parallel strokes echo the reference's technical corner detail.
    detail = Image.new("RGBA", size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(detail, "RGBA")
    for offset, alpha in ((0, 225), (6, 175), (12, 120)):
        draw.line(
            scaled_points([(20 + offset, 41), (35 + offset, 26)], scale),
            fill=(*CYAN_HOT, alpha),
            width=max(1, 2 * scale),
        )
    for offset, alpha in ((0, 180), (6, 130)):
        draw.line(
            scaled_points([(620 - offset, 200), (631 - offset, 189)], scale),
            fill=(*CYAN, alpha),
            width=max(1, scale),
        )
    image.alpha_composite(detail)

    image.putalpha(ImageChops.multiply(image.getchannel("A"), outer_mask))
    return image


def generate_name_bubble(scale: int) -> Image.Image:
    width, height = NAME_SIZE[0] * scale, NAME_SIZE[1] * scale
    size = (width, height)
    image = Image.new("RGBA", size, (0, 0, 0, 0))

    # Compact attached tab: clipped top corners, straight lower joining edge.
    outer = scaled_points([(4, 64), (4, 15), (15, 4), (177, 4), (188, 15), (188, 64)], scale)
    mask = polygon_mask(size, outer)
    fill = Image.new("RGBA", size, (*INK_BLUE, 235))
    fill.putalpha(mask.point(lambda value: value * 235 // 255))
    image.alpha_composite(fill)

    # A subtle vertical material shift; horizontally uniform for name-length scaling.
    shade = Image.new("RGBA", size, (0, 0, 0, 0))
    pixels = shade.load()
    for y in range(height):
        alpha = clamp(22 * (1 - y / max(1, height - 1)))
        for x in range(width):
            pixels[x, y] = (*CYAN_DEEP, alpha)
    shade.putalpha(ImageChops.multiply(shade.getchannel("A"), mask))
    image.alpha_composite(shade)

    # Rails stop at the flat lower edge so the bubble merges cleanly with the frame.
    rails = Image.new("RGBA", size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(rails, "RGBA")
    top_outer = scaled_points([(15, 7), (177, 7), (185, 15)], scale)
    top_inner = scaled_points([(17, 11), (175, 11), (181, 17)], scale)
    left_outer = scaled_points([(7, 16), (7, 63)], scale)
    right_outer = scaled_points([(185, 16), (185, 63)], scale)
    draw.line(top_outer, fill=(*CYAN_DEEP, 190), width=max(1, 2 * scale))
    draw.line(top_inner, fill=(*CYAN_HOT, 215), width=max(1, scale))
    draw.line(left_outer, fill=(*CYAN, 205), width=max(1, 2 * scale))
    draw.line(right_outer, fill=(*CYAN_DEEP, 150), width=max(1, scale))
    bloom = rails.filter(ImageFilter.GaussianBlur(2 * scale))
    bloom.putalpha(bloom.getchannel("A").point(lambda a: a * 70 // 255))
    image.alpha_composite(bloom)
    image.alpha_composite(rails)

    # Left accent plate stays fully inside the fixed 32px cap.
    add_plate(
        image,
        scaled_points([(5, 12), (18, 12), (23, 7), (29, 7), (32, 11), (32, 22), (25, 22), (21, 18), (5, 18)], scale),
        scale,
    )
    image.putalpha(ImageChops.multiply(image.getchannel("A"), mask))
    return image


def nine_slice(
    image: Image.Image,
    target: tuple[int, int],
    border: tuple[int, int, int, int] = SLICE,
) -> Image.Image:
    left, top, right, bottom = border
    source_w, source_h = image.size
    target_w, target_h = target
    xs = (0, left, source_w - right, source_w)
    ys = (0, top, source_h - bottom, source_h)
    tx = (0, left, target_w - right, target_w)
    ty = (0, top, target_h - bottom, target_h)
    result = Image.new("RGBA", target, (0, 0, 0, 0))
    for row in range(3):
        for column in range(3):
            src = (xs[column], ys[row], xs[column + 1], ys[row + 1])
            dst = (tx[column], ty[row], tx[column + 1], ty[row + 1])
            sw, sh = src[2] - src[0], src[3] - src[1]
            dw, dh = dst[2] - dst[0], dst[3] - dst[1]
            if min(sw, sh, dw, dh) <= 0:
                continue
            part = image.crop(src)
            if part.size != (dw, dh):
                part = part.resize((dw, dh), Image.Resampling.BILINEAR)
            result.alpha_composite(part, (dst[0], dst[1]))
    return result


def checker(size: tuple[int, int], cell: int = 20) -> Image.Image:
    image = Image.new("RGBA", size, (6, 10, 18, 255))
    draw = ImageDraw.Draw(image)
    for y in range(0, size[1], cell):
        for x in range(0, size[0], cell):
            if (x // cell + y // cell) % 2:
                draw.rectangle((x, y, x + cell - 1, y + cell - 1), fill=(11, 18, 29, 255))
    return image


def save(image: Image.Image, path: Path) -> None:
    image.save(path, format="PNG", optimize=True, dpi=(72, 72))


def verify(path: Path, expected: tuple[int, int]) -> None:
    with Image.open(path) as image:
        assert image.size == expected
        assert image.mode == "RGBA"
        assert image.getchannel("A").getextrema() == (0, 255)
        assert "icc_profile" not in image.info
        assert abs(image.info.get("dpi", (72, 72))[0] - 72) < 0.02


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    base = generate(1)
    triple = generate(3)
    save(base, OUT / "dialogue_frame_cyan.png")
    save(triple, OUT / "dialogue_frame_cyan@3x.png")
    verify(OUT / "dialogue_frame_cyan.png", BASE_SIZE)
    verify(OUT / "dialogue_frame_cyan@3x.png", (BASE_SIZE[0] * 3, BASE_SIZE[1] * 3))

    name_base = generate_name_bubble(1)
    name_triple = generate_name_bubble(3)
    save(name_base, OUT / "speaker_name_bubble.png")
    save(name_triple, OUT / "speaker_name_bubble@3x.png")
    verify(OUT / "speaker_name_bubble.png", NAME_SIZE)
    verify(OUT / "speaker_name_bubble@3x.png", (NAME_SIZE[0] * 3, NAME_SIZE[1] * 3))

    preview = checker((1180, 920))
    wide = nine_slice(base, (1080, 300))
    tall = nine_slice(base, (360, 440))
    preview.alpha_composite(wide, (50, 60))
    preview.alpha_composite(base, (40, 470))
    preview.alpha_composite(tall, (780, 430))
    preview.alpha_composite(nine_slice(name_base, (320, 64), NAME_SLICE), (105, 432))
    preview.alpha_composite(nine_slice(name_base, (480, 64), NAME_SLICE), (615, 40))
    draw = ImageDraw.Draw(preview)
    draw.text((50, 30), "nine-slice 1080 x 300", fill=(220, 255, 250, 255))
    draw.text((780, 400), "nine-slice 360 x 440", fill=(220, 255, 250, 255))
    draw.text((40, 440), "native 640 x 240", fill=(220, 255, 250, 255))
    draw.text((615, 18), "speaker bubble 480 x 64", fill=(220, 255, 250, 255))
    save(preview, OUT / "dialogue_frame_cyan_preview.png")

    name_preview = checker((760, 300))
    for width, y in ((192, 30), (320, 115), (560, 200)):
        name_preview.alpha_composite(nine_slice(name_base, (width, 64), NAME_SLICE), (30, y))
    save(name_preview, OUT / "speaker_name_bubble_preview.png")
    print(f"Generated dialogue frame in {OUT}")


if __name__ == "__main__":
    main()
