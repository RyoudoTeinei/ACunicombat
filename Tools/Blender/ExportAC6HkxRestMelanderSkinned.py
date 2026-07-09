import json
import math
import os
import re
import sys

import bpy
from mathutils import Matrix, Quaternion, Vector


SOURCE_COLLECTIONS = [
    "HD-011 MELANDER",
    "BD-011 MELANDER",
    "AR-011 MELANDER",
    "LG-011 MELANDER",
]

EXPORT_ARMATURE_NAME = "AC6_HKX_REST_MELANDER_1010_RIG"
EXPORT_FBX_NAME = "AC6_HKX_MELANDER_1010_SKINNED.fbx"
BAKED_MANIFEST_NAME = "AC6_BlendBaked_MELANDER_1010_manifest.json"


def args_after_double_dash():
    if "--" not in sys.argv:
        return []
    return sys.argv[sys.argv.index("--") + 1 :]


def ensure_dir(path):
    os.makedirs(path, exist_ok=True)


def sanitize(value):
    value = re.sub(r"[^A-Za-z0-9_\-]+", "_", value)
    value = value.strip("_")
    return value[:96] if value else "Asset"


def configure_scene():
    bpy.context.scene.view_settings.view_transform = "Standard"
    bpy.context.scene.view_settings.look = "None"
    bpy.context.scene.view_settings.exposure = 0
    bpy.context.scene.view_settings.gamma = 1


def infer_baked_manifest_path(output_root):
    imported_from_blend_root = os.path.dirname(os.path.abspath(output_root))
    return os.path.join(imported_from_blend_root, "MELANDER_1010", BAKED_MANIFEST_NAME)


def load_baked_texture_lookup(output_root, explicit_manifest_path=None):
    manifest_path = explicit_manifest_path or infer_baked_manifest_path(output_root)
    if not os.path.exists(manifest_path):
        print(f"Warning: baked material manifest not found: {manifest_path}")
        return {}, manifest_path

    with open(manifest_path, "r", encoding="utf-8") as handle:
        manifest = json.load(handle)

    lookup = {}
    for sample in manifest.get("samples", []):
        for obj in sample.get("objects", []):
            source_name = obj.get("source_object")
            texture_path = obj.get("texture")
            if not source_name or not texture_path:
                continue
            lookup[source_name] = {
                "part_id": sample.get("part_id", ""),
                "texture": texture_path,
                "material": obj.get("material", ""),
            }

    return lookup, manifest_path


def create_baked_texture_material(name, texture_path, source_material_name):
    image = bpy.data.images.load(texture_path, check_existing=True)
    image.colorspace_settings.name = "sRGB"

    material = bpy.data.materials.new(name)
    material.use_nodes = True
    nodes = material.node_tree.nodes
    for node in list(nodes):
        nodes.remove(node)

    output = nodes.new("ShaderNodeOutputMaterial")
    output.location = (420, 0)
    principled = nodes.new("ShaderNodeBsdfPrincipled")
    principled.location = (160, 0)
    texture = nodes.new("ShaderNodeTexImage")
    texture.location = (-120, 0)
    texture.image = image

    links = material.node_tree.links
    links.new(texture.outputs["Color"], principled.inputs["Base Color"])
    links.new(principled.outputs["BSDF"], output.inputs["Surface"])

    if "Roughness" in principled.inputs:
        principled.inputs["Roughness"].default_value = 0.42
    if "Metallic" in principled.inputs:
        principled.inputs["Metallic"].default_value = 0.45

    is_emissive = (
        "emission" in source_material_name.lower()
        or "emissive" in source_material_name.lower()
        or "limiter" in source_material_name.lower()
    )
    if is_emissive:
        if "Emission Color" in principled.inputs:
            links.new(texture.outputs["Color"], principled.inputs["Emission Color"])
        elif "Emission" in principled.inputs:
            links.new(texture.outputs["Color"], principled.inputs["Emission"])
        if "Emission Strength" in principled.inputs:
            principled.inputs["Emission Strength"].default_value = 1.5

    return material


def apply_baked_material(duplicate, source, baked_texture_lookup):
    report = {
        "source_mesh": source.name,
        "applied": False,
        "texture": "",
        "material": "",
        "reason": "",
    }

    entry = baked_texture_lookup.get(source.name)
    if entry is None:
        report["reason"] = "source mesh not present in baked manifest"
        return report

    texture_path = entry.get("texture", "")
    if not os.path.exists(texture_path):
        report["reason"] = f"baked texture not found: {texture_path}"
        return report

    material_name = sanitize(source.name) + "_HkxBaked_Mat"
    material = create_baked_texture_material(material_name, texture_path, entry.get("material", ""))

    duplicate.data.materials.clear()
    duplicate.data.materials.append(material)
    for polygon in duplicate.data.polygons:
        polygon.material_index = 0
    if len(duplicate.data.uv_layers) > 0:
        duplicate.data.uv_layers.active_index = 0

    report["applied"] = True
    report["texture"] = texture_path
    report["material"] = material.name
    return report


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

    bones = data["Bones"]
    index_to_bone = {bone["Index"]: bone for bone in bones}
    for bone in bones:
        parent_index = bone["ParentIndex"]
        bone["ParentName"] = index_to_bone[parent_index]["Name"] if parent_index >= 0 else None
        bone["LocalMatrix"] = pose_matrix(bone["ReferenceLocalTransform"])

    globals_by_name = {}
    unresolved = set(bone["Name"] for bone in bones)
    while unresolved:
        progressed = False
        for bone in bones:
            name = bone["Name"]
            if name not in unresolved:
                continue
            parent = bone["ParentName"]
            if parent is not None and parent not in globals_by_name:
                continue
            parent_matrix = globals_by_name[parent] if parent is not None else Matrix.Identity(4)
            globals_by_name[name] = parent_matrix @ bone["LocalMatrix"]
            unresolved.remove(name)
            progressed = True

        if not progressed:
            raise RuntimeError(f"Could not resolve HKX bone hierarchy: {sorted(unresolved)[:20]}")

    return bones, globals_by_name


def create_hkx_armature(bones, global_matrices, collection):
    armature_data = bpy.data.armatures.new(EXPORT_ARMATURE_NAME + "_Data")
    armature = bpy.data.objects.new(EXPORT_ARMATURE_NAME, armature_data)
    collection.objects.link(armature)
    bpy.context.view_layer.objects.active = armature
    armature.select_set(True)

    bpy.ops.object.mode_set(mode="EDIT")
    edit_bones = armature.data.edit_bones
    for bone in bones:
        name = bone["Name"]
        edit_bone = edit_bones.new(name)
        matrix = global_matrices[name].copy()
        edit_bone.head = matrix @ Vector((0.0, 0.0, 0.0))
        edit_bone.tail = matrix @ Vector((0.0, 0.18, 0.0))
        edit_bone.matrix = matrix
        edit_bone.length = max(edit_bone.length, 0.08)
        edit_bone.use_connect = False

    for bone in bones:
        parent_name = bone["ParentName"]
        if parent_name is None:
            continue
        edit_bones[bone["Name"]].parent = edit_bones[parent_name]
        edit_bones[bone["Name"]].use_connect = False

    bpy.ops.object.mode_set(mode="OBJECT")
    armature.select_set(False)
    return armature


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
            if not obj.vertex_groups:
                continue
            seen.add(obj.name)
            meshes.append(obj)

    return meshes


def source_armature_for_mesh(source):
    for modifier in source.modifiers:
        if modifier.type == "ARMATURE" and modifier.object is not None:
            return modifier.object
    return None


def bone_position_world(armature, bone_name):
    bone = armature.data.bones.get(bone_name)
    if bone is None:
        return None
    return armature.matrix_world @ bone.matrix_local.to_translation()


def vertex_group_centroid_world(source, group_name):
    group = source.vertex_groups.get(group_name)
    if group is None:
        return None

    total_weight = 0.0
    centroid = None
    for vertex in source.data.vertices:
        weight = 0.0
        for assignment in vertex.groups:
            if assignment.group == group.index:
                weight = assignment.weight
                break
        if weight <= 0.0:
            continue

        position = source.matrix_world @ vertex.co
        centroid = position * weight if centroid is None else centroid + position * weight
        total_weight += weight

    if centroid is None or total_weight <= 0.0:
        return None
    return centroid / total_weight


def build_group_remap(source, hkx_bone_names):
    source_armature = source_armature_for_mesh(source)
    if source_armature is None:
        return {}

    candidates = []
    for bone in source_armature.data.bones:
        if bone.name not in hkx_bone_names:
            continue
        candidates.append((bone.name, source_armature.matrix_world @ bone.matrix_local.to_translation()))

    if not candidates:
        return {}

    remap = {}
    for group in source.vertex_groups:
        if group.name in hkx_bone_names:
            continue

        source_position = bone_position_world(source_armature, group.name)
        if source_position is None:
            source_position = vertex_group_centroid_world(source, group.name)
        if source_position is None:
            continue

        nearest_name = min(candidates, key=lambda item: (item[1] - source_position).length)[0]
        remap[group.name] = nearest_name

    return remap


def transfer_non_hkx_weights(duplicate, group_remap, hkx_bone_names):
    transferred_groups = []
    for source_group_name, target_group_name in group_remap.items():
        source_group = duplicate.vertex_groups.get(source_group_name)
        if source_group is None:
            continue
        target_group = duplicate.vertex_groups.get(target_group_name)
        if target_group is None:
            target_group = duplicate.vertex_groups.new(name=target_group_name)

        source_index = source_group.index
        transferred_vertices = 0
        weight_sum = 0.0
        for vertex in duplicate.data.vertices:
            for assignment in vertex.groups:
                if assignment.group != source_index:
                    continue
                target_group.add([vertex.index], assignment.weight, "ADD")
                transferred_vertices += 1
                weight_sum += assignment.weight
                break

        transferred_groups.append(
            {
                "source": source_group_name,
                "target": target_group_name,
                "vertices": transferred_vertices,
                "weight_sum": weight_sum,
            }
        )

    for group in list(duplicate.vertex_groups):
        if group.name not in hkx_bone_names:
            duplicate.vertex_groups.remove(group)

    return transferred_groups


def vertex_has_hkx_weight(vertex, hkx_group_indices):
    for assignment in vertex.groups:
        if assignment.group in hkx_group_indices and assignment.weight > 0.0:
            return True
    return False


def assign_unweighted_vertices_to_nearest_bone(duplicate, armature, global_matrices, hkx_bone_names):
    hkx_group_indices = {group.index for group in duplicate.vertex_groups if group.name in hkx_bone_names}
    bone_positions = [
        (bone_name, armature.matrix_world @ global_matrices[bone_name].to_translation())
        for bone_name in hkx_bone_names
        if bone_name in global_matrices
    ]

    assigned = {}
    for vertex in duplicate.data.vertices:
        if vertex_has_hkx_weight(vertex, hkx_group_indices):
            continue

        vertex_world = duplicate.matrix_world @ vertex.co
        nearest_name = min(bone_positions, key=lambda item: (item[1] - vertex_world).length)[0]
        group = duplicate.vertex_groups.get(nearest_name)
        if group is None:
            group = duplicate.vertex_groups.new(name=nearest_name)
            hkx_group_indices.add(group.index)
        group.add([vertex.index], 1.0, "REPLACE")
        assigned[nearest_name] = assigned.get(nearest_name, 0) + 1

    return assigned


def count_vertices_without_hkx_weight(duplicate, hkx_bone_names):
    hkx_group_indices = {group.index for group in duplicate.vertex_groups if group.name in hkx_bone_names}
    return sum(1 for vertex in duplicate.data.vertices if not vertex_has_hkx_weight(vertex, hkx_group_indices))


def duplicate_mesh_for_hkx_armature(source, armature, export_collection, hkx_bone_names, global_matrices, baked_texture_lookup):
    duplicate = source.copy()
    duplicate.data = source.data.copy()
    duplicate.animation_data_clear()
    duplicate.name = source.name.replace("[", "_").replace("]", "") + "_HkxSkin"
    duplicate.data.name = duplicate.name + "_Mesh"
    export_collection.objects.link(duplicate)
    material_report = apply_baked_material(duplicate, source, baked_texture_lookup)

    duplicate.matrix_world = source.matrix_world.copy()
    duplicate.parent = armature
    duplicate.matrix_parent_inverse = armature.matrix_world.inverted()

    group_remap = build_group_remap(source, hkx_bone_names)
    transferred_groups = transfer_non_hkx_weights(duplicate, group_remap, hkx_bone_names)
    unweighted_before = count_vertices_without_hkx_weight(duplicate, hkx_bone_names)
    nearest_assignments = assign_unweighted_vertices_to_nearest_bone(
        duplicate,
        armature,
        global_matrices,
        hkx_bone_names,
    )
    unweighted_after = count_vertices_without_hkx_weight(duplicate, hkx_bone_names)

    for modifier in list(duplicate.modifiers):
        duplicate.modifiers.remove(modifier)

    modifier = duplicate.modifiers.new("HKX Rest Armature", "ARMATURE")
    modifier.object = armature
    modifier.use_vertex_groups = True

    return duplicate, {
        "source_mesh": source.name,
        "transferred_groups": transferred_groups,
        "unweighted_before_nearest_assignment": unweighted_before,
        "nearest_bone_assignments": nearest_assignments,
        "unweighted_after_nearest_assignment": unweighted_after,
        "remaining_non_hkx_groups": [group.name for group in duplicate.vertex_groups if group.name not in hkx_bone_names],
        "baked_material": material_report,
    }


def local_matrix_from_blender_bone(armature, bone_name):
    bone = armature.data.bones.get(bone_name)
    if bone is None:
        return None
    if bone.parent is None:
        return bone.matrix_local.copy()
    return bone.parent.matrix_local.inverted() @ bone.matrix_local


def validate_created_armature(armature, bones):
    rows = []
    for bone in bones:
        local = local_matrix_from_blender_bone(armature, bone["Name"])
        if local is None:
            continue
        delta = bone["LocalMatrix"].inverted() @ local
        pos_error, rot_error = decompose_error(delta)
        rows.append(
            {
                "bone": bone["Name"],
                "position_error": pos_error,
                "rotation_error_degrees": rot_error,
            }
        )

    rows.sort(key=lambda row: (row["rotation_error_degrees"], row["position_error"]), reverse=True)
    return {
        "checked_bones": len(rows),
        "rotation_error_over_1deg": sum(1 for row in rows if row["rotation_error_degrees"] > 1.0),
        "position_error_over_0_001": sum(1 for row in rows if row["position_error"] > 0.001),
        "worst_bones": rows[:30],
    }


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
    args = args_after_double_dash()
    if len(args) < 2:
        raise RuntimeError(
            "Usage: blender <blend> --background --python ExportAC6HkxRestMelanderSkinned.py -- "
            "<pose_json> <output_root> [baked_manifest]"
        )

    pose_json = args[0]
    output_root = args[1]
    baked_manifest_arg = args[2] if len(args) > 2 else None
    fbx_dir = os.path.join(output_root, "FBX")
    ensure_dir(fbx_dir)
    configure_scene()

    bones, global_matrices = read_pose(pose_json)
    hkx_bone_names = {bone["Name"] for bone in bones}
    baked_texture_lookup, baked_manifest_path = load_baked_texture_lookup(output_root, baked_manifest_arg)

    export_collection = bpy.data.collections.new(EXPORT_ARMATURE_NAME + "_Export")
    bpy.context.scene.collection.children.link(export_collection)

    armature = create_hkx_armature(bones, global_matrices, export_collection)
    validation = validate_created_armature(armature, bones)

    meshes = []
    weight_remap_report = {}
    for source in source_meshes():
        duplicate, remap_report = duplicate_mesh_for_hkx_armature(
            source,
            armature,
            export_collection,
            hkx_bone_names,
            global_matrices,
            baked_texture_lookup,
        )
        meshes.append(duplicate)
        weight_remap_report[duplicate.name] = remap_report

    fbx_path = os.path.join(fbx_dir, EXPORT_FBX_NAME)
    export_fbx([armature] + meshes, fbx_path)

    manifest = {
        "name": "AC6 HKX Rest Melander 1010 Skinned",
        "pose_json": pose_json,
        "fbx": fbx_path,
        "armature": armature.name,
        "bone_count": len(armature.data.bones),
        "mesh_count": len(meshes),
        "armature_validation": validation,
        "weight_remap_by_mesh": weight_remap_report,
        "baked_material_manifest": baked_manifest_path,
        "baked_materials_applied": sum(
            1
            for report in weight_remap_report.values()
            if report.get("baked_material", {}).get("applied")
        ),
        "meshes": [mesh.name for mesh in meshes],
    }

    manifest_path = os.path.join(output_root, "AC6_HKX_Melander_1010_Skinned_manifest.json")
    with open(manifest_path, "w", encoding="utf-8") as handle:
        json.dump(manifest, handle, ensure_ascii=False, indent=2)

    print(f"Exported HKX-rest Melander FBX: {fbx_path}")
    print(f"Armature bones: {len(armature.data.bones)}")
    print(f"Meshes: {len(meshes)}")
    print(f"Baked materials applied: {manifest['baked_materials_applied']}/{len(meshes)}")
    print(
        "Validation: "
        f"rot>1deg={validation['rotation_error_over_1deg']}, "
        f"pos>0.001={validation['position_error_over_0_001']}"
    )
    print(f"Manifest: {manifest_path}")


if __name__ == "__main__":
    main()
