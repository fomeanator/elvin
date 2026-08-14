#!/usr/bin/env python3
"""Generate the Time Romance nine-slice texture set.

The recipe is deterministic and renders @1x and @3x independently.  The @3x
files are never resized from the @1x files.
"""

from __future__ import annotations

import hashlib
import math
import random
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter


ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "art" / "time-romance-ui"

BACKGROUND = (23, 17, 25)
SURFACE = (36, 26, 36)
BORDER = (56, 41, 58)
TEXT = (246, 236, 241)
ACCENT = (236, 90, 146)
BRASS = (145, 108, 66)

SIZES = {
    "panel_surface": (192, 192),
    "panel_raised": (192, 192),
    "panel_sunken": (96, 96),
    "button_primary": (160, 96),
    "button_secondary": (160, 96),
    "chip": (64, 48),
    "card_frame": (250, 340),
    "divider": (192, 8),
    "sheet_top": (192, 64),
}

BORDERS = {
    "panel_surface": (24, 24, 24, 24),
    "panel_raised": (24, 24, 24, 24),
    "panel_sunken": (16, 16, 16, 16),
    "button_primary": (24, 16, 24, 16),
    "button_secondary": (24, 16, 24, 16),
    "chip": (24, 16, 24, 16),
    "card_frame": (40, 40, 40, 96),
    "divider": (24, 0, 24, 0),
    "sheet_top": (24, 24, 24, 0),
}


def clamp(value: float) -> int:
    return max(0, min(255, round(value)))


def mul(color: tuple[int, int, int], amount: float) -> tuple[int, int, int]:
    return tuple(clamp(channel * amount) for channel in color)


def mix(a: tuple[int, int, int], b: tuple[int, int, int], t: float) -> tuple[int, int, int]:
    return tuple(clamp(x + (y - x) * t) for x, y in zip(a, b))


def seed_for(name: str, scale: int) -> int:
    return int.from_bytes(hashlib.sha256(f"{name}:{scale}".encode()).digest()[:8], "big")


def vertical_gradient(
    size: tuple[int, int], top: tuple[int, int, int, int], bottom: tuple[int, int, int, int]
) -> Image.Image:
    width, height = size
    image = Image.new("RGBA", size)
    pixels = image.load()
    for y in range(height):
        t = y / max(1, height - 1)
        color = tuple(clamp(a + (b - a) * t) for a, b in zip(top, bottom))
        for x in range(width):
            pixels[x, y] = color
    return image


def rounded_mask(size: tuple[int, int], radius: int, top_only: bool = False) -> Image.Image:
    width, height = size
    ss = 4
    mask = Image.new("L", (width * ss, height * ss), 0)
    draw = ImageDraw.Draw(mask)
    draw.rounded_rectangle(
        (0, 0, width * ss - 1, height * ss - 1),
        radius=radius * ss,
        fill=255,
    )
    if top_only:
        draw.rectangle((0, radius * ss, width * ss - 1, height * ss - 1), fill=255)
    return mask.resize(size, Image.Resampling.LANCZOS)


def outline_mask(size: tuple[int, int], radius: int, width: int, inset: int = 1) -> Image.Image:
    canvas = Image.new("L", size, 0)
    outer = rounded_mask((size[0] - 2 * inset, size[1] - 2 * inset), max(1, radius - inset))
    canvas.paste(outer, (inset, inset))
    inner_inset = inset + width
    inner = rounded_mask(
        (size[0] - 2 * inner_inset, size[1] - 2 * inner_inset),
        max(1, radius - inner_inset),
    )
    inner_canvas = Image.new("L", size, 0)
    inner_canvas.paste(inner, (inner_inset, inner_inset))
    return ImageChops.subtract(canvas, inner_canvas)


def restrained_grain(size: tuple[int, int], name: str, scale: int, strength: float = 1.25) -> Image.Image:
    rng = random.Random(seed_for(name, scale))
    width, height = size
    noise = Image.new("L", size)
    noise.putdata([clamp(128 + rng.gauss(0, strength)) for _ in range(width * height)])
    # A tiny directional component suggests matte cloth rather than digital noise.
    fibers = noise.filter(ImageFilter.GaussianBlur(radius=max(0.35, 0.45 * scale)))
    fibers = fibers.filter(ImageFilter.GaussianBlur(radius=(0.7 * scale)))
    return ImageChops.blend(noise, fibers, 0.58)


def tint_with_grain(image: Image.Image, mask: Image.Image, name: str, scale: int, strength: float = 1.25) -> None:
    grain = restrained_grain(image.size, name, scale, strength)
    neutral = Image.new("L", image.size, 128)
    delta = ImageChops.subtract(grain, neutral, scale=1.0, offset=128)
    pixels = image.load()
    dp = delta.load()
    mp = mask.load()
    for y in range(image.height):
        for x in range(image.width):
            if mp[x, y]:
                r, g, b, a = pixels[x, y]
                d = (dp[x, y] - 128) * 0.72
                pixels[x, y] = (clamp(r + d), clamp(g + d), clamp(b + d), a)


def apply_mask(image: Image.Image, mask: Image.Image, opacity: int = 255) -> Image.Image:
    if opacity != 255:
        mask = mask.point(lambda value: value * opacity // 255)
    image.putalpha(mask)
    return image


def add_patinated_border(
    image: Image.Image,
    mask: Image.Image,
    radius: int,
    scale: int,
    color: tuple[int, int, int] = BORDER,
    opacity: int = 225,
    width_px: int = 1,
) -> None:
    edge = outline_mask(image.size, radius, max(1, width_px * scale), inset=max(1, scale))
    rng = random.Random(seed_for(f"edge-{image.size}-{color}", scale))
    ep = edge.load()
    for y in range(edge.height):
        for x in range(edge.width):
            if ep[x, y]:
                wave = 0.91 + 0.06 * math.sin((x * 0.17 + y * 0.11) / scale)
                micro = rng.uniform(0.93, 1.0)
                ep[x, y] = clamp(ep[x, y] * wave * micro)
    edge = ImageChops.multiply(edge, mask)
    layer = Image.new("RGBA", image.size, (*color, opacity))
    layer.putalpha(edge.point(lambda value: value * opacity // 255))
    image.alpha_composite(layer)


def add_lamp_glow(image: Image.Image, mask: Image.Image, scale: int) -> None:
    width, height = image.size
    glow = Image.new("RGBA", image.size, (0, 0, 0, 0))
    pixels = glow.load()
    radius = width * 0.44
    cx, cy = width * 0.12, height * 0.12
    corner_limit = 24 * scale
    for y in range(height):
        for x in range(width):
            distance = math.hypot(x - cx, y - cy) / radius
            # Keep the non-uniform glow inside the fixed nine-slice corner.
            # Otherwise its broad source would become a stripe when the center stretches.
            corner_taper = max(0.0, 1.0 - max(x, y) / max(1, corner_limit))
            if distance < 1 and corner_taper > 0:
                alpha = clamp(15 * (1 - distance) ** 2 * corner_taper)
                pixels[x, y] = (*mix(BRASS, SURFACE, 0.35), alpha)
    glow.putalpha(ImageChops.multiply(glow.getchannel("A"), mask))
    image.alpha_composite(glow)


def panel_surface(scale: int) -> Image.Image:
    size = tuple(value * scale for value in SIZES["panel_surface"])
    radius = 16 * scale
    mask = rounded_mask(size, radius)
    image = vertical_gradient(size, (*mul(SURFACE, 1.04), 255), (*SURFACE, 255))
    tint_with_grain(image, mask, "panel_surface", scale, 1.0)
    apply_mask(image, mask)
    add_lamp_glow(image, mask, scale)
    add_patinated_border(image, mask, radius, scale)
    return image


def panel_raised(scale: int) -> Image.Image:
    size = tuple(value * scale for value in SIZES["panel_raised"])
    radius = 16 * scale
    mask = rounded_mask(size, radius)
    base = mul(SURFACE, 1.06)
    image = vertical_gradient(size, (*mul(base, 1.04), 255), (*mul(base, 0.99), 255))
    tint_with_grain(image, mask, "panel_raised", scale, 1.0)
    apply_mask(image, mask)
    add_lamp_glow(image, mask, scale)
    add_patinated_border(image, mask, radius, scale)
    lighting = Image.new("RGBA", size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(lighting, "RGBA")
    draw.line((radius, scale, size[0] - radius, scale), fill=(*TEXT, 31), width=scale)
    for y in range(size[1] - 3 * scale, size[1] - scale):
        alpha = clamp(32 * (y - (size[1] - 3 * scale)) / max(1, 2 * scale))
        draw.line((radius, y, size[0] - radius, y), fill=(*BACKGROUND, alpha), width=1)
    lighting.putalpha(ImageChops.multiply(lighting.getchannel("A"), mask))
    image.alpha_composite(lighting)
    return image


def panel_sunken(scale: int) -> Image.Image:
    size = tuple(value * scale for value in SIZES["panel_sunken"])
    radius = 12 * scale
    mask = rounded_mask(size, radius)
    base = mul(SURFACE, 0.92)
    image = vertical_gradient(size, (*mul(base, 0.94), 255), (*mul(base, 1.02), 255))
    tint_with_grain(image, mask, "panel_sunken", scale, 0.85)
    apply_mask(image, mask)
    add_patinated_border(image, mask, radius, scale, opacity=195)
    shadow = Image.new("RGBA", size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(shadow, "RGBA")
    draw.arc((scale, scale, size[0] - scale - 1, size[1] - scale - 1), 180, 360, fill=(*BACKGROUND, 115), width=2 * scale)
    draw.line((radius, scale, size[0] - radius, scale), fill=(*BACKGROUND, 125), width=2 * scale)
    shadow.putalpha(ImageChops.multiply(shadow.getchannel("A"), mask))
    image.alpha_composite(shadow)
    return image


def button_primary(scale: int) -> Image.Image:
    size = tuple(value * scale for value in SIZES["button_primary"])
    radius = 12 * scale
    mask = rounded_mask(size, radius)
    image = vertical_gradient(size, (*mul(ACCENT, 1.08), 255), (*mul(ACCENT, 0.96), 255))
    tint_with_grain(image, mask, "button_primary", scale, 1.2)
    apply_mask(image, mask)
    lighting = Image.new("RGBA", size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(lighting, "RGBA")
    draw.line((radius, scale, size[0] - radius, scale), fill=(*TEXT, 48), width=scale)
    draw.line((radius, size[1] - 2 * scale, size[0] - radius, size[1] - 2 * scale), fill=(*mul(ACCENT, 0.72), 90), width=2 * scale)
    lighting.putalpha(ImageChops.multiply(lighting.getchannel("A"), mask))
    image.alpha_composite(lighting)
    return image


def button_secondary(scale: int) -> Image.Image:
    size = tuple(value * scale for value in SIZES["button_secondary"])
    radius = 12 * scale
    mask = rounded_mask(size, radius)
    image = Image.new("RGBA", size, (*TEXT, 0))
    # A 3% white veil keeps the interior readable without baking in a fill color.
    inner = Image.new("RGBA", size, (*TEXT, 8))
    inner.putalpha(mask.point(lambda value: value * 8 // 255))
    image.alpha_composite(inner)
    add_patinated_border(image, mask, radius, scale, opacity=230)
    return image


def chip(scale: int) -> Image.Image:
    size = tuple(value * scale for value in SIZES["chip"])
    radius = 12 * scale
    mask = rounded_mask(size, radius)
    image = Image.new("RGBA", size, (*BACKGROUND, 72))
    image.putalpha(mask.point(lambda value: value * 72 // 255))
    tint = Image.new("RGBA", size, (*BORDER, 0))
    draw = ImageDraw.Draw(tint, "RGBA")
    draw.arc((scale, scale, size[0] - scale - 1, size[1] - scale - 1), 180, 360, fill=(*TEXT, 46), width=scale)
    draw.line((radius, scale, size[0] - radius, scale), fill=(*TEXT, 46), width=scale)
    tint.putalpha(ImageChops.multiply(tint.getchannel("A"), mask))
    image.alpha_composite(tint)
    return image


def card_frame(scale: int) -> Image.Image:
    size = tuple(value * scale for value in SIZES["card_frame"])
    width, height = size
    radius = 16 * scale
    outer = rounded_mask(size, radius)
    image = Image.new("RGBA", size, (0, 0, 0, 0))
    add_patinated_border(image, outer, radius, scale, opacity=235, width_px=2)

    # Poster legibility veil: exactly the lower 30%, transparent at its top.
    start = round(height * 0.70)
    veil = Image.new("RGBA", size, (*BACKGROUND, 0))
    vp = veil.load()
    for y in range(start, height):
        t = (y - start) / max(1, height - 1 - start)
        alpha = clamp(217 * t * t * (3 - 2 * t))
        for x in range(width):
            vp[x, y] = (*BACKGROUND, alpha)
    veil.putalpha(ImageChops.multiply(veil.getchannel("A"), outer))
    image.alpha_composite(veil)

    # Restrained geometric corner cuts/ticks, contained within 40px corner zones.
    detail = Image.new("RGBA", size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(detail, "RGBA")
    c = (*BRASS, 115)
    w = max(1, scale)
    o, short, long = 6 * scale, 12 * scale, 23 * scale
    draw.line((o, long, o, short, short, o, long, o), fill=c, width=w)
    draw.line((width - o - 1, long, width - o - 1, short, width - short - 1, o, width - long - 1, o), fill=c, width=w)
    draw.line((o, height - long - 1, o, height - short - 1, short, height - o - 1, long, height - o - 1), fill=c, width=w)
    draw.line((width - o - 1, height - long - 1, width - o - 1, height - short - 1, width - short - 1, height - o - 1, width - long - 1, height - o - 1), fill=c, width=w)
    detail.putalpha(ImageChops.multiply(detail.getchannel("A"), outer))
    image.alpha_composite(detail)
    return image


def divider(scale: int) -> Image.Image:
    size = tuple(value * scale for value in SIZES["divider"])
    width, height = size
    image = Image.new("RGBA", size, (*BORDER, 0))
    pixels = image.load()
    margin = 24 * scale
    center_y = (height - 1) / 2
    for x in range(width):
        fade = min(1.0, x / max(1, margin), (width - 1 - x) / max(1, margin))
        for y in range(height):
            vertical = max(0.0, 1.0 - abs(y - center_y) / max(1.0, 1.15 * scale))
            alpha = clamp(255 * fade * vertical)
            pixels[x, y] = (*BORDER, alpha)
    return image


def sheet_top(scale: int) -> Image.Image:
    size = tuple(value * scale for value in SIZES["sheet_top"])
    width, height = size
    radius = 16 * scale
    mask = rounded_mask(size, radius, top_only=True)
    base = mul(SURFACE, 1.04)
    image = vertical_gradient(size, (*mul(base, 1.035), 255), (*base, 255))
    tint_with_grain(image, mask, "sheet_top", scale, 0.9)
    apply_mask(image, mask)
    draw = ImageDraw.Draw(image, "RGBA")
    handle_w, handle_h = 40 * scale, 4 * scale
    x0 = (width - handle_w) // 2
    y0 = 8 * scale
    draw.rounded_rectangle((x0, y0, x0 + handle_w - 1, y0 + handle_h - 1), radius=2 * scale, fill=(*BORDER, 255))
    image.putalpha(mask)
    return image


GENERATORS = {
    "panel_surface": panel_surface,
    "panel_raised": panel_raised,
    "panel_sunken": panel_sunken,
    "button_primary": button_primary,
    "button_secondary": button_secondary,
    "chip": chip,
    "card_frame": card_frame,
    "divider": divider,
    "sheet_top": sheet_top,
}


def save_png(image: Image.Image, path: Path) -> None:
    image.save(path, format="PNG", optimize=True, dpi=(72, 72))


def nine_slice(image: Image.Image, border: tuple[int, int, int, int], target: tuple[int, int]) -> Image.Image:
    left, top, right, bottom = border
    source_w, source_h = image.size
    target_w, target_h = target
    xs = (0, left, source_w - right, source_w)
    ys = (0, top, source_h - bottom, source_h)
    tx = (0, left, target_w - right, target_w)
    ty = (0, top, target_h - bottom, target_h)
    result = Image.new("RGBA", target, (0, 0, 0, 0))
    for row in range(3):
        for col in range(3):
            src_box = (xs[col], ys[row], xs[col + 1], ys[row + 1])
            dst_box = (tx[col], ty[row], tx[col + 1], ty[row + 1])
            sw, sh = src_box[2] - src_box[0], src_box[3] - src_box[1]
            dw, dh = dst_box[2] - dst_box[0], dst_box[3] - dst_box[1]
            if min(sw, sh, dw, dh) <= 0:
                continue
            part = image.crop(src_box)
            if part.size != (dw, dh):
                part = part.resize((dw, dh), Image.Resampling.BILINEAR)
            result.alpha_composite(part, (dst_box[0], dst_box[1]))
    return result


def checker(size: tuple[int, int], cell: int = 12) -> Image.Image:
    image = Image.new("RGBA", size, (27, 20, 29, 255))
    draw = ImageDraw.Draw(image)
    for y in range(0, size[1], cell):
        for x in range(0, size[0], cell):
            if (x // cell + y // cell) % 2:
                draw.rectangle((x, y, x + cell - 1, y + cell - 1), fill=(40, 30, 42, 255))
    return image


def preview(assets: dict[str, Image.Image]) -> Image.Image:
    canvas = checker((1240, 1040), 16)
    draw = ImageDraw.Draw(canvas)
    positions = {
        "panel_surface": (40, 60),
        "panel_raised": (272, 60),
        "panel_sunken": (504, 60),
        "button_primary": (40, 310),
        "button_secondary": (240, 310),
        "chip": (440, 330),
        "card_frame": (680, 60),
        "divider": (40, 490),
        "sheet_top": (40, 570),
    }
    for name, position in positions.items():
        asset = assets[name]
        canvas.alpha_composite(asset, position)
        draw.text((position[0], max(8, position[1] - 25)), name, fill=TEXT)

    # Small usage mockup using the same assets without inventing game content.
    mock = Image.new("RGBA", (500, 350), (*BACKGROUND, 255))
    mock.alpha_composite(nine_slice(assets["panel_surface"], BORDERS["panel_surface"], (460, 310)), (20, 20))
    mock.alpha_composite(nine_slice(assets["panel_raised"], BORDERS["panel_raised"], (410, 118)), (45, 48))
    mock.alpha_composite(nine_slice(assets["panel_sunken"], BORDERS["panel_sunken"], (350, 46)), (75, 185))
    mock.alpha_composite(nine_slice(assets["button_primary"], BORDERS["button_primary"], (180, 56)), (250, 250))
    mock.alpha_composite(nine_slice(assets["button_secondary"], BORDERS["button_secondary"], (160, 56)), (65, 250))
    canvas.alpha_composite(mock, (680, 550))
    draw.text((680, 925), "nine-slice usage mockup", fill=TEXT)
    return canvas


def nine_slice_preview(assets: dict[str, Image.Image]) -> Image.Image:
    row_height = 228
    canvas = checker((1240, row_height * len(assets) + 30), 16)
    draw = ImageDraw.Draw(canvas)
    for row, (name, asset) in enumerate(assets.items()):
        y = row * row_height + 24
        draw.text((18, y), name, fill=TEXT)
        if name == "divider":
            wide = nine_slice(asset, BORDERS[name], (1000, 8))
            canvas.alpha_composite(wide, (210, y + 42))
            continue
        wide = nine_slice(asset, BORDERS[name], (1000, 200))
        canvas.alpha_composite(wide, (210, y))
    return canvas


def nine_slice_vertical_preview(assets: dict[str, Image.Image]) -> Image.Image:
    names = [name for name in assets if name != "divider"]
    column_width = 220
    canvas = checker((column_width * len(names) + 20, 1060), 16)
    draw = ImageDraw.Draw(canvas)
    for column, name in enumerate(names):
        x = column * column_width + 14
        draw.text((x, 10), name, fill=TEXT)
        tall = nine_slice(assets[name], BORDERS[name], (200, 1000))
        canvas.alpha_composite(tall, (x, 42))
    return canvas


def verify(path: Path, expected: tuple[int, int]) -> None:
    with Image.open(path) as image:
        assert image.mode == "RGBA", (path, image.mode)
        assert image.size == expected, (path, image.size, expected)
        assert "icc_profile" not in image.info, path
        assert abs(image.info.get("dpi", (72, 72))[0] - 72) < 0.02, (path, image.info.get("dpi"))
        alpha = image.getchannel("A")
        assert alpha.getextrema()[0] < 255, f"{path}: no transparency"


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    base_assets: dict[str, Image.Image] = {}
    for name, generator in GENERATORS.items():
        for scale in (1, 3):
            image = generator(scale)
            suffix = "@3x" if scale == 3 else ""
            path = OUT / f"{name}{suffix}.png"
            save_png(image, path)
            verify(path, tuple(value * scale for value in SIZES[name]))
            if scale == 1:
                base_assets[name] = image
    save_png(preview(base_assets), OUT / "preview.png")
    save_png(nine_slice_preview(base_assets), OUT / "nine_slice_horizontal_preview.png")
    save_png(nine_slice_vertical_preview(base_assets), OUT / "nine_slice_vertical_preview.png")
    print(f"Generated and verified {len(GENERATORS) * 2} textures in {OUT}")


if __name__ == "__main__":
    main()
