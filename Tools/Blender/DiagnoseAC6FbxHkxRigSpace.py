import json
import math
import os
import sys

import bpy
from mathutils import Matrix, Quaternion


def args_after_double_dash():
    if "--" not in sys.argv:
        return []
    return sys.argv[sys.argv.index("--") + 1 :]


def pose_matrix(transform):
    t = transform["Translation"]
    r = transform["Rotation"]
    translation = Matrix.Translation((t["X"], t["Y"], t["Z"]))
    rotation = Quaternion((r["W"], r["X"], r["Y"], r["Z"])).to_matrix().to_4x4()
    return translation @ rotation


def decompose_error(delta):
    pos_error = delta.to_translation().length
    rot = delta.to_quaternion()
    rot_error = math.degrees(rot.angle)
    if rot_error > 180.0:
        rot_error = 360.0 - rot_error
    return pos_error, rot_error


def read_pose(path):
    with open(path, "r", encoding="utf-8") as handle:
        data = json.load(handle)
    return {
        bone["Name"]: {
            "index": bone["Index"],
            "parent_index": bone["ParentIndex"],
            "matrix": pose_matrix(bone["ReferenceLocalTransform"]),
        }
        for bone in data["Bones"]
    }


def local_matrix_from_blender_bone(armature, bone_name):
    bone = armature.data.bones.get(bone_name)
    if bone is None:
        return None
    if bone.parent is None:
        return bone.matrix_local.copy()
    return bone.parent.matrix_local.inverted() @ bone.matrix_local


def main():
    args = args_after_double_dash()
    if len(args) < 3:
        raise RuntimeError("Usage: blender --background --python DiagnoseAC6FbxHkxRigSpace.py -- <fbx_path> <pose_json> <report_json>")

    fbx_path = args[0]
    pose_json = args[1]
    report_json = args[2]

    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete()
    bpy.ops.import_scene.fbx(filepath=fbx_path)

    hkx = read_pose(pose_json)
    armatures = [obj for obj in bpy.context.scene.objects if obj.type == "ARMATURE"]
    if not armatures:
        raise RuntimeError("No armature imported from FBX")

    armature = armatures[0]
    rows = []
    missing = []
    for name, expected in hkx.items():
        actual = local_matrix_from_blender_bone(armature, name)
        if actual is None:
            missing.append(name)
            continue
        delta = expected["matrix"].inverted() @ actual
        pos_error, rot_error = decompose_error(delta)
        rows.append(
            {
                "bone": name,
                "position_error": pos_error,
                "rotation_error_degrees": rot_error,
            }
        )

    rows.sort(key=lambda row: (row["rotation_error_degrees"], row["position_error"]), reverse=True)
    report = {
        "fbx": fbx_path,
        "pose_json": pose_json,
        "armature": armature.name,
        "object_transform": {
            "location": list(armature.location),
            "rotation_euler_degrees": [math.degrees(v) for v in armature.rotation_euler],
            "scale": list(armature.scale),
        },
        "checked_bones": len(rows),
        "missing_bones": missing,
        "rotation_error_over_1deg": sum(1 for row in rows if row["rotation_error_degrees"] > 1.0),
        "position_error_over_0_001": sum(1 for row in rows if row["position_error"] > 0.001),
        "worst_bones": rows[:30],
        "mesh_count": len([obj for obj in bpy.context.scene.objects if obj.type == "MESH"]),
    }

    os.makedirs(os.path.dirname(report_json), exist_ok=True)
    with open(report_json, "w", encoding="utf-8") as handle:
        json.dump(report, handle, ensure_ascii=False, indent=2)

    print(f"Wrote FBX rig-space diagnostic report: {report_json}")
    print(
        f"{armature.name}: checked {report['checked_bones']}, "
        f"missing {len(missing)}, "
        f"rot>1deg {report['rotation_error_over_1deg']}, "
        f"pos>0.001 {report['position_error_over_0_001']}"
    )
    print(f"Armature object transform: {report['object_transform']}")


if __name__ == "__main__":
    main()
