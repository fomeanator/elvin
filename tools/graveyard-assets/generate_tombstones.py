#!/usr/bin/env python3
"""Generate the first four low-poly graveyard OBJ assets.

Output deliberately uses only v/vt/vn/f records.  Geometry is authored in
metres, Y-up, front toward +Z, with the origin centred on the footprint at
ground level.  Every triangle gets a flat geometric normal derived from its
counter-clockwise winding.
"""

from __future__ import annotations

import argparse
import math
from dataclasses import dataclass, field
from pathlib import Path

Vec2 = tuple[float, float]
Vec3 = tuple[float, float, float]
Tri = tuple[Vec3, Vec3, Vec3]


def sub(a: Vec3, b: Vec3) -> Vec3:
    return a[0] - b[0], a[1] - b[1], a[2] - b[2]


def cross(a: Vec3, b: Vec3) -> Vec3:
    return (
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    )


def normal(a: Vec3, b: Vec3, c: Vec3) -> Vec3:
    n = cross(sub(b, a), sub(c, a))
    length = math.sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2])
    if length < 1e-9:
        raise ValueError(f"degenerate triangle: {a}, {b}, {c}")
    return n[0] / length, n[1] / length, n[2] / length


@dataclass
class Mesh:
    triangles: list[Tri] = field(default_factory=list)

    def tri(self, a: Vec3, b: Vec3, c: Vec3) -> None:
        normal(a, b, c)
        self.triangles.append((a, b, c))

    def box(self, centre: Vec3, size: Vec3) -> None:
        cx, cy, cz = centre
        sx, sy, sz = (value * 0.5 for value in size)
        v = [
            (cx - sx, cy - sy, cz - sz),
            (cx + sx, cy - sy, cz - sz),
            (cx + sx, cy + sy, cz - sz),
            (cx - sx, cy + sy, cz - sz),
            (cx - sx, cy - sy, cz + sz),
            (cx + sx, cy - sy, cz + sz),
            (cx + sx, cy + sy, cz + sz),
            (cx - sx, cy + sy, cz + sz),
        ]
        faces = (
            (0, 3, 2), (0, 2, 1),       # -Z
            (4, 5, 6), (4, 6, 7),       # +Z
            (0, 4, 7), (0, 7, 3),       # -X
            (1, 2, 6), (1, 6, 5),       # +X
            (0, 1, 5), (0, 5, 4),       # -Y
            (3, 7, 6), (3, 6, 2),       # +Y
        )
        for a, b, c in faces:
            self.tri(v[a], v[b], v[c])

    def extrude(self, profile: list[Vec2], depth: float) -> None:
        """Extrude a simple CCW XY profile along Z."""
        indices = triangulate(profile)
        z0, z1 = -depth * 0.5, depth * 0.5
        back = [(x, y, z0) for x, y in profile]
        front = [(x, y, z1) for x, y in profile]

        for a, b, c in indices:
            self.tri(front[a], front[b], front[c])
            self.tri(back[c], back[b], back[a])
        for i in range(len(profile)):
            j = (i + 1) % len(profile)
            self.tri(back[i], back[j], front[j])
            self.tri(back[i], front[j], front[i])

    def transform(self, fn) -> None:
        self.triangles = [tuple(fn(v) for v in triangle) for triangle in self.triangles]


def area2(a: Vec2, b: Vec2, c: Vec2) -> float:
    return (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])


def point_in_triangle(p: Vec2, a: Vec2, b: Vec2, c: Vec2) -> bool:
    ab = area2(a, b, p)
    bc = area2(b, c, p)
    ca = area2(c, a, p)
    return ab >= -1e-9 and bc >= -1e-9 and ca >= -1e-9


def triangulate(profile: list[Vec2]) -> list[tuple[int, int, int]]:
    signed = sum(
        profile[i][0] * profile[(i + 1) % len(profile)][1]
        - profile[(i + 1) % len(profile)][0] * profile[i][1]
        for i in range(len(profile))
    )
    if signed <= 0:
        raise ValueError("profile must be counter-clockwise")

    remaining = list(range(len(profile)))
    result: list[tuple[int, int, int]] = []
    while len(remaining) > 3:
        for cursor, current in enumerate(remaining):
            previous = remaining[cursor - 1]
            following = remaining[(cursor + 1) % len(remaining)]
            a, b, c = profile[previous], profile[current], profile[following]
            if area2(a, b, c) <= 1e-9:
                continue
            if any(
                point_in_triangle(profile[index], a, b, c)
                for index in remaining
                if index not in (previous, current, following)
            ):
                continue
            result.append((previous, current, following))
            del remaining[cursor]
            break
        else:
            raise ValueError("profile could not be triangulated")
    result.append(tuple(remaining))
    return result


def chamfered_rect(cx: float, cy: float, width: float, height: float, bevel: float) -> list[Vec2]:
    x0, x1 = cx - width * 0.5, cx + width * 0.5
    y0, y1 = cy - height * 0.5, cy + height * 0.5
    return [
        (x0 + bevel, y0), (x1 - bevel, y0), (x1, y0 + bevel),
        (x1, y1 - bevel), (x1 - bevel, y1), (x0 + bevel, y1),
        (x0, y1 - bevel), (x0, y0 + bevel),
    ]


def add_rect_frustum(mesh: Mesh, y0: float, y1: float, bottom: Vec2, top: Vec2) -> None:
    bx, bz = bottom[0] * 0.5, bottom[1] * 0.5
    tx, tz = top[0] * 0.5, top[1] * 0.5
    low = [(-bx, y0, -bz), (bx, y0, -bz), (bx, y0, bz), (-bx, y0, bz)]
    high = [(-tx, y1, -tz), (tx, y1, -tz), (tx, y1, tz), (-tx, y1, tz)]
    # Bottom -Y and top +Y.
    mesh.tri(low[0], low[2], low[1]); mesh.tri(low[0], low[3], low[2])
    mesh.tri(high[0], high[1], high[2]); mesh.tri(high[0], high[2], high[3])
    # -Z, +X, +Z, -X.
    for i, j in ((0, 1), (1, 2), (2, 3), (3, 0)):
        mesh.tri(low[i], high[i], high[j])
        mesh.tri(low[i], high[j], low[j])


def rounded_slab() -> Mesh:
    mesh = Mesh()
    mesh.box((0.0, 0.06, 0.0), (0.76, 0.12, 0.34))
    profile = [
        (-0.31, 0.12), (0.31, 0.12), (0.31, 0.64), (0.29, 0.74),
        (0.22, 0.83), (0.11, 0.90), (0.0, 0.92), (-0.11, 0.90),
        (-0.22, 0.83), (-0.29, 0.74), (-0.31, 0.64), (-0.315, 0.36),
    ]
    mesh.extrude(profile, 0.20)
    return mesh


def stone_cross() -> Mesh:
    mesh = Mesh()
    mesh.box((0.0, 0.07, 0.0), (0.74, 0.14, 0.36))
    mesh.extrude(chamfered_rect(0.0, 0.64, 0.20, 1.00, 0.025), 0.22)
    mesh.extrude(chamfered_rect(0.0, 0.80, 0.70, 0.18, 0.025), 0.22)
    return mesh


def obelisk() -> Mesh:
    mesh = Mesh()
    mesh.box((0.0, 0.06, 0.0), (0.58, 0.12, 0.48))
    add_rect_frustum(mesh, 0.12, 0.22, (0.48, 0.40), (0.38, 0.32))
    add_rect_frustum(mesh, 0.22, 0.88, (0.30, 0.26), (0.22, 0.19))
    top = [(-0.11, 0.88, -0.095), (0.11, 0.88, -0.095),
           (0.11, 0.88, 0.095), (-0.11, 0.88, 0.095)]
    apex = (0.0, 1.04, 0.0)
    for i, j in ((0, 1), (1, 2), (2, 3), (3, 0)):
        mesh.tri(top[i], apex, top[j])
    return mesh


def leaning_cracked_slab() -> Mesh:
    mesh = Mesh()
    # Sixteen-point profile: the right-hand V-notch is an open silhouette crack.
    profile = [
        (-0.31, 0.0), (0.30, 0.0), (0.30, 0.36), (0.21, 0.42),
        (0.30, 0.49), (0.30, 0.64), (0.27, 0.74), (0.20, 0.83),
        (0.10, 0.89), (0.0, 0.91), (-0.10, 0.89), (-0.20, 0.83),
        (-0.27, 0.74), (-0.30, 0.64), (-0.31, 0.42), (-0.32, 0.18),
    ]
    mesh.extrude(profile, 0.18)
    angle = math.radians(-9.0)
    cosine, sine = math.cos(angle), math.sin(angle)

    def rotate(v: Vec3) -> Vec3:
        x, y, z = v
        return x * cosine - y * sine, x * sine + y * cosine, z

    mesh.transform(rotate)
    vertices = [vertex for triangle in mesh.triangles for vertex in triangle]
    min_y = min(vertex[1] for vertex in vertices)
    min_x = min(vertex[0] for vertex in vertices)
    max_x = max(vertex[0] for vertex in vertices)
    centre_x = (min_x + max_x) * 0.5
    mesh.transform(lambda v: (v[0] - centre_x, v[1] - min_y, v[2]))
    return mesh


def write_obj(path: Path, mesh: Mesh) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    lines: list[str] = []
    for triangle in mesh.triangles:
        for x, y, z in triangle:
            lines.append(f"v {x:.6f} {y:.6f} {z:.6f}")
    lines.extend(("vt 0.000000 0.000000", "vt 1.000000 0.000000", "vt 0.500000 1.000000"))
    for triangle in mesh.triangles:
        nx, ny, nz = normal(*triangle)
        lines.append(f"vn {nx:.6f} {ny:.6f} {nz:.6f}")
    for index in range(len(mesh.triangles)):
        vertex = index * 3 + 1
        normal_index = index + 1
        lines.append(
            f"f {vertex}/1/{normal_index} {vertex + 1}/2/{normal_index} "
            f"{vertex + 2}/3/{normal_index}"
        )
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def validate(path: Path, expected_triangles: int) -> str:
    allowed = {"v", "vt", "vn", "f"}
    vertices: list[Vec3] = []
    normals: list[Vec3] = []
    faces: list[list[str]] = []
    for line in path.read_text(encoding="utf-8").splitlines():
        fields = line.split()
        if not fields or fields[0] not in allowed:
            raise ValueError(f"{path}: forbidden OBJ record: {line}")
        if fields[0] == "v":
            vertices.append(tuple(float(value) for value in fields[1:4]))
        elif fields[0] == "vn":
            normals.append(tuple(float(value) for value in fields[1:4]))
        elif fields[0] == "f":
            faces.append(fields[1:])

    if len(faces) != expected_triangles or any(len(face) != 3 for face in faces):
        raise ValueError(f"{path}: expected {expected_triangles} triangles, got {len(faces)}")
    for face in faces:
        refs = [item.split("/") for item in face]
        tri = tuple(vertices[int(ref[0]) - 1] for ref in refs)
        geometric = normal(*tri)
        stored = normals[int(refs[0][2]) - 1]
        if sum(geometric[i] * stored[i] for i in range(3)) < 0.999:
            raise ValueError(f"{path}: winding/normal mismatch")

    xs, ys, zs = zip(*vertices)
    if abs(min(ys)) > 1e-6:
        raise ValueError(f"{path}: base must be y=0, got {min(ys)}")
    if abs(min(xs) + max(xs)) > 1e-5 or abs(min(zs) + max(zs)) > 1e-5:
        raise ValueError(f"{path}: footprint must be centred on origin")
    return (
        f"{path.name}: {len(faces)} tris, "
        f"{max(xs) - min(xs):.2f}×{max(ys) - min(ys):.2f}×{max(zs) - min(zs):.2f} m"
    )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("output", nargs="?", default="docs/art/graveyard-battle-v1/models")
    args = parser.parse_args()
    output = Path(args.output)
    assets = {
        "надгробие_плита.obj": (rounded_slab(), 56),
        "надгробие_крест_камень.obj": (stone_cross(), 68),
        "надгробие_обелиск.obj": (obelisk(), 40),
        "надгробие_покосившееся.obj": (leaning_cracked_slab(), 60),
    }
    for filename, (mesh, count) in assets.items():
        path = output / filename
        write_obj(path, mesh)
        print(validate(path, count))


if __name__ == "__main__":
    main()
