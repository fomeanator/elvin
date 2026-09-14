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
    ap.add_argument("--history", default="", help="json со списком выпусков канала — страница показывает историю изменений")
    ap.add_argument("--check", default="", help="путь проверки одной строкой — идёт в текст сниппета (он обрезается после ~200 символов), список изменений остаётся картинке")
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
    # ИСТОРИЯ СБОРОК (Илья 15.09: «надо, чтобы тут история хранилась
    # изменений»): страница показывает все выпуски канала, свежие сверху.
    history_html = ""
    if a.history and os.path.exists(a.history):
        try:
            import json
            hist = json.load(open(a.history, encoding="utf-8"))
            prev = [h for h in hist if h.get("name") != a.name][:30]
            if prev:
                blocks = "".join(
                    f'<h3>{html.escape(h.get("stamp",""))} · {html.escape(h.get("sha",""))} · <a href="{html.escape(h.get("page",""))}">{html.escape(h.get("name",""))}</a></h3>'
                    f'<ul>{"".join(f"<li>{html.escape(l)}</li>" for l in h.get("lines", []))}</ul>' for h in prev)
                history_html = f'<h2>История сборок</h2><div class="hist">{blocks}</div>'
        except Exception:
            history_html = ""
    img = a.image or (a.page.rsplit(".", 1)[0] + ".png")
    # ТЕКСТ СНИППЕТА КОРОТКИЙ: мессенджер показывает три-четыре строки и
    # режет. Туда идёт путь проверки («что потыкать»), а решённые пункты
    # целиком показывает картинка.
    desc = a.check.strip() if a.check.strip() else " · ".join(shown[:3])
    esc = lambda s: html.escape(s, quote=True)
    page = f"""<!doctype html><html lang="ru"><head><meta charset="utf-8">
<title>{esc(a.title)}</title>
<meta property="og:type" content="website">
<meta property="og:title" content="{esc(a.title)}">
<meta property="og:description" content="{esc(desc)}">
<meta property="og:image" content="{esc(img)}">
<meta property="og:image:width" content="{W}"><meta property="og:image:height" content="{H}">
<meta property="og:url" content="{esc(a.page)}">
<meta name="twitter:card" content="summary_large_image">
<meta name="viewport" content="width=device-width,initial-scale=1">
<style>
body{{background:#12121a;color:#e6e6ec;font:17px/1.55 -apple-system,Segoe UI,Roboto,Arial,sans-serif;margin:0;padding:28px 22px 48px;max-width:720px;margin:0 auto}}
h1{{color:#e8dcbe;font-size:26px;margin:0 0 6px;line-height:1.25}}
p.sub{{color:#a0a0b0;margin:0 0 22px;font-size:15px}}
a.btn{{display:block;text-align:center;padding:16px 24px;background:#d4af37;color:#12121a;border-radius:12px;text-decoration:none;font-weight:700;font-size:18px;margin:0 0 26px}}
h2{{color:#e8dcbe;font-size:16px;letter-spacing:.04em;text-transform:uppercase;margin:24px 0 8px}}
ul{{padding-left:22px;margin:0}} li{{margin:6px 0}}
.check{{background:#1b1b26;border-left:3px solid #d4af37;padding:12px 16px;border-radius:0 10px 10px 0}}
.foot{{color:#6f6f80;font-size:13px;margin-top:30px}}
.hist h3{{color:#a0a0b0;font-size:14px;font-weight:600;margin:18px 0 4px}} .hist a{{color:#d4af37;text-decoration:none}} .hist ul{{color:#bdbdc8}}
</style></head><body>
<h1>{esc(a.title)}</h1><p class="sub">{esc(a.subtitle)}</p>
<a class="btn" href="{esc(a.apk)}">Скачать APK</a>
{f'<h2>Как проверить</h2><div class="check">{esc(a.check.strip())}</div>' if a.check.strip() else ''}
<h2>Что изменилось</h2>
<ul>{''.join(f'<li>{esc(l)}</li>' for l in lines)}</ul>
{history_html}
<p class="foot">{esc(a.apk.rsplit("/",1)[-1])}</p>
</body></html>
"""
    with open(os.path.join(a.out, a.name + ".html"), "w", encoding="utf-8") as f: f.write(page)
    print(png)

if __name__ == "__main__": main()
