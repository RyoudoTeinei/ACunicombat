import json
import os
import re
import sys

import bpy


SOURCE_ROOT = "E:/UnityDev/Resources/AC6_ExtractedAssets"

WEAPON_SAMPLES = [
    {
        "part_id": "AC6_RAW_WP_0010_Left_Rifle",
        "display_name": "AC6 Baked WP-0010 Rifle",
        "slot": "LeftHandWeapon",
        "source": "wp/wp_0010/WP_L_0010.fbx",
    },
    {
        "part_id": "AC6_RAW_WP_0230_Left_Rifle",
        "display_name": "AC6 Baked WP-0230 Rifle",
        "slot": "LeftHandWeapon",
        "source": "wp/wp_0230/WP_L_0230.fbx",
    },
    {
        "part_id": "AC6_RAW_WP_0250_Left_Heavy_Rifle",
        "display_name": "AC6 Baked WP-0250 Heavy Rifle",
        "slot": "LeftHandWeapon",
        "source": "wp/wp_0250/WP_L_0250.fbx",
    },
    {
        "part_id": "AC6_RAW_WP_1310_Right_Back",
        "display_name": "AC6 Baked WP-1310 Right Back Weapon",
        "slot": "RightBackWeapon",
        "source": "wp/wp_1310/WP_R_1310.fbx",
    },
    {
        "part_id": "AC6_RAW_WP_1410_Left_Back",
        "display_name": "AC6 Baked WP-1410 Left Back Weapon",
        "slot": "LeftBackWeapon",
        "source": "wp/wp_1410/WP_L_1410.fbx",
    },
    {
        "part_id": "AC6_RAW_WP_1410_Right_Back",
        "display_name": "AC6 Baked WP-1410 Right Back Weapon",
        "slot": "RightBackWeapon",
        "source": "wp/wp_1410/WP_R_1410.fbx",
    },
]


def sanitize(value):
    value = re.sub(r"[^A-Za-z0-9_\-]+", "_", value)
    value = value.strip("_")
    return value[:96] if value else "Asset"


def ensure_dir(path):
    os.makedirs(path, exist_ok=True)


def reset_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete()
    for material in list(bpy.data.materials):
        bpy.data.materials.remove(material)
    for image in list(bpy.data.images):
        bpy.data.images.remove(image)


def select_only(obj):
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj


def texture_stems_from_material(material_name, fallback_stem):
    stems = []
    for match in re.finditer(r"WP_\d{4}(?:_\d{2})?", material_name, re.IGNORECASE):
        stem = match.group(0).upper()
        if stem not in stems:
            stems.append(stem)
    if fallback_stem and fallback_stem not in stems:
        stems.append(fallback_stem)
    return stems


def find_texture(source_dir, stems, suffixes):
    for stem in stems:
        for suffix in suffixes:
            path = os.path.join(source_dir, stem + suffix + ".dds")
            if os.path.exists(path):
                return path
    return None


def load_image(path, colorspace):
    if not path or not os.path.exists(path):
        return None
    image = bpy.data.images.load(path, check_existing=True)
    if image.colorspace_settings is not None:
        image.colorspace_settings.name = colorspace
    return image


def remove_links_to_input(node_tree, socket):
    for link in list(socket.links):
        node_tree.links.remove(link)


def configure_material(material, source_dir, fallback_stem):
    material.use_nodes = True
    node_tree = material.node_tree
    nodes = node_tree.nodes
    links = node_tree.links

    for node in list(nodes):
        nodes.remove(node)

    output = nodes.new("ShaderNodeOutputMaterial")
    output.location = (460, 0)
    emission = nodes.new("ShaderNodeEmission")
    emission.location = (230, 0)
    emission.inputs["Strength"].default_value = 1.0
    links.new(emission.outputs[0], output.inputs["Surface"])

    material_name = material.name
    if is_emissive_material(material_name):
        color = nodes.new("ShaderNodeRGB")
        color.outputs[0].default_value = (0.95, 0.12, 1.0, 1.0)
        links.new(color.outputs[0], emission.inputs["Color"])
        return

    stems = texture_stems_from_material(material_name, fallback_stem)
    base_path = find_texture(source_dir, stems, ["_a", "_01_a"])
    mask_path = find_texture(source_dir, stems, ["_3m", "_01_3m", "_m", "_01_m"])
    base_image = load_image(base_path, "sRGB")
    mask_image = load_image(mask_path, "Linear Rec.709")

    base_color = nodes.new("ShaderNodeRGB")
    base_color.location = (-760, 80)
    base_color.outputs[0].default_value = (0.48, 0.46, 0.42, 1.0)

    if base_image is not None:
        texture = nodes.new("ShaderNodeTexImage")
        texture.location = (-760, 280)
        texture.image = base_image
        color_source = texture.outputs["Color"]
    else:
        color_source = base_color.outputs[0]

    if mask_image is not None:
        mask_texture = nodes.new("ShaderNodeTexImage")
        mask_texture.location = (-760, -140)
        mask_texture.image = mask_image
        separate = nodes.new("ShaderNodeSeparateColor")
        separate.location = (-540, -140)
        ramp = nodes.new("ShaderNodeValToRGB")
        ramp.location = (-320, -120)
        ramp.color_ramp.interpolation = "CONSTANT"
        while len(ramp.color_ramp.elements) < 4:
            ramp.color_ramp.elements.new(0.5)
        elements = ramp.color_ramp.elements
        elements[0].position = 0.00
        elements[0].color = (0.18, 0.17, 0.16, 1.0)
        elements[1].position = 0.10
        elements[1].color = (0.60, 0.58, 0.52, 1.0)
        elements[2].position = 0.28
        elements[2].color = (0.34, 0.36, 0.36, 1.0)
        elements[3].position = 0.58
        elements[3].color = (0.08, 0.10, 0.12, 1.0)

        mix = nodes.new("ShaderNodeMix")
        mix.location = (-60, 90)
        mix.data_type = "RGBA"
        mix.factor_mode = "UNIFORM"
        mix.inputs["Factor"].default_value = 0.42

        links.new(mask_texture.outputs["Color"], separate.inputs["Color"])
        links.new(separate.outputs["Red"], ramp.inputs["Fac"])
        links.new(color_source, mix.inputs["A"])
        links.new(ramp.outputs["Color"], mix.inputs["B"])
        links.new(mix.outputs[2], emission.inputs["Color"])
    else:
        links.new(color_source, emission.inputs["Color"])


def is_emissive_material(material_name):
    lower = material_name.lower()
    return "emissive" in lower or "eyecolor" in lower or "eye" in lower


def material_names(obj):
    return [slot.material.name if slot.material else "" for slot in obj.material_slots]


def prepare_bake_target(obj, image):
    for slot in obj.material_slots:
        material = slot.material
        if material is None:
            continue
        material.use_nodes = True
        node_tree = material.node_tree
        target = node_tree.nodes.new("ShaderNodeTexImage")
        target.name = "ACunicombat_Weapon_Bake_Target"
        target.image = image
        node_tree.nodes.active = target


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


def create_baked_material(name, image_path, emissive=False):
    image = bpy.data.images.load(image_path, check_existing=True)
    image.colorspace_settings.name = "sRGB"

    material = bpy.data.materials.new(name)
    material.use_nodes = True
    nodes = material.node_tree.nodes
    for node in list(nodes):
        nodes.remove(node)

    output = nodes.new("ShaderNodeOutputMaterial")
    output.location = (420, 0)
    shader = nodes.new("ShaderNodeEmission" if emissive else "ShaderNodeBsdfPrincipled")
    shader.location = (160, 0)
    texture = nodes.new("ShaderNodeTexImage")
    texture.location = (-120, 0)
    texture.image = image

    links = material.node_tree.links
    if emissive:
        links.new(texture.outputs["Color"], shader.inputs["Color"])
        shader.inputs["Strength"].default_value = 1.5
        links.new(shader.outputs["Emission"], output.inputs["Surface"])
    else:
        links.new(texture.outputs["Color"], shader.inputs["Base Color"])
        if "Roughness" in shader.inputs:
            shader.inputs["Roughness"].default_value = 0.40
        if "Metallic" in shader.inputs:
            shader.inputs["Metallic"].default_value = 0.38
        links.new(shader.outputs["BSDF"], output.inputs["Surface"])

    return material


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


def configure_scene():
    bpy.context.scene.render.engine = "CYCLES"
    bpy.context.scene.cycles.samples = 8
    bpy.context.scene.cycles.use_denoising = False
    bpy.context.scene.view_settings.view_transform = "Standard"
    bpy.context.scene.view_settings.look = "None"
    bpy.context.scene.view_settings.exposure = 0
    bpy.context.scene.view_settings.gamma = 1


def export_weapon(sample, output_root, resolution):
    reset_scene()
    configure_scene()

    source_path = os.path.join(SOURCE_ROOT, sample["source"].replace("/", os.sep))
    source_dir = os.path.dirname(source_path)
    source_stem = os.path.splitext(os.path.basename(source_path))[0]
    fallback_stem = source_stem.replace("WP_L_", "WP_").replace("WP_R_", "WP_").replace("WP_A_", "WP_")

    print(f"Importing {source_path}")
    bpy.ops.import_scene.fbx(filepath=source_path)
    meshes = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
    if not meshes:
        raise RuntimeError(f"No mesh objects imported from {source_path}")

    for material in bpy.data.materials:
        configure_material(material, source_dir, fallback_stem)

    textures_dir = os.path.join(output_root, "Textures")
    fbx_dir = os.path.join(output_root, "FBX")
    ensure_dir(textures_dir)
    ensure_dir(fbx_dir)

    export_collection = bpy.data.collections.new(sample["part_id"] + "_Export")
    bpy.context.scene.collection.children.link(export_collection)

    baked_objects = []
    object_reports = []
    for index, obj in enumerate(meshes):
        source_material_names = material_names(obj)
        source_material = " | ".join(source_material_names)
        safe_object_name = sanitize(f"{sample['part_id']}_{index:02d}_{obj.name}")
        texture_path = os.path.join(textures_dir, safe_object_name + "_Color.png")
        bake_object_color(obj, texture_path, resolution)

        baked_material = create_baked_material(
            safe_object_name + "_Mat",
            texture_path,
            emissive=any(is_emissive_material(name) for name in source_material_names),
        )
        duplicate = duplicate_with_baked_material(obj, baked_material)
        export_collection.objects.link(duplicate)
        baked_objects.append(duplicate)
        object_reports.append(
            {
                "source_object": obj.name,
                "material": source_material,
                "texture": texture_path,
                "vertices": len(obj.data.vertices),
                "polygons": len(obj.data.polygons),
            }
        )

    fbx_path = os.path.join(fbx_dir, sample["part_id"] + ".fbx")
    bpy.ops.object.select_all(action="DESELECT")
    for obj in baked_objects:
        obj.select_set(True)
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
        "source_fbx": source_path,
        "fbx": fbx_path,
        "objects": object_reports,
    }


def main():
    args = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []
    output_root = args[0] if args else os.path.join(os.getcwd(), "AC6_ExtractedWeaponBakes")
    resolution = int(args[1]) if len(args) > 1 else 1024

    ensure_dir(output_root)
    reports = []
    for sample in WEAPON_SAMPLES:
        print(f"Exporting {sample['part_id']}")
        reports.append(export_weapon(sample, output_root, resolution))

    manifest_path = os.path.join(output_root, "AC6_ExtractedWeaponBakes_manifest.json")
    with open(manifest_path, "w", encoding="utf-8") as handle:
        json.dump(
            {
                "source_root": SOURCE_ROOT,
                "resolution": resolution,
                "samples": reports,
            },
            handle,
            ensure_ascii=False,
            indent=2,
        )

    print(f"AC6 extracted weapon bakes exported: {output_root}")


if __name__ == "__main__":
    main()
