#!/usr/bin/env python3
"""Extract native SpaceEngine Mars cube faces for Unity runtime sampling."""

from __future__ import annotations

import argparse
import json
import zipfile
from pathlib import Path

from PIL import Image


FACES = ("pos_x", "neg_x", "pos_y", "neg_y", "pos_z", "neg_z")
SOURCE_TILE_SIZE = 258
CONTENT_TILE_SIZE = 256


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("surface_pak", type=Path)
    parser.add_argument("clouds_pak", type=Path)
    parser.add_argument("output_root", type=Path)
    parser.add_argument("--surface-quality", type=int, default=96)
    return parser.parse_args()


def extract_layer(
    archive: zipfile.ZipFile,
    archive_root: str,
    level: int,
    tiles_per_face: int,
    mode: str,
    output_root: Path,
    output_format: str,
    quality: int = 96,
) -> list[Path]:
    face_size = tiles_per_face * CONTENT_TILE_SIZE
    output_root.mkdir(parents=True, exist_ok=True)
    outputs: list[Path] = []

    for face_index, face_name in enumerate(FACES, start=1):
        face = Image.new(mode, (face_size, face_size))
        for row in range(tiles_per_face):
            destination_row = tiles_per_face - 1 - row
            y = destination_row * CONTENT_TILE_SIZE
            for column in range(tiles_per_face):
                entry = (
                    f"{archive_root}/{face_name}/"
                    f"{level}_{row}_{column}.png"
                )
                with archive.open(entry) as source:
                    with Image.open(source) as image:
                        if image.size != (SOURCE_TILE_SIZE, SOURCE_TILE_SIZE):
                            raise ValueError(
                                f"Unexpected tile size {image.size}: {entry}"
                            )
                        tile = image.convert(mode).crop((1, 1, 257, 257))
                        tile = tile.transpose(Image.Transpose.FLIP_TOP_BOTTOM)
                face.paste(tile, (column * CONTENT_TILE_SIZE, y))

        suffix = ".jpg" if output_format == "JPEG" else ".png"
        output = output_root / f"{face_name}{suffix}"
        save_options = (
            {"quality": quality, "subsampling": 0, "optimize": True}
            if output_format == "JPEG"
            else {"compress_level": 6}
        )
        face.save(output, format=output_format, **save_options)
        outputs.append(output)
        print(
            f"wrote {face_index}/6 {output} ({face_size}x{face_size})",
            flush=True,
        )

    return outputs


def main() -> None:
    args = parse_args()
    if not 1 <= args.surface_quality <= 100:
        raise ValueError("--surface-quality must be between 1 and 100")

    with zipfile.ZipFile(args.surface_pak) as surface_archive:
        surface_outputs = extract_layer(
            surface_archive,
            "textures/planets/Mars/Surface-PBC",
            5,
            32,
            "RGB",
            args.output_root / "SurfaceCube32k",
            "JPEG",
            args.surface_quality,
        )

    cloud_outputs: dict[str, list[Path]] = {}
    with zipfile.ZipFile(args.clouds_pak) as cloud_archive:
        for layer_name, archive_root in (
            ("Dust", "textures/planets/Mars/Clouds-dust-HD"),
            ("WaterIce", "textures/planets/Mars/Clouds-water-HD"),
        ):
            cloud_outputs[layer_name] = extract_layer(
                cloud_archive,
                archive_root,
                1,
                2,
                "L",
                args.output_root / f"Clouds{layer_name}Cube2k",
                "PNG",
            )

    manifest = {
        "surface_source": str(args.surface_pak.resolve()),
        "clouds_source": str(args.clouds_pak.resolve()),
        "surface_faces": [str(path.resolve()) for path in surface_outputs],
        "cloud_faces": {
            name: [str(path.resolve()) for path in paths]
            for name, paths in cloud_outputs.items()
        },
    }
    manifest_path = args.output_root / "SpaceEngineMarsLayers.json"
    manifest_path.write_text(
        json.dumps(manifest, indent=2, ensure_ascii=False),
        encoding="utf-8",
    )
    print(f"wrote {manifest_path}", flush=True)


if __name__ == "__main__":
    main()
