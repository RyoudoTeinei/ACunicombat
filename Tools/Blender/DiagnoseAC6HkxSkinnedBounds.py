import json
import os
import sys

import bpy
from mathutils import Vector


def args_after_double_dash():
    if "--" not in sys.argv:
        return []
    return sys.argv[sys.argv.index("--") + 1 :]


def object_world_bounds(obj):
    corners = [obj.matrix_world @ Vector(corner) for corner in obj.bound_box]
    mins = Vector((min(c.x for c in corners), min(c.y for c in corners), min(c.z for c in corners)))
    maxs = Vector((max(c.x for c in corners), max(c.y for c in corners), max(c.z for c in corners)))
    return mins, maxs


def combined_mesh_bounds(objects):
    mins = None
    maxs = None
    for obj in objects:
        if obj.type != "MESH":
            continue
        obj_min, obj_max = object_world_bounds(obj)
        if mins is None:
            mins = obj_min.copy()
            maxs = obj_max.copy()
        else:
            mins.x = min(mins.x, obj_min.x)
            mins.y = min(mins.y, obj_min.y)
            mins.z = min(mins.z, obj_min.z)
            maxs.x = max(maxs.x, obj_max.x)
            maxs.y = max(maxs.y, obj_max.y)
            maxs.z = max(maxs.z, obj_max.z)

    if mins is None:
        return None

    size = maxs - mins
    return {
        "min": [mins.x, mins.y, mins.z],
        "max": [maxs.x, maxs.y, maxs.z],
        "size": [size.x, size.y, size.z],
    }


def main():
    args = args_after_double_dash()
    if len(args) < 2:
        raise RuntimeError("Usage: blender --background --python DiagnoseAC6HkxSkinnedBounds.py -- <fbx> <out_json>")

    fbx_path = args[0]
    out_json = args[1]

    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete()
    bpy.ops.import_scene.fbx(filepath=fbx_path)

    objects = list(bpy.context.scene.objects)
    armatures = [obj for obj in objects if obj.type == "ARMATURE"]
    meshes = [obj for obj in objects if obj.type == "MESH"]

    report = {
        "fbx": fbx_path,
        "armatures": [
            {
                "name": obj.name,
                "location": list(obj.location),
                "rotation_euler_degrees": [axis * 57.29577951308232 for axis in obj.rotation_euler],
                "scale": list(obj.scale),
            }
            for obj in armatures
        ],
        "mesh_count": len(meshes),
        "combined_mesh_bounds": combined_mesh_bounds(meshes),
        "mesh_names": [obj.name for obj in meshes],
    }

    os.makedirs(os.path.dirname(out_json), exist_ok=True)
    with open(out_json, "w", encoding="utf-8") as handle:
        json.dump(report, handle, indent=2)

    print(json.dumps(report["combined_mesh_bounds"], indent=2))


if __name__ == "__main__":
    main()
