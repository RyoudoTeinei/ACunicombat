import json
import os
import re
import sys

import bpy
from mathutils import Vector


PART_PATTERN = re.compile(r"(HD|BD|AM|LG|BS)_M_(\d{4})(?:_(left|right))?", re.IGNORECASE)


def material_names(obj):
    names = []
    for slot in obj.material_slots:
        if slot.material is not None:
            names.append(slot.material.name)
    return names


def part_tokens_from_materials(materials):
    tokens = []
    for material in materials:
        for match in PART_PATTERN.finditer(material):
            prefix = match.group(1).upper()
            code = match.group(2)
            side = match.group(3)
            token = f"{prefix}_M_{code}"
            if side:
                token = f"{token}_{side.lower()}"
            if token not in tokens:
                tokens.append(token)
    return tokens


def object_bounds(obj):
    corners = [obj.matrix_world @ Vector(corner) for corner in obj.bound_box]
    minimum = [min(corner[i] for corner in corners) for i in range(3)]
    maximum = [max(corner[i] for corner in corners) for i in range(3)]
    return {
        "min": [float(v) for v in minimum],
        "max": [float(v) for v in maximum],
        "size": [float(maximum[i] - minimum[i]) for i in range(3)],
    }


def object_info(obj):
    materials = material_names(obj)
    mesh = obj.data
    return {
        "name": obj.name,
        "mesh": mesh.name if mesh is not None else "",
        "collections": [collection.name for collection in obj.users_collection],
        "materials": materials,
        "part_tokens": part_tokens_from_materials(materials),
        "vertex_count": len(mesh.vertices) if mesh is not None else 0,
        "polygon_count": len(mesh.polygons) if mesh is not None else 0,
        "uv_layers": [layer.name for layer in mesh.uv_layers] if mesh is not None else [],
        "color_attributes": [attr.name for attr in mesh.color_attributes] if mesh is not None else [],
        "bounds": object_bounds(obj),
    }


def main():
    args = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []
    output_path = args[0] if args else os.path.join(os.getcwd(), "AC6BlendObjectCatalog.json")

    objects = [obj for obj in bpy.data.objects if obj.type == "MESH"]
    infos = [object_info(obj) for obj in objects]
    by_part = {}
    for info in infos:
        for token in info["part_tokens"]:
            by_part.setdefault(token, []).append(info["name"])

    report = {
        "blend_file": bpy.data.filepath,
        "object_count": len(objects),
        "part_tokens": {key: by_part[key] for key in sorted(by_part.keys())},
        "objects": infos,
        "collections": [
            {
                "name": collection.name,
                "object_count": len(collection.objects),
            }
            for collection in bpy.data.collections
        ],
    }

    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    with open(output_path, "w", encoding="utf-8") as handle:
        json.dump(report, handle, ensure_ascii=False, indent=2)

    print(f"AC6 blend object catalog written: {output_path}")
    print(json.dumps({"objects": len(objects), "parts": len(by_part)}, ensure_ascii=False))


if __name__ == "__main__":
    main()
