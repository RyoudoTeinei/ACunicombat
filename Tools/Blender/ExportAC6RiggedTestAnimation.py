import json
import math
import os
import sys

import bpy


SOURCE_FBX = r"E:\UnityDev\Resources\AC6_ExtractedAssets\chr\ALLMIND - c4201\c4201.fbx"
DEFAULT_OUTPUT_ROOT = r"E:\UnityDev\ACunicombat\Assets\_Project\ImportedFromExtracted\Animations"
OUTPUT_NAME = "AC6_C4201_TestAction"
ACTION_NAME = "AC6_C4201_Test_MechReady"


def clear_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete()


def find_armature():
    armatures = [obj for obj in bpy.context.scene.objects if obj.type == "ARMATURE"]
    if not armatures:
        raise RuntimeError("Imported FBX has no armature.")
    return armatures[0]


def set_pose_rotations(armature, frame, degrees_by_bone):
    bpy.context.scene.frame_set(frame)

    for bone in armature.pose.bones:
        bone.rotation_mode = "XYZ"
        bone.rotation_euler = (0.0, 0.0, 0.0)

    for bone_name, euler_degrees in degrees_by_bone.items():
        bone = armature.pose.bones.get(bone_name)
        if bone is None:
            continue

        bone.rotation_mode = "XYZ"
        bone.rotation_euler = tuple(math.radians(v) for v in euler_degrees)

    for bone_name in degrees_by_bone:
        bone = armature.pose.bones.get(bone_name)
        if bone is not None:
            bone.keyframe_insert(data_path="rotation_euler", frame=frame)

    armature.keyframe_insert(data_path="location", frame=frame)
    armature.keyframe_insert(data_path="rotation_euler", frame=frame)


def create_test_action(armature):
    bpy.context.scene.frame_start = 1
    bpy.context.scene.frame_end = 72
    bpy.context.scene.render.fps = 30

    armature.animation_data_create()
    action = bpy.data.actions.new(ACTION_NAME)
    action.use_fake_user = True
    armature.animation_data.action = action

    keyed_bones = {
        "Body_Control": (0.0, 0.0, 0.0),
        "Body_RXY": (0.0, 0.0, 0.0),
        "head": (0.0, 0.0, 0.0),
        "R_Shoulder_Control": (0.0, 0.0, 0.0),
        "L_Shoulder_Control": (0.0, 0.0, 0.0),
        "R_Arm_Control": (0.0, 0.0, 0.0),
        "L_Arm_Control": (0.0, 0.0, 0.0),
        "R_arm_01": (0.0, 0.0, 0.0),
        "R_arm_02": (0.0, 0.0, 0.0),
        "R_hand": (0.0, 0.0, 0.0),
        "L_arm_01": (0.0, 0.0, 0.0),
        "L_arm_02": (0.0, 0.0, 0.0),
        "L_hand": (0.0, 0.0, 0.0),
        "R_leg_01": (0.0, 0.0, 0.0),
        "L_leg_01": (0.0, 0.0, 0.0),
    }

    set_pose_rotations(armature, 1, keyed_bones)
    armature.location = (0.0, 0.0, 0.0)
    armature.rotation_euler = (0.0, 0.0, 0.0)

    mid_pose = dict(keyed_bones)
    mid_pose.update(
        {
            "Body_Control": (-2.5, 0.0, 0.0),
            "Body_RXY": (0.0, 2.0, 0.0),
            "head": (3.0, -3.0, 0.0),
            "R_Shoulder_Control": (0.0, -4.0, -6.0),
            "L_Shoulder_Control": (0.0, 4.0, 6.0),
            "R_Arm_Control": (-10.0, -8.0, -4.0),
            "L_Arm_Control": (-8.0, 7.0, 4.0),
            "R_arm_01": (-12.0, -6.0, -6.0),
            "R_arm_02": (-18.0, 0.0, 0.0),
            "R_hand": (0.0, 0.0, -6.0),
            "L_arm_01": (-8.0, 6.0, 6.0),
            "L_arm_02": (-14.0, 0.0, 0.0),
            "L_hand": (0.0, 0.0, 6.0),
            "R_leg_01": (3.0, -2.0, 0.0),
            "L_leg_01": (-2.0, 2.0, 0.0),
        }
    )
    armature.location = (0.0, 0.04, 0.0)
    set_pose_rotations(armature, 36, mid_pose)

    armature.location = (0.0, 0.0, 0.0)
    set_pose_rotations(armature, 72, keyed_bones)

    return action


def export_fbx(output_root):
    os.makedirs(output_root, exist_ok=True)
    output_fbx = os.path.join(output_root, OUTPUT_NAME + ".fbx")
    manifest_path = os.path.join(output_root, OUTPUT_NAME + "_manifest.json")

    bpy.ops.export_scene.fbx(
        filepath=output_fbx,
        use_selection=False,
        add_leaf_bones=False,
        bake_anim=True,
        bake_anim_use_all_bones=True,
        bake_anim_use_nla_strips=False,
        bake_anim_use_all_actions=True,
        bake_anim_force_startend_keying=True,
        bake_anim_step=1.0,
        bake_anim_simplify_factor=0.0,
        path_mode="AUTO",
    )

    armature = find_armature()
    manifest = {
        "source_fbx": SOURCE_FBX,
        "output_fbx": output_fbx,
        "action": ACTION_NAME,
        "frame_start": 1,
        "frame_end": 72,
        "fps": 30,
        "armature": armature.name,
        "bone_count": len(armature.data.bones),
        "note": "Synthetic test action generated to validate Unity's rigged FBX animation import path. It is not an extracted AC6 HKX action.",
    }
    with open(manifest_path, "w", encoding="utf-8") as handle:
        json.dump(manifest, handle, ensure_ascii=False, indent=2)

    print(f"Exported rigged test animation: {output_fbx}")
    print(json.dumps(manifest, ensure_ascii=False))


def main():
    args = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []
    output_root = args[0] if args else DEFAULT_OUTPUT_ROOT

    if not os.path.exists(SOURCE_FBX):
        raise RuntimeError(f"Source FBX not found: {SOURCE_FBX}")

    clear_scene()
    bpy.ops.import_scene.fbx(filepath=SOURCE_FBX)
    armature = find_armature()
    create_test_action(armature)
    export_fbx(output_root)


if __name__ == "__main__":
    main()
