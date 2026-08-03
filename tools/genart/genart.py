#!/usr/bin/env python3
"""genart — спрайты новеллы через HydraAI, с сохранением НАШЕГО стиля.

Зачем отдельный инструмент, а не «сходить в чат и скачать картинку»:

1. **Референс обязателен.** Модель, получившая наш собственный спрайт, рисует
   ТОГО ЖЕ персонажа в другой позе — та же броня, тот же плащ, та же палитра.
   Без референса выходит «похожий скелет», и в ряду поз это видно сразу.
2. **Фон надо снимать.** Модель отдаёт JPEG на чёрном; игре нужен PNG с альфой,
   обрезанный по фигуре. Руками это десять минут на кадр и разъезжающиеся
   пропорции между позами.
3. **Партия, а не штука.** Поз у одного врага четыре, врагов девять. Задачи
   описываются списком, прогон повторяем, результат ложится сразу в пакет.

Ключ берётся из ~/.config/lvn/hydra.env — в репозитории его быть не должно,
репозиторий публичный.

    python3 tools/genart/genart.py tools/genart/tasks/duel-poses.json
    python3 tools/genart/genart.py --one --ref art/skeleton_idle.png \\
        --prompt "то же существо, но замахивается мечом" --out art/skeleton_attack.png
"""

import argparse
import base64
import json
import os
import pathlib
import re
import sys
import time
import urllib.request

HYDRA_ENV = pathlib.Path.home() / ".config" / "lvn" / "hydra.env"
MODEL = "gemini-3-pro-image"

# Общий хвост промпта: он держит партию поз в одном кадре и масштабе. Без него
# модель произвольно меняет крупность, и фигуры в ряду «прыгают» по росту.
STYLE_TAIL = (
    " Полный рост, вид строго спереди, фигура по центру кадра, ступни видны целиком, "
    "тот же масштаб и та же крупность, что у референса. Фон СПЛОШНОЙ ЧЁРНЫЙ, без пола, "
    "без теней на фоне, без рамки и подписей. Освещение и палитра как у референса."
)


def load_key():
    if not HYDRA_ENV.exists():
        sys.exit(f"нет {HYDRA_ENV} — положите туда HYDRA_KEY и HYDRA_BASE")
    env = {}
    for line in HYDRA_ENV.read_text().splitlines():
        line = line.strip()
        if line and not line.startswith("#") and "=" in line:
            k, v = line.split("=", 1)
            env[k.strip()] = v.strip()
    return env["HYDRA_BASE"], env["HYDRA_KEY"]


def generate(base, key, prompt, ref_path=None, tries=3):
    """Один кадр. Возвращает сырые байты изображения."""
    content = [{"type": "text", "text": prompt + STYLE_TAIL}]
    if ref_path:
        raw = pathlib.Path(ref_path).read_bytes()
        content.append({
            "type": "image_url",
            "image_url": {"url": "data:image/png;base64," + base64.b64encode(raw).decode()},
        })
    body = {"model": MODEL, "messages": [{"role": "user", "content": content}]}
    req = urllib.request.Request(
        base + "/v1/chat/completions",
        data=json.dumps(body).encode(),
        headers={"Authorization": "Bearer " + key, "Content-Type": "application/json"},
    )
    for attempt in range(1, tries + 1):
        try:
            answer = json.loads(urllib.request.urlopen(req, timeout=420).read())
            text = answer["choices"][0]["message"]["content"]
            if not isinstance(text, str):
                text = json.dumps(text, ensure_ascii=False)
            # Картинка приходит ВНУТРИ текста как data-url: у этих моделей нет
            # отдельного поля, и /v1/images/generations они не поддерживают.
            m = re.search(r"data:image/(\w+);base64,([A-Za-z0-9+/=]+)", text)
            if not m:
                raise RuntimeError("в ответе нет картинки: " + text[:160])
            return base64.b64decode(m.group(2))
        except Exception as exc:  # noqa: BLE001 — сеть и лимиты, ретрай осмыслен
            if attempt == tries:
                raise
            wait = 8 * attempt
            print(f"    попытка {attempt} не вышла ({exc}); жду {wait} с", flush=True)
            time.sleep(wait)
    raise RuntimeError("недостижимо")


def cut_background(raw, feather=1, threshold=20):
    """Чёрный фон → альфа, обрезка по фигуре.

    Заливкой ОТ КРАЁВ, а не «всё тёмное прозрачно»: у скелета своя тень в
    доспехе и чёрные провалы между рёбрами, и порог по яркости выел бы их
    вместе с фоном. Заливка трогает только то, что связано с краем кадра.
    """
    from PIL import Image
    from collections import deque
    import io

    im = Image.open(io.BytesIO(raw)).convert("RGBA")
    w, h = im.size
    px = im.load()
    alpha = [[255] * w for _ in range(h)]

    def dark(x, y):
        r, g, b, _ = px[x, y]
        # По САМОМУ ЯРКОМУ каналу, а не по сумме: тёмно-багровый плащ
        # (60,12,18) в сумме почти чёрный и раньше выедался вместе с фоном.
        # Фон у модели ровный, поэтому строгий порог безопаснее щедрого —
        # лучше оставить ободок, чем прогрызть дыру в фигуре.
        return max(r, g, b) <= threshold

    seen = [[False] * w for _ in range(h)]
    q = deque()
    for x in range(w):
        for y in (0, h - 1):
            if dark(x, y) and not seen[y][x]:
                seen[y][x] = True
                q.append((x, y))
    for y in range(h):
        for x in (0, w - 1):
            if dark(x, y) and not seen[y][x]:
                seen[y][x] = True
                q.append((x, y))
    while q:
        x, y = q.popleft()
        alpha[y][x] = 0
        for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            nx, ny = x + dx, y + dy
            if 0 <= nx < w and 0 <= ny < h and not seen[ny][nx] and dark(nx, ny):
                seen[ny][nx] = True
                q.append((nx, ny))

    for y in range(h):
        for x in range(w):
            r, g, b, _ = px[x, y]
            px[x, y] = (r, g, b, alpha[y][x])

    if feather:
        from PIL import ImageFilter
        a = im.getchannel("A").filter(ImageFilter.GaussianBlur(feather))
        im.putalpha(a)

    box = im.getbbox()
    im = im.crop(box) if box else im

    # Страж: вырезание — самая рискованная часть конвейера, и ошибается оно
    # молча. Числа в лог, чтобы «съело половину героя» не уехало в игру.
    a = im.getchannel("A")
    hist = a.histogram()
    clear = sum(hist[:16])
    total = im.width * im.height
    share = clear * 100 // max(total, 1)
    if share > 80:
        print(f"    ВНИМАНИЕ: прозрачно {share}% — вырезано лишнее, проверьте кадр")
    elif share < 8:
        print(f"    ВНИМАНИЕ: прозрачно всего {share}% — фон почти не снялся")
    else:
        print(f"    фон снят: прозрачно {share}%")
    return im


def run_task(base, key, task, out_root):
    out = pathlib.Path(task["out"])
    if not out.is_absolute():
        out = out_root / out
    if out.exists() and not task.get("force"):
        print(f"  · {out.name} — уже есть, пропускаю")
        return
    print(f"  · {out.name} …", flush=True)
    raw = generate(base, key, task["prompt"], task.get("ref"))
    im = cut_background(raw)
    if "height" in task:
        k = task["height"] / im.height
        im = im.resize((max(1, int(im.width * k)), task["height"]))
    out.parent.mkdir(parents=True, exist_ok=True)
    im.save(out)
    print(f"    → {out} ({im.width}×{im.height})")


def main():
    ap = argparse.ArgumentParser(description="спрайты через HydraAI")
    ap.add_argument("tasks", nargs="?", help="json со списком задач")
    ap.add_argument("--one", action="store_true", help="одна задача из аргументов")
    ap.add_argument("--ref")
    ap.add_argument("--prompt")
    ap.add_argument("--out")
    ap.add_argument("--height", type=int)
    args = ap.parse_args()

    base, key = load_key()
    root = pathlib.Path.cwd()

    if args.one:
        task = {"prompt": args.prompt, "out": args.out, "ref": args.ref, "force": True}
        if args.height:
            task["height"] = args.height
        run_task(base, key, task, root)
        return

    if not args.tasks:
        ap.error("нужен файл задач или --one")
    doc = json.loads(pathlib.Path(args.tasks).read_text())
    tasks = doc["tasks"]
    print(f"задач: {len(tasks)}")
    for i, t in enumerate(tasks, 1):
        print(f"[{i}/{len(tasks)}]")
        try:
            run_task(base, key, t, root)
        except Exception as exc:  # noqa: BLE001 — партия не должна падать из-за одного кадра
            print(f"    НЕ ВЫШЛО: {exc}")
        # Лимит 10 запросов в минуту: держим паузу сами, иначе партия из
        # тридцати поз упрётся в 429 на середине.
        time.sleep(7)


if __name__ == "__main__":
    main()
