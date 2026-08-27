#!/usr/bin/env python3
"""Build a high-resolution orbital Mars albedo from the GLB color and PAK relief."""

from __future__ import annotations

import argparse
import io
import json
import struct
import tempfile
from pathlib import Path

import numpy as np
from PIL import Image, ImageEnhance

Image.MAX_IMAGE_PIXELS = None


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("glb", type=Path)
    parser.add_argument("normal", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--width", type=int, default=16384)
    parser.add_argument("--saturation", type=float, default=0.82)
    parser.add_argument("--contrast", type=float, default=1.08)
    parser.add_argument("--relief-strength", type=float, default=0.13)
    parser.add_argument("--slope-darkening", type=float, default=0.035)
    parser.add_argument("--quality", type=int, default=95)
    return parser.parse_args()


def extract_base_color(glb_path: Path) -> Image.Image:
    with glb_path.open("rb") as source:
        magic, version, _ = struct.unpack("<4sII", source.read(12))
        if magic != b"glTF" or version != 2:
            raise ValueError(f"Unsupported GLB header: {glb_path}")

        json_length, json_type = struct.unpack("<I4s", source.read(8))
        if json_type != b"JSON":
            raise ValueError("The first GLB chunk is not JSON")
        document = json.loads(source.read(json_length))

        binary_length, binary_type = struct.unpack("<I4s", source.read(8))
        if binary_type != b"BIN\x00":
            raise ValueError("The second GLB chunk is not binary")
        binary = source.read(binary_length)

    material = document["materials"][0]
    texture_index = material["pbrMetallicRoughness"]["baseColorTexture"]["index"]
    image_index = document["textures"][texture_index]["source"]
    image_definition = document["images"][image_index]
    view = document["bufferViews"][image_definition["bufferView"]]
    start = view.get("byteOffset", 0)
    end = start + view["byteLength"]
    return Image.open(io.BytesIO(binary[start:end])).convert("RGB")


def build_albedo(args: argparse.Namespace) -> None:
    if args.width <= 0 or args.width % 2:
        raise ValueError("--width must be a positive even number")
    if not 1 <= args.quality <= 100:
        raise ValueError("--quality must be between 1 and 100")

    output_height = args.width // 2
    base = extract_base_color(args.glb)
    base = base.resize((args.width, output_height), Image.Resampling.LANCZOS)
    base = ImageEnhance.Color(base).enhance(args.saturation)
    base = ImageEnhance.Contrast(base).enhance(args.contrast)

    with Image.open(args.normal) as normal_source:
        normal = normal_source.convert("RGB")
        if normal.size != base.size:
            normal = normal.resize(base.size, Image.Resampling.LANCZOS)

        args.output.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.NamedTemporaryFile(
            prefix="mars-orbital-albedo-",
            suffix=".rgb",
            delete=False,
        ) as temporary:
            temporary_path = Path(temporary.name)

        try:
            output = np.memmap(
                temporary_path,
                dtype=np.uint8,
                mode="w+",
                shape=(output_height, args.width, 3),
            )
            strip_height = 128
            for y_start in range(0, output_height, strip_height):
                y_end = min(y_start + strip_height, output_height)
                box = (0, y_start, args.width, y_end)
                color = np.asarray(base.crop(box), dtype=np.float32)
                normal_strip = np.asarray(normal.crop(box), dtype=np.float32)
                nx = normal_strip[:, :, 0] / 127.5 - 1.0
                ny = normal_strip[:, :, 1] / 127.5 - 1.0
                slope = np.sqrt(nx * nx + ny * ny)

                relief = nx * -0.55 + ny * -0.35
                modulation = (
                    1.0
                    + relief[:, :, None] * args.relief_strength
                    - slope[:, :, None] * args.slope_darkening
                )
                output[y_start:y_end] = np.clip(color * modulation, 0, 255).astype(
                    np.uint8
                )
                print(f"composited rows {y_end}/{output_height}", flush=True)

            output.flush()
            Image.fromarray(output, mode="RGB").save(
                args.output,
                format="JPEG",
                quality=args.quality,
                subsampling=0,
                optimize=True,
            )
            del output
        finally:
            temporary_path.unlink(missing_ok=True)

    print(f"wrote {args.output.resolve()}", flush=True)


def main() -> None:
    build_albedo(parse_args())


if __name__ == "__main__":
    main()
