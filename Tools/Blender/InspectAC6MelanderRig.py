import json
import os
import sys

import bpy


PART_ARMATURES = [
    "HD-011 MELANDER",
    "BD-011 MELANDER",
    "AR-011 MELANDER",
    "LG-011 MELANDER",
]


def pose_json_from_args():
    if "--" in sys.argv:
        index = sys.argv.index("--")
        if index + 1 < len(sys.argv):
            return sys.argv[index + 1]
    return None


def main():
    pose_json = pose_json_from_args()
    hkx_bones = []
    if pose_json and os.path.exists(pose_json):
        with open(pose_json, "r", encoding="utf-8") as handle:
            data = json.load(handle)
        hkx_bones = [bone["Name"] for bone in data["Bones"]]

    print("HKX_BONES", len(hkx_bones))
    for armature_name in PART_ARMATURES:
        armature = bpy.data.objects.get(armature_name)
        if armature is None:
            print("MISSING_ARMATURE", armature_name)
            continue

        names = [bone.name for bone in armature.data.bones]
        matching = [name for name in hkx_bones if name in names]
        missing = [name for name in hkx_bones if name not in names]
        extra = [name for name in names if name not in hkx_bones]

        print("ARMATURE", armature_name)
        print("  BONES", len(names))
        print("  MATCHING_HKX", len(matching))
        print("  MISSING_HKX", len(missing), missing[:80])
        print("  EXTRA", len(extra), extra[:40])

    union = set()
    for armature_name in PART_ARMATURES:
        armature = bpy.data.objects.get(armature_name)
        if armature is not None:
            union.update(bone.name for bone in armature.data.bones)

    print("UNION_BONES", len(union))
    print("UNION_MISSING_HKX", [name for name in hkx_bones if name not in union])


if __name__ == "__main__":
    main()
