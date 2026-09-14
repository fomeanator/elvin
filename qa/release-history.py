#!/usr/bin/env python3
"""Журнал выпусков канала для страницы сборки: свежие сверху, не больше 60.
Читает окружение: HIST (файл json), NAME, SHA, PAGE, LINES (по строке на пункт), CHECK."""
import json, os, datetime
p = os.environ["HIST"]
hist = json.load(open(p, encoding="utf-8")) if os.path.exists(p) else []
hist = [h for h in hist if h.get("name") != os.environ["NAME"]]
hist.insert(0, {"name": os.environ["NAME"], "stamp": datetime.datetime.now().strftime("%d.%m %H:%M"),
                "sha": os.environ["SHA"], "page": os.environ["PAGE"],
                "lines": [l for l in os.environ.get("LINES", "").splitlines() if l.strip()],
                "check": os.environ.get("CHECK", "")})
json.dump(hist[:60], open(p, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
