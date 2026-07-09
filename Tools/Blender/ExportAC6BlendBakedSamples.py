import json
import os
import re
import sys

import bpy


SAMPLES = [
    {
        "part_id": "AC6_BLEND_HD_M_1010",
        "display_name": "AC6 Blend Melander Head",
        "slot": "Head",
        "collection": "HD-011 MELANDER",
    },
    {
        "part_id": "AC6_BLEND_BD_M_1010",
        "display_name": "AC6 Blend Melander Core",
        "slot": "Core",
        "collection": "BD-011 MELANDER",
    },
    {
        "part_id": "AC6_BLEND_AM_M_1010",
        "display_name": "AC6 Blend Melander Arms",
        "slot": "Arms",
        "collection": "AR-011 MELANDER",
    },
    {
        "part_id": "AC6_BLEND_LG_M_1010",
        "display_name": "AC6 Blend Melander Legs",
        "slot": "Legs",
        "collection": "LG-011 MELANDER",
    },
]


def sanitize(value):
    value = re.sub(r"[^A-Za-z0-9_\-]+", "_", value)
    value = value.strip("_")
    return value[:96] if value else "Asset"


def ensure_dir(path):
    os.makedirs(path, exist_ok=True)


def select_only(obj):
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj


def material_name(obj):
    names = []
    for slot in obj.material_slots:
        if slot.material is not None:
            names.append(slot.material.name)
    return " | ".join(names) if names else "NoMaterial"


def prepare_bake_target(obj, image):
    for slot in obj.material_slots:
        material = slot.material
        if material is None:
            continue

        material.use_nodes = True
        force_emission_color_bake(material)
        node_tree = material.node_tree
        target = node_tree.nodes.new("ShaderNodeTexImage")
        target.name = "ACunicombat_Bake_Target"
        target.image = image
        node_tree.nodes.active = target


def force_emission_color_bake(material):
    node_tree = material.node_tree
    nodes = node_tree.nodes
    links = node_tree.links

    output = next((node for node in nodes if node.type == "OUTPUT_MATERIAL"), None)
    if output is None:
        output = nodes.new("ShaderNodeOutputMaterial")

    color_socket = find_material_color_socket(material)
    if color_socket is None:
        color_node = nodes.new("ShaderNodeRGB")
        color_node.outputs[0].default_value = material.diffuse_color
        color_socket = color_node.outputs[0]

    emission = nodes.new("ShaderNodeEmission")
    emission.name = "ACunicombat_Color_Bake_Emission"
    emission.inputs["Strength"].default_value = 1.0
    links.new(color_socket, emission.inputs["Color"])

    for link in list(output.inputs["Surface"].links):
        links.remove(link)
    links.new(emission.outputs[0], output.inputs["Surface"])


def find_material_color_socket(material):
    node_tree = material.node_tree
    nodes = node_tree.nodes

    preferred_group = nodes.get("Group")
    if preferred_group is not None and "Color" in preferred_group.outputs:
        return preferred_group.outputs["Color"]

    for node in nodes:
        if node.type == "GROUP" and "Color" in node.outputs:
            return node.outputs["Color"]

    for node in nodes:
        if node.type == "BSDF_PRINCIPLED" and "Base Color" in node.inputs:
            base_color = node.inputs["Base Color"]
            if base_color.is_linked:
                return base_color.links[0].from_socket

            color_node = nodes.new("ShaderNodeRGB")
            color_node.outputs[0].default_value = base_color.default_value
            return color_node.outputs[0]

    return None


def create_baked_material(name, image_path):
    image = bpy.data.images.load(image_path, check_existing=True)
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

    return material


def bake_object_color(obj, texture_path, resolution):
    if len(obj.data.uv_layers) == 0:
        raise RuntimeError(f"Object has no UVs: {obj.name}")

    obj.data.uv_layers.active_index = 0
    image = bpy.data.images.new(
        name=sanitize(os.path.splitext(os.path.basename(texture_path))[0]),
        width=resolution,
        height=resolution,
        alpha=False,
        float_buffer=False,
    )
    image.colorspace_settings.name = "sRGB"

    prepare_bake_target(obj, image)
    select_only(obj)
    bpy.ops.object.bake(type="EMIT", margin=8, use_clear=True)

    image.filepath_raw = texture_path
    image.file_format = "PNG"
    image.save()


def duplicate_with_baked_material(obj, baked_material):
    duplicate = obj.copy()
    duplicate.data = obj.data.copy()
    duplicate.animation_data_clear()
    duplicate.hide_render = False
    duplicate.hide_viewport = False
    duplicate.hide_select = False
    duplicate.data.materials.clear()
    duplicate.data.materials.append(baked_material)
    for polygon in duplicate.data.polygons:
        polygon.material_index = 0
    duplicate.name = sanitize(obj.name) + "_Baked"
    duplicate.data.name = duplicate.name + "_Mesh"
    return duplicate


def duplicate_for_bake(obj, collection):
    duplicate = obj.copy()
    duplicate.data = obj.data.copy()
    duplicate.animation_data_clear()
    duplicate.hide_render = False
    duplicate.hide_viewport = False
    duplicate.hide_select = False

    for index, material in enumerate(list(duplicate.data.materials)):
        if material is not None:
            duplicate.data.materials[index] = material.copy()

    duplicate.name = sanitize(obj.name) + "_BakeSource"
    duplicate.data.name = duplicate.name + "_Mesh"
    collection.objects.link(duplicate)
    return duplicate


def collection_meshes(collection_name):
    collection = bpy.data.collections.get(collection_name)
    if collection is None:
        raise RuntimeError(f"Collection not found: {collection_name}")

    return [obj for obj in collection.objects if obj.type == "MESH"]


def export_part(sample, output_root, resolution):
    textures_dir = os.path.join(output_root, "Textures")
    fbx_dir = os.path.join(output_root, "FBX")
    ensure_dir(textures_dir)
    ensure_dir(fbx_dir)

    export_collection = bpy.data.collections.new(sample["part_id"] + "_Export")
    bpy.context.scene.collection.children.link(export_collection)
    bake_collection = bpy.data.collections.new(sample["part_id"] + "_Bake")
    bpy.context.scene.collection.children.link(bake_collection)

    baked_objects = []
    object_reports = []
    source_objects = collection_meshes(sample["collection"])
    for index, obj in enumerate(source_objects):
        bake_source = duplicate_for_bake(obj, bake_collection)
        safe_object_name = sanitize(f"{sample['part_id']}_{index:02d}_{obj.name}")
        texture_path = os.path.join(textures_dir, safe_object_name + "_Color.png")
        bake_object_color(bake_source, texture_path, resolution)

        baked_material = create_baked_material(safe_object_name + "_Mat", texture_path)
        duplicate = duplicate_with_baked_material(bake_source, baked_material)
        export_collection.objects.link(duplicate)
        baked_objects.append(duplicate)
        object_reports.append(
            {
                "source_object": obj.name,
                "material": material_name(obj),
                "texture": texture_path,
                "vertices": len(obj.data.vertices),
                "polygons": len(obj.data.polygons),
            }
        )

    fbx_path = os.path.join(fbx_dir, sample["part_id"] + ".fbx")
    bpy.ops.object.select_all(action="DESELECT")
    for obj in baked_objects:
        obj.select_set(True)
    if baked_objects:
        bpy.context.view_layer.objects.active = baked_objects[0]

    bpy.ops.export_scene.fbx(
        filepath=fbx_path,
        use_selection=True,
        object_types={"MESH"},
        apply_unit_scale=True,
        bake_space_transform=False,
        add_leaf_bones=False,
        path_mode="RELATIVE",
        embed_textures=False,
    )

    return {
        **sample,
        "fbx": fbx_path,
        "objects": object_reports,
    }


def configure_scene():
    bpy.context.scene.render.engine = "CYCLES"
    bpy.context.scene.cycles.samples = 16
    bpy.context.scene.cycles.use_denoising = False
    bpy.context.scene.view_settings.view_transform = "Standard"
    bpy.context.scene.view_settings.look = "None"
    bpy.context.scene.view_settings.exposure = 0
    bpy.context.scene.view_settings.gamma = 1


def main():
    args = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []
    output_root = args[0] if args else os.path.join(os.getcwd(), "AC6_BlendBaked", "MELANDER_1010")
    resolution = int(args[1]) if len(args) > 1 else 1024

    ensure_dir(output_root)
    configure_scene()

    reports = []
    for sample in SAMPLES:
        print(f"Exporting {sample['part_id']} from {sample['collection']}")
        reports.append(export_part(sample, output_root, resolution))

    manifest_path = os.path.join(output_root, "AC6_BlendBaked_MELANDER_1010_manifest.json")
    with open(manifest_path, "w", encoding="utf-8") as handle:
        json.dump(
            {
                "blend_file": bpy.data.filepath,
                "resolution": resolution,
                "samples": reports,
            },
            handle,
            ensure_ascii=False,
            indent=2,
        )

    print(f"AC6 baked blend samples exported: {output_root}")


if __name__ == "__main__":
    main()
