#!/usr/bin/env python3
"""Карточка сборки для сниппета в мессенджере: HTML с OG-тегами + PNG 1200×630.

    qa/release-preview.py --out DIR --name tr-dev-20260914-1826 \
        --title "Игра · dev" --subtitle "14.09 18:26 · bb5abe30 · 44 МБ" \
        --apk https://…/dl/tr-dev-20260914-1826.apk --page https://…/dl-preview/tr-dev-20260914-1826.html \
        < строки-изменений

Зачем: ссылка на .apk — двоичный файл, сниппета у неё нет. nginx отдаёт
ботам-превью (Telegram, Twitter, Facebook…) вместо APK эту карточку, а
людям по той же ссылке — сам файл. Строки приходят со stdin, по одной на
изменение; всё, что не влезает, сворачивается в «…и ещё N».
"""
import argparse, html, os, sys
from PIL import Image, ImageDraw, ImageFont

FONTS = ["/System/Library/Fonts/Supplemental/Arial Bold.ttf", "/Library/Fonts/Arial Bold.ttf",
         "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"]
FONTS_R = ["/System/Library/Fonts/Supplemental/Arial.ttf", "/Library/Fonts/Arial.ttf",
           "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"]

def font(paths, size):
    for p in paths:
        if os.path.exists(p): return ImageFont.truetype(p, size)
    return ImageFont.load_default()

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True); ap.add_argument("--name", required=True)
    ap.add_argument("--title", required=True); ap.add_argument("--subtitle", default="")
    ap.add_argument("--apk", required=True); ap.add_argument("--page", required=True)
    ap.add_argument("--image", default=None, help="url картинки (по умолчанию <page>.png рядом)")
    ap.add_argument("--max-lines", type=int, default=12)
    a = ap.parse_args()
    lines = [l.strip() for l in sys.stdin.read().splitlines() if l.strip()]
    shown = lines[:a.max_lines]
    if len(lines) > a.max_lines: shown[-1] = f"…и ещё {len(lines) - a.max_lines + 1}"
    os.makedirs(a.out, exist_ok=True)

    # ── PNG 1200×630: тёмный фон, золотой заголовок, список ──────────────
    W, H = 1200, 630
    im = Image.new("RGB", (W, H), (18, 18, 26))
    d = ImageDraw.Draw(im)
    d.rectangle([0, 0, W, 6], fill=(212, 175, 55))
    f_title, f_sub, f_line = font(FONTS, 46), font(FONTS_R, 28), font(FONTS_R, 32)
    d.text((56, 44), a.title, font=f_title, fill=(232, 220, 190))
    d.text((56, 104), a.subtitle, font=f_sub, fill=(160, 160, 176))
    # ОДИН ПУНКТ — ОДНА СТРОКА: перенос ронял список на подпись, и длинные
    # пункты съедали место коротких. Что не влезает по ширине — укорачивается
    # многоточием; строк ровно столько, сколько помещается над подписью.
    y, step, right = 160, 44, W - 56
    def fit(text):
        while text and d.textlength("•  " + text, font=f_line) > right - 56:
            text = text[:-2].rstrip() + "…"
        return text
    rows = min(len(shown), (H - 70 - y) // step)
    if len(lines) > rows: shown = lines[:rows - 1] + [f"…и ещё {len(lines) - rows + 1}"]
    else: shown = lines[:rows]
    for l in shown:
        d.text((56, y), "•  " + fit(l), font=f_line, fill=(230, 230, 236)); y += step
    d.text((56, H - 52), a.apk.rsplit("/", 1)[-1], font=f_sub, fill=(120, 120, 140))
    png = os.path.join(a.out, a.name + ".png"); im.save(png, optimize=True)

    # ── HTML с OG-тегами ─────────────────────────────────────────────────
    img = a.image or (a.page.rsplit(".", 1)[0] + ".png")
    desc = "\n".join(shown)
    esc = lambda s: html.escape(s, quote=True)
    page = f"""<!doctype html><html lang="ru"><head><meta charset="utf-8">
<title>{esc(a.title)} — {esc(a.subtitle)}</title>
<meta property="og:type" content="website">
<meta property="og:title" content="{esc(a.title)} · {esc(a.subtitle)}">
<meta property="og:description" content="{esc(desc)}">
<meta property="og:image" content="{esc(img)}">
<meta property="og:image:width" content="{W}"><meta property="og:image:height" content="{H}">
<meta property="og:url" content="{esc(a.page)}">
<meta name="twitter:card" content="summary_large_image">
<meta name="viewport" content="width=device-width,initial-scale=1">
<style>body{{background:#12121a;color:#e6e6ec;font:18px/1.5 -apple-system,Arial,sans-serif;margin:0;padding:32px}}
h1{{color:#e8dcbe;font-size:28px;margin:0 0 4px}}p.sub{{color:#a0a0b0;margin:0 0 20px}}ul{{padding-left:22px}}
a.btn{{display:inline-block;margin-top:20px;padding:14px 28px;background:#d4af37;color:#12121a;border-radius:10px;text-decoration:none;font-weight:bold}}</style>
</head><body><h1>{esc(a.title)}</h1><p class="sub">{esc(a.subtitle)}</p>
<ul>{''.join(f'<li>{esc(l)}</li>' for l in lines)}</ul>
<a class="btn" href="{esc(a.apk)}">Скачать APK</a></body></html>
"""
    with open(os.path.join(a.out, a.name + ".html"), "w", encoding="utf-8") as f: f.write(page)
    print(png)

if __name__ == "__main__": main()
