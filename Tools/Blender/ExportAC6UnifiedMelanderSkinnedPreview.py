import json
import os
import sys

import bpy


SOURCE_COLLECTIONS = [
    "HD-011 MELANDER",
    "BD-011 MELANDER",
    "AR-011 MELANDER",
    "LG-011 MELANDER",
]

MASTER_ARMATURE = "BD-011 MELANDER"
EXPORT_ARMATURE_NAME = "AC6_UNIFIED_MELANDER_1010_RIG"


def export_root_from_args():
    if "--" in sys.argv:
        index = sys.argv.index("--")
        if index + 1 < len(sys.argv):
            return sys.argv[index + 1]
    return os.path.join(os.getcwd(), "AC6UnifiedMelanderSkinned")


def ensure_dir(path):
    os.makedirs(path, exist_ok=True)


def link_to_scene(obj, collection):
    if obj.name not in collection.objects.keys():
        collection.objects.link(obj)


def make_unified_armature(export_collection):
    source = bpy.data.objects.get(MASTER_ARMATURE)
    if source is None or source.type != "ARMATURE":
        raise RuntimeError(f"Master armature not found: {MASTER_ARMATURE}")

    unified_data = source.data.copy()
    unified_data.name = EXPORT_ARMATURE_NAME + "_Data"
    unified = bpy.data.objects.new(EXPORT_ARMATURE_NAME, unified_data)
    unified.matrix_world = source.matrix_world.copy()
    export_collection.objects.link(unified)
    bpy.context.view_layer.objects.active = unified
    unified.select_set(True)

    bpy.ops.object.mode_set(mode="EDIT")
    edit_bones = unified.data.edit_bones

    pending_parents = {}
    for source_name in SOURCE_COLLECTIONS:
        source_armature = bpy.data.objects.get(source_name)
        if source_armature is None or source_armature.type != "ARMATURE":
            raise RuntimeError(f"Source armature not found: {source_name}")

        for source_bone in source_armature.data.bones:
            if source_bone.name in edit_bones:
                continue

            new_bone = edit_bones.new(source_bone.name)
            try:
                new_bone.matrix = source_bone.matrix_local
            except Exception:
                new_bone.head = source_bone.head_local
                new_bone.tail = source_bone.tail_local
            new_bone.use_connect = False
            if source_bone.parent is not None:
                pending_parents[new_bone.name] = source_bone.parent.name

    changed = True
    while changed:
        changed = False
        for child_name, parent_name in list(pending_parents.items()):
            if child_name in edit_bones and parent_name in edit_bones:
                edit_bones[child_name].parent = edit_bones[parent_name]
                del pending_parents[child_name]
                changed = True

    bpy.ops.object.mode_set(mode="OBJECT")
    unified.select_set(False)
    return unified, pending_parents


def source_meshes():
    meshes = []
    seen = set()
    for collection_name in SOURCE_COLLECTIONS:
        collection = bpy.data.collections.get(collection_name)
        if collection is None:
            raise RuntimeError(f"Collection not found: {collection_name}")

        for obj in collection.objects:
            if obj.type != "MESH" or obj.name in seen:
                continue
            if not any(mod.type == "ARMATURE" for mod in obj.modifiers):
                continue

            seen.add(obj.name)
            meshes.append(obj)

    return meshes


def duplicate_mesh_for_unified_rig(source, unified, export_collection):
    duplicate = source.copy()
    duplicate.data = source.data.copy()
    duplicate.animation_data_clear()
    duplicate.name = source.name.replace("[", "_").replace("]", "") + "_UnifiedSkin"
    duplicate.data.name = duplicate.name + "_Mesh"

    export_collection.objects.link(duplicate)
    duplicate.matrix_world = source.matrix_world.copy()
    duplicate.parent = unified
    duplicate.matrix_parent_inverse = unified.matrix_world.inverted()

    for modifier in duplicate.modifiers:
        if modifier.type == "ARMATURE":
            modifier.object = unified

    duplicate.hide_viewport = False
    duplicate.hide_render = False
    duplicate.hide_select = False
    return duplicate


def export_fbx(objects, fbx_path):
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)

    armatures = [obj for obj in objects if obj.type == "ARMATURE"]
    if armatures:
        bpy.context.view_layer.objects.active = armatures[0]

    bpy.ops.export_scene.fbx(
        filepath=fbx_path,
        use_selection=True,
        object_types={"ARMATURE", "MESH"},
        apply_unit_scale=True,
        bake_space_transform=False,
        add_leaf_bones=False,
        path_mode="RELATIVE",
        embed_textures=False,
        use_armature_deform_only=False,
    )


def main():
    export_root = export_root_from_args()
    fbx_dir = os.path.join(export_root, "FBX")
    ensure_dir(fbx_dir)

    export_collection = bpy.data.collections.new(EXPORT_ARMATURE_NAME + "_Export")
    bpy.context.scene.collection.children.link(export_collection)

    unified, unresolved_parents = make_unified_armature(export_collection)
    meshes = [duplicate_mesh_for_unified_rig(obj, unified, export_collection) for obj in source_meshes()]

    fbx_path = os.path.join(fbx_dir, "AC6_UNIFIED_MELANDER_1010_SKINNED.fbx")
    export_fbx([unified] + meshes, fbx_path)

    manifest = {
        "name": "AC6 Unified Melander 1010 Skinned",
        "source_collections": SOURCE_COLLECTIONS,
        "fbx": fbx_path,
        "armature": unified.name,
        "bone_count": len(unified.data.bones),
        "mesh_count": len(meshes),
        "unresolved_parent_count": len(unresolved_parents),
        "unresolved_parents": unresolved_parents,
        "meshes": [mesh.name for mesh in meshes],
    }

    manifest_path = os.path.join(export_root, "AC6_Unified_Melander_1010_Skinned_manifest.json")
    with open(manifest_path, "w", encoding="utf-8") as handle:
        json.dump(manifest, handle, ensure_ascii=False, indent=2)

    print(f"Exported unified skinned Melander FBX: {fbx_path}")
    print(f"Armature bones: {len(unified.data.bones)}")
    print(f"Meshes: {len(meshes)}")
    print(f"Manifest: {manifest_path}")


if __name__ == "__main__":
    main()
