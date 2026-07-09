import json
import os
import sys

import bpy


def material_info(material):
    info = {
        "name": material.name,
        "diffuse_color": [float(v) for v in material.diffuse_color],
        "use_nodes": bool(material.use_nodes),
        "nodes": [],
    }

    if material.use_nodes and material.node_tree is not None:
        for node in material.node_tree.nodes:
            node_info = {
                "name": node.name,
                "type": node.type,
                "bl_idname": node.bl_idname,
            }
            if node.type == "TEX_IMAGE" and node.image is not None:
                node_info["image"] = {
                    "name": node.image.name,
                    "filepath": bpy.path.abspath(node.image.filepath) if node.image.filepath else "",
                    "colorspace": node.image.colorspace_settings.name if node.image.colorspace_settings else "",
                }
            info["nodes"].append(node_info)

    return info


def object_info(obj):
    return {
        "name": obj.name,
        "type": obj.type,
        "mesh": obj.data.name if obj.data is not None else "",
        "materials": [slot.material.name if slot.material else "" for slot in obj.material_slots],
        "vertex_count": len(obj.data.vertices) if obj.data is not None else 0,
        "polygon_count": len(obj.data.polygons) if obj.data is not None else 0,
        "uv_layers": [layer.name for layer in obj.data.uv_layers] if obj.data is not None else [],
        "parent": obj.parent.name if obj.parent is not None else "",
        "has_animation_data": obj.animation_data is not None and obj.animation_data.action is not None,
    }


def armature_info(obj):
    bones = obj.data.bones if obj.data is not None else []
    return {
        "name": obj.name,
        "bone_count": len(bones),
        "bones_sample": [bone.name for bone in bones[:80]],
        "has_animation_data": obj.animation_data is not None and obj.animation_data.action is not None,
        "action": obj.animation_data.action.name if obj.animation_data is not None and obj.animation_data.action is not None else "",
    }


def action_info(action):
    fcurves = getattr(action, "fcurves", None)
    layers = getattr(action, "layers", None)
    slots = getattr(action, "slots", None)

    return {
        "name": action.name,
        "frame_range": [float(action.frame_range[0]), float(action.frame_range[1])],
        "fcurves": len(fcurves) if fcurves is not None else 0,
        "layers": len(layers) if layers is not None else 0,
        "slots": len(slots) if slots is not None else 0,
    }


def main():
    args = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []
    if len(args) < 2:
        raise RuntimeError("Usage: InspectAC6RawFbx.py -- <fbx_path> <output_json>")

    fbx_path = args[0]
    output_path = args[1]

    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete()
    bpy.ops.import_scene.fbx(filepath=fbx_path)

    report = {
        "fbx": fbx_path,
        "objects": [object_info(obj) for obj in bpy.context.scene.objects if obj.type == "MESH"],
        "armatures": [armature_info(obj) for obj in bpy.context.scene.objects if obj.type == "ARMATURE"],
        "actions": [action_info(action) for action in bpy.data.actions],
        "materials": [material_info(material) for material in bpy.data.materials],
    }

    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    with open(output_path, "w", encoding="utf-8") as handle:
        json.dump(report, handle, ensure_ascii=False, indent=2)

    print(f"FBX report written: {output_path}")
    print(json.dumps({
        "objects": len(report["objects"]),
        "armatures": len(report["armatures"]),
        "actions": len(report["actions"]),
        "materials": len(report["materials"]),
    }, ensure_ascii=False))


if __name__ == "__main__":
    main()
