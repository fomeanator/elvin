#!/usr/bin/env python3
"""Build a seam-safe texture source and calibrate a prepared albedo.

The image generator is useful for semantic edits, but an edit may redraw the
outer pixels and break a previously good tile.  ``composite`` keeps the exact
edge band from a measured seamless base and feathers the edited centre into it.
``calibrate`` applies a monotonic, hue-preserving luminance curve so an albedo
meets the percentile contract without baking lighting into it.
"""

from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np
from PIL import Image


def luminance(rgb: np.ndarray) -> np.ndarray:
    return 0.2126 * rgb[..., 0] + 0.7152 * rgb[..., 1] + 0.0722 * rgb[..., 2]


def levels(rgb: np.ndarray) -> tuple[float, float, float]:
    return tuple(float(x) for x in np.percentile(luminance(rgb), [5, 50, 95]))


def seam(rgb: np.ndarray) -> tuple[float, float, float]:
    def mad(a: np.ndarray, b: np.ndarray) -> float:
        return float(np.abs(a.astype(np.float32) - b.astype(np.float32)).mean())

    vertical = mad(rgb[:, 0], rgb[:, -1])
    horizontal = mad(rgb[0, :], rgb[-1, :])
    mid = rgb.shape[1] // 2
    ordinary = mad(rgb[:, mid], rgb[:, mid + 1])
    return vertical, horizontal, ordinary


def load_rgb(path: str, size: tuple[int, int] | None = None) -> np.ndarray:
    image = Image.open(path).convert("RGB")
    if size is not None and image.size != size:
        image = image.resize(size, Image.Resampling.LANCZOS)
    return np.asarray(image, dtype=np.float32) / 255.0


def save_rgb(path: str, rgb: np.ndarray) -> None:
    destination = Path(path)
    destination.parent.mkdir(parents=True, exist_ok=True)
    Image.fromarray(np.clip(rgb * 255.0 + 0.5, 0, 255).astype(np.uint8)).save(destination)


def composite(base_path: str, edit_path: str, output_path: str, feather: float) -> None:
    base_image = Image.open(base_path).convert("RGB")
    base = np.asarray(base_image, dtype=np.float32) / 255.0
    edit = load_rgb(edit_path, base_image.size)
    height, width = base.shape[:2]

    x = np.linspace(0.0, 1.0, width, dtype=np.float32)
    y = np.linspace(0.0, 1.0, height, dtype=np.float32)
    edge_x = np.minimum(x, 1.0 - x)[None, :]
    edge_y = np.minimum(y, 1.0 - y)[:, None]
    distance = np.minimum(edge_x, edge_y)
    t = np.clip(distance / feather, 0.0, 1.0)
    mask = t * t * (3.0 - 2.0 * t)
    result = base * (1.0 - mask[..., None]) + edit * mask[..., None]
    save_rgb(output_path, result)

    rgb8 = np.clip(result * 255.0 + 0.5, 0, 255).astype(np.uint8)
    v, h, ordinary = seam(rgb8)
    print(f"composite seam={v:.1f}/{h:.1f}, ordinary={ordinary:.1f}")


def calibrate(input_path: str, output_path: str, targets: tuple[float, float, float]) -> None:
    rgb = load_rgb(input_path)
    source = np.asarray(levels(rgb), dtype=np.float32)
    knots_x = np.asarray([0.0, source[0], source[1], source[2], 1.0], dtype=np.float32)
    knots_y = np.asarray([0.0, targets[0], targets[1], targets[2], 1.0], dtype=np.float32)
    lum = luminance(rgb)
    mapped = np.interp(lum, knots_x, knots_y).astype(np.float32)
    scale = mapped / np.maximum(lum, 1e-5)
    result = np.clip(rgb * scale[..., None], 0.0, 1.0)
    save_rgb(output_path, result)

    rgb8 = np.clip(result * 255.0 + 0.5, 0, 255).astype(np.uint8)
    p5, p50, p95 = levels(result)
    v, h, ordinary = seam(rgb8)
    print(f"albedo p5={p5:.3f} p50={p50:.3f} p95={p95:.3f}")
    print(f"albedo seam={v:.1f}/{h:.1f}, ordinary={ordinary:.1f}")


def main() -> None:
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)

    compose = sub.add_parser("composite")
    compose.add_argument("base")
    compose.add_argument("edit")
    compose.add_argument("output")
    compose.add_argument("--feather", type=float, default=0.22)

    curve = sub.add_parser("calibrate")
    curve.add_argument("input")
    curve.add_argument("output")
    curve.add_argument("--p5", type=float, default=0.045)
    curve.add_argument("--p50", type=float, default=0.22)
    curve.add_argument("--p95", type=float, default=0.47)

    args = parser.parse_args()
    if args.command == "composite":
        composite(args.base, args.edit, args.output, args.feather)
    else:
        calibrate(args.input, args.output, (args.p5, args.p50, args.p95))


if __name__ == "__main__":
    main()
