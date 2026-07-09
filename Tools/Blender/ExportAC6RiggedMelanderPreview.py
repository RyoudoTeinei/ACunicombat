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


def ensure_dir(path):
    os.makedirs(path, exist_ok=True)


def export_root_from_args():
    if "--" in sys.argv:
        index = sys.argv.index("--")
        if index + 1 < len(sys.argv):
            return sys.argv[index + 1]

    return os.path.join(os.getcwd(), "AC6RiggedMelander")


def collect_export_objects():
    objects = []
    seen = set()
    export_collection = bpy.data.collections.new("ACunicombat_Rigged_Melander_Export")
    bpy.context.scene.collection.children.link(export_collection)

    for collection_name in SOURCE_COLLECTIONS:
        collection = bpy.data.collections.get(collection_name)
        if collection is None:
            raise RuntimeError(f"Collection not found: {collection_name}")

        for obj in collection.objects:
            if obj.type not in {"ARMATURE", "MESH"}:
                continue

            if obj.name in seen:
                continue

            seen.add(obj.name)
            obj.hide_set(False)
            obj.hide_viewport = False
            obj.hide_render = False

            if obj.type == "ARMATURE":
                obj.data.pose_position = "REST"

            if obj.name not in export_collection.objects.keys():
                export_collection.objects.link(obj)

            objects.append(obj)

    return objects


def export_fbx(objects, fbx_path):
    bpy.ops.object.select_all(action="DESELECT")

    for obj in objects:
        obj.select_set(True)

    armatures = [obj for obj in objects if obj.type == "ARMATURE"]
    if armatures:
        bpy.context.view_layer.objects.active = armatures[0]
    elif objects:
        bpy.context.view_layer.objects.active = objects[0]

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

    objects = collect_export_objects()
    fbx_path = os.path.join(fbx_dir, "AC6_RIGGED_MELANDER_1010.fbx")
    export_fbx(objects, fbx_path)

    manifest = {
        "name": "AC6 Rigged Melander 1010",
        "source_collections": SOURCE_COLLECTIONS,
        "fbx": fbx_path,
        "armatures": [obj.name for obj in objects if obj.type == "ARMATURE"],
        "meshes": [obj.name for obj in objects if obj.type == "MESH"],
    }

    manifest_path = os.path.join(export_root, "AC6_Rigged_Melander_1010_manifest.json")
    with open(manifest_path, "w", encoding="utf-8") as handle:
        json.dump(manifest, handle, ensure_ascii=False, indent=2)

    print(f"Exported rigged Melander FBX: {fbx_path}")
    print(f"Manifest: {manifest_path}")


if __name__ == "__main__":
    main()
