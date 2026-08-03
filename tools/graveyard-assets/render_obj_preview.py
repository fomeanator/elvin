#!/usr/bin/env python3
"""Render a small orthographic contact sheet for generated OBJ QA."""

from __future__ import annotations

import argparse
import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


def dot(a, b):
    return sum(x * y for x, y in zip(a, b))


def sub(a, b):
    return tuple(x - y for x, y in zip(a, b))


def cross(a, b):
    return (a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0])


def unit(v):
    length = math.sqrt(dot(v, v))
    return tuple(x / length for x in v)


def load(path: Path):
    vertices = []
    faces = []
    for line in path.read_text(encoding="utf-8").splitlines():
        fields = line.split()
        if fields[0] == "v":
            vertices.append(tuple(float(x) for x in fields[1:4]))
        elif fields[0] == "f":
            faces.append(tuple(vertices[int(x.split("/")[0]) - 1] for x in fields[1:4]))
    return faces


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("input")
    parser.add_argument("output")
    args = parser.parse_args()
    files = sorted(Path(args.input).glob("*.obj"))
    cell_w, cell_h = 440, 520
    image = Image.new("RGB", (cell_w * len(files), cell_h), (22, 25, 34))
    draw = ImageDraw.Draw(image)
    view = unit((2.8, 1.8, 4.0))
    right = unit(cross((0.0, 1.0, 0.0), view))
    up = unit(cross(view, right))
    light = unit((-1.5, 3.0, 2.0))

    try:
        font = ImageFont.truetype("/System/Library/Fonts/Supplemental/Arial Unicode.ttf", 20)
    except OSError:
        font = ImageFont.load_default()

    for index, path in enumerate(files):
        faces = load(path)
        projected = []
        all_points = []
        for triangle in faces:
            points = [(dot(v, right), dot(v, up)) for v in triangle]
            all_points.extend(points)
        min_x = min(p[0] for p in all_points); max_x = max(p[0] for p in all_points)
        min_y = min(p[1] for p in all_points); max_y = max(p[1] for p in all_points)
        scale = min(340.0 / max(max_x - min_x, 1e-6), 390.0 / max(max_y - min_y, 1e-6))
        centre_x = (min_x + max_x) * 0.5
        centre_y = (min_y + max_y) * 0.5
        offset_x = index * cell_w + cell_w * 0.5
        offset_y = 430.0

        for triangle in faces:
            edge1, edge2 = sub(triangle[1], triangle[0]), sub(triangle[2], triangle[0])
            n = unit(cross(edge1, edge2))
            if dot(n, view) <= 0.0:
                continue
            shade = 0.30 + 0.70 * max(0.0, dot(n, light))
            colour = tuple(int(base * shade) for base in (174, 181, 190))
            points = [
                (offset_x + (dot(v, right) - centre_x) * scale,
                 offset_y - (dot(v, up) - min_y) * scale)
                for v in triangle
            ]
            depth = sum(dot(v, view) for v in triangle) / 3.0
            projected.append((depth, points, colour))
        for _, points, colour in sorted(projected):
            draw.polygon(points, fill=colour, outline=(30, 34, 44))

        label = path.stem
        box = draw.textbbox((0, 0), label, font=font)
        draw.text((index * cell_w + (cell_w - (box[2] - box[0])) * 0.5, 480),
                  label, fill=(218, 220, 226), font=font)
    destination = Path(args.output)
    destination.parent.mkdir(parents=True, exist_ok=True)
    image.save(destination)


if __name__ == "__main__":
    main()
