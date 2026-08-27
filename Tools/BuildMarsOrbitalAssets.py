#!/usr/bin/env python3
"""Convert SpaceEngine Mars elevation tiles into an orbital normal map."""

from __future__ import annotations

import argparse
import io
import math
import shutil
import tempfile
import zipfile
from pathlib import Path

import numpy as np
from PIL import Image


FACES = ("pos_x", "neg_x", "pos_y", "neg_y", "pos_z", "neg_z")
TILES_PER_FACE = 32
SOURCE_TILE_SIZE = 258
CONTENT_TILE_SIZE = 256
FACE_SIZE = TILES_PER_FACE * CONTENT_TILE_SIZE


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--width", type=int, default=16384)
    parser.add_argument("--longitude-offset", type=float, default=0.0)
    parser.add_argument("--height-range-meters", type=float, default=30000.0)
    return parser.parse_args()


def assemble_faces(archive: zipfile.ZipFile, temp_root: Path) -> dict[str, np.memmap]:
    faces: dict[str, np.memmap] = {}
    for face_index, face_name in enumerate(FACES, start=1):
        face_path = temp_root / f"{face_name}.r16"
        face = np.memmap(face_path, dtype=np.uint16, mode="w+", shape=(FACE_SIZE, FACE_SIZE))
        for row in range(TILES_PER_FACE):
            destination_row = TILES_PER_FACE - 1 - row
            y0 = destination_row * CONTENT_TILE_SIZE
            for column in range(TILES_PER_FACE):
                entry = (
                    "textures/planets/Mars/Bump-PBC/"
                    f"{face_name}/5_{row}_{column}.png"
                )
                with Image.open(io.BytesIO(archive.read(entry))) as image:
                    tile = np.asarray(image, dtype=np.uint16)
                if tile.shape != (SOURCE_TILE_SIZE, SOURCE_TILE_SIZE):
                    raise ValueError(f"Unexpected tile shape {tile.shape}: {entry}")
                x0 = column * CONTENT_TILE_SIZE
                face[y0:y0 + CONTENT_TILE_SIZE, x0:x0 + CONTENT_TILE_SIZE] = \
                    np.flipud(tile[1:-1, 1:-1])
        face.flush()
        faces[face_name] = face
        print(f"assembled face {face_index}/6: {face_name}", flush=True)
    return faces


def sample_face(face: np.ndarray, u: np.ndarray, v: np.ndarray) -> np.ndarray:
    x = np.clip((u + 1.0) * 0.5 * (FACE_SIZE - 1), 0.0, FACE_SIZE - 1.0)
    y = np.clip((1.0 - v) * 0.5 * (FACE_SIZE - 1), 0.0, FACE_SIZE - 1.0)
    x0 = x.astype(np.int32)
    y0 = y.astype(np.int32)
    x1 = np.minimum(x0 + 1, FACE_SIZE - 1)
    y1 = np.minimum(y0 + 1, FACE_SIZE - 1)
    fx = x - x0
    fy = y - y0
    top = face[y0, x0] * (1.0 - fx) + face[y0, x1] * fx
    bottom = face[y1, x0] * (1.0 - fx) + face[y1, x1] * fx
    return top * (1.0 - fy) + bottom * fy


def project_equirectangular(
    faces: dict[str, np.memmap],
    output_path: Path,
    width: int,
    longitude_offset_degrees: float,
) -> np.memmap:
    height = width // 2
    output = np.memmap(output_path, dtype=np.uint16, mode="w+", shape=(height, width))
    longitude_offset = math.radians(longitude_offset_degrees)
    longitude = ((np.arange(width, dtype=np.float64) + 0.5) / width) * (2.0 * math.pi) \
        - math.pi - longitude_offset

    strip_height = 128
    for y_start in range(0, height, strip_height):
        y_end = min(y_start + strip_height, height)
        latitude = math.pi * 0.5 - (
            (np.arange(y_start, y_end, dtype=np.float64) + 0.5) / height
        ) * math.pi
        lon, lat = np.meshgrid(longitude, latitude)
        x = np.cos(lat) * np.cos(lon)
        y = np.sin(lat)
        # SpaceEngine's positive-Z face runs toward decreasing east longitude.
        # Negating Z keeps the MOLA relief aligned with conventional NASA
        # equirectangular surface maps, including the GLB albedo used here.
        z = -np.cos(lat) * np.sin(lon)
        ax, ay, az = np.abs(x), np.abs(y), np.abs(z)
        projected = np.empty(x.shape, dtype=np.float64)

        major_x = (ax >= ay) & (ax >= az)
        major_y = (ay > ax) & (ay >= az)
        major_z = ~(major_x | major_y)

        samples = (
            (major_x & (x >= 0), "pos_x", -z / ax, -y / ax),
            (major_x & (x < 0), "neg_x", z / ax, -y / ax),
            (major_y & (y >= 0), "pos_y", x / ay, z / ay),
            (major_y & (y < 0), "neg_y", x / ay, -z / ay),
            (major_z & (z >= 0), "pos_z", x / az, -y / az),
            (major_z & (z < 0), "neg_z", -x / az, -y / az),
        )
        for mask, face_name, u, v in samples:
            projected[mask] = sample_face(faces[face_name], u[mask], v[mask])

        output[y_start:y_end] = np.clip(np.rint(projected), 0, 65535).astype(np.uint16)
        print(f"projected rows {y_end}/{height}", flush=True)
    output.flush()
    return output


def build_normal_map(
    height_map: np.memmap,
    output_path: Path,
    height_range_meters: float,
) -> None:
    height, width = height_map.shape
    temp_normal = output_path.with_suffix(".rgb")
    normal = np.memmap(temp_normal, dtype=np.uint8, mode="w+", shape=(height, width, 3))
    code_to_meters = height_range_meters / 65535.0
    north_south_meters = math.pi * 3_389_500.0 / height

    strip_height = 128
    for y_start in range(0, height, strip_height):
        y_end = min(y_start + strip_height, height)
        rows = np.arange(y_start, y_end)
        center = height_map[y_start:y_end].astype(np.float32)
        west = np.roll(center, 1, axis=1)
        east = np.roll(center, -1, axis=1)
        north = height_map[np.maximum(rows - 1, 0)].astype(np.float32)
        south = height_map[np.minimum(rows + 1, height - 1)].astype(np.float32)

        latitude = math.pi * 0.5 - ((rows + 0.5) / height) * math.pi
        east_west_meters = (
            (2.0 * math.pi * 3_389_500.0 / width)
            * np.maximum(np.cos(latitude), 0.02)
        )[:, None]
        slope_east = (east - west) * code_to_meters / (2.0 * east_west_meters)
        slope_south = (south - north) * code_to_meters / (2.0 * north_south_meters)

        nx = -slope_east
        ny = -slope_south
        nz = np.ones_like(nx)
        inverse_length = 1.0 / np.sqrt(nx * nx + ny * ny + nz * nz)
        normal[y_start:y_end, :, 0] = np.clip((nx * inverse_length * 0.5 + 0.5) * 255, 0, 255)
        normal[y_start:y_end, :, 1] = np.clip((ny * inverse_length * 0.5 + 0.5) * 255, 0, 255)
        normal[y_start:y_end, :, 2] = np.clip((nz * inverse_length * 0.5 + 0.5) * 255, 0, 255)
        print(f"generated normals {y_end}/{height}", flush=True)

    normal.flush()
    output_path.parent.mkdir(parents=True, exist_ok=True)
    Image.fromarray(normal, mode="RGB").save(output_path, compress_level=4)
    del normal
    temp_normal.unlink(missing_ok=True)


def main() -> None:
    args = parse_args()
    if args.width <= 0 or args.width % 2:
        raise ValueError("--width must be a positive even number")
    args.output.parent.mkdir(parents=True, exist_ok=True)

    temp_root = Path(tempfile.mkdtemp(prefix="mars-orbital-"))
    try:
        with zipfile.ZipFile(args.source) as archive:
            faces = assemble_faces(archive, temp_root)
            height_path = temp_root / "mars-equirect.r16"
            height_map = project_equirectangular(
                faces,
                height_path,
                args.width,
                args.longitude_offset,
            )
            build_normal_map(height_map, args.output, args.height_range_meters)
        print(f"wrote {args.output.resolve()}", flush=True)
    finally:
        shutil.rmtree(temp_root, ignore_errors=True)


if __name__ == "__main__":
    main()
