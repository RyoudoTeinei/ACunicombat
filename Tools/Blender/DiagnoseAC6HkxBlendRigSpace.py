import json
import math
import os
import sys

import bpy
from mathutils import Matrix, Quaternion, Vector


PART_ARMATURES = [
    "HD-011 MELANDER",
    "BD-011 MELANDER",
    "AR-011 MELANDER",
    "LG-011 MELANDER",
]

KEY_BONES = [
    "Root",
    "Pelvis",
    "BD_Root",
    "Spine",
    "Head",
    "L_Clavicle",
    "L_Shoulder",
    "L_UpperArm",
    "L_Forearm",
    "L_Hand",
    "R_Clavicle",
    "R_Shoulder",
    "R_UpperArm",
    "R_Forearm",
    "R_Hand",
    "L_Coxa",
    "L_Thigh",
    "L_Calf",
    "L_Foot",
    "R_Coxa",
    "R_Thigh",
    "R_Calf",
    "R_Foot",
]


def args_after_double_dash():
    if "--" not in sys.argv:
        return []
    return sys.argv[sys.argv.index("--") + 1 :]


def matrix_from_pose_transform(transform):
    t = transform["Translation"]
    r = transform["Rotation"]
    s = transform["Scale"]
    translation = Matrix.Translation((t["X"], t["Y"], t["Z"]))
    rotation = Quaternion((r["W"], r["X"], r["Y"], r["Z"])).to_matrix().to_4x4()
    scale = Matrix.Diagonal((s["X"], s["Y"], s["Z"], 1.0))
    return translation @ rotation @ scale


def decompose_delta(delta):
    translation = delta.to_translation()
    rotation = delta.to_quaternion()
    angle = math.degrees(rotation.angle)
    if angle > 180.0:
        angle = 360.0 - angle
    scale = delta.to_scale()
    scale_error = max(abs(scale.x - 1.0), abs(scale.y - 1.0), abs(scale.z - 1.0))
    return translation.length, angle, scale_error


def blender_local_matrix(armature, bone_name):
    bone = armature.data.bones.get(bone_name)
    if bone is None:
        return None
    if bone.parent is None:
        return bone.matrix_local.copy()
    return bone.parent.matrix_local.inverted() @ bone.matrix_local


def read_hkx_pose(path):
    with open(path, "r", encoding="utf-8") as handle:
        data = json.load(handle)
    bones = {}
    for bone in data["Bones"]:
        bones[bone["Name"]] = {
            "index": bone["Index"],
            "parent_index": bone["ParentIndex"],
            "parent_name": None,
            "matrix": matrix_from_pose_transform(bone["ReferenceLocalTransform"]),
        }

    index_to_name = {bone["Index"]: bone["Name"] for bone in data["Bones"]}
    for bone in data["Bones"]:
        parent_index = bone["ParentIndex"]
        if parent_index >= 0:
            bones[bone["Name"]]["parent_name"] = index_to_name.get(parent_index)

    return bones


def summarize_armature(armature, hkx_bones):
    rows = []
    parent_mismatches = []
    for name, hkx in hkx_bones.items():
        blender = blender_local_matrix(armature, name)
        if blender is None:
            continue

        delta = hkx["matrix"].inverted() @ blender
        pos_error, rot_error, scale_error = decompose_delta(delta)
        rows.append(
            {
                "bone": name,
                "position_error": pos_error,
                "rotation_error_degrees": rot_error,
                "scale_error": scale_error,
            }
        )

        bone = armature.data.bones.get(name)
        blender_parent = bone.parent.name if bone is not None and bone.parent is not None else None
        if blender_parent != hkx["parent_name"]:
            parent_mismatches.append(
                {
                    "bone": name,
                    "hkx_parent": hkx["parent_name"],
                    "blend_parent": blender_parent,
                }
            )

    rows.sort(key=lambda row: (row["rotation_error_degrees"], row["position_error"]), reverse=True)
    matched = len(rows)
    over_5_degrees = sum(1 for row in rows if row["rotation_error_degrees"] > 5.0)
    over_45_degrees = sum(1 for row in rows if row["rotation_error_degrees"] > 45.0)
    over_01_units = sum(1 for row in rows if row["position_error"] > 0.1)

    return {
        "armature": armature.name,
        "matched_bones": matched,
        "total_hkx_bones": len(hkx_bones),
        "missing_hkx_bones": [name for name in hkx_bones if armature.data.bones.get(name) is None],
        "parent_mismatch_count": len(parent_mismatches),
        "parent_mismatches_top": parent_mismatches[:24],
        "rotation_error_over_5deg": over_5_degrees,
        "rotation_error_over_45deg": over_45_degrees,
        "position_error_over_0_1": over_01_units,
        "worst_bones": rows[:30],
        "key_bones": [row for row in rows if row["bone"] in KEY_BONES],
        "object_transform": {
            "location": list(armature.location),
            "rotation_euler_degrees": [math.degrees(v) for v in armature.rotation_euler],
            "scale": list(armature.scale),
        },
    }


def main():
    args = args_after_double_dash()
    if len(args) < 2:
        raise RuntimeError("Usage: blender <blend> --background --python DiagnoseAC6HkxBlendRigSpace.py -- <pose_json> <report_json>")

    pose_json = args[0]
    report_json = args[1]
    hkx_bones = read_hkx_pose(pose_json)

    reports = []
    for armature_name in PART_ARMATURES:
        armature = bpy.data.objects.get(armature_name)
        if armature is None or armature.type != "ARMATURE":
            reports.append({"armature": armature_name, "missing": True})
            continue
        reports.append(summarize_armature(armature, hkx_bones))

    os.makedirs(os.path.dirname(report_json), exist_ok=True)
    with open(report_json, "w", encoding="utf-8") as handle:
        json.dump({"pose_json": pose_json, "reports": reports}, handle, ensure_ascii=False, indent=2)

    print(f"Wrote rig-space diagnostic report: {report_json}")
    for report in reports:
        if report.get("missing"):
            print(f"{report['armature']}: missing")
            continue
        print(
            f"{report['armature']}: matched {report['matched_bones']}/{report['total_hkx_bones']}, "
            f"parent mismatches {report['parent_mismatch_count']}, "
            f"rot>45deg {report['rotation_error_over_45deg']}, "
            f"pos>0.1 {report['position_error_over_0_1']}"
        )


if __name__ == "__main__":
    main()
