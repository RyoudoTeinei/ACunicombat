import json
import os
import sys

import bpy


TARGET_TOKENS = (
    "1010",
    "2010",
    "3010",
    "HD_M",
    "BD_M",
    "AM_M",
    "LG_M",
    "BS_A",
    "ACPARTS",
    "CORE",
    "LIMITER",
    "EMISSIVE",
    "MARKING",
    "AC_01",
    "AC_02",
    "AC_03",
)


def contains_target(value):
    if not value:
        return False
    upper = value.upper()
    return any(token in upper for token in TARGET_TOKENS)


def safe_default_value(socket):
    if not hasattr(socket, "default_value"):
        return None

    value = socket.default_value
    try:
        if hasattr(value, "__len__") and not isinstance(value, str):
            return [float(v) for v in value]
        return float(value)
    except Exception:
        return str(value)


def image_info(image):
    if image is None:
        return None

    packed = image.packed_file is not None
    try:
        size = [int(image.size[0]), int(image.size[1])]
    except Exception:
        size = [0, 0]

    return {
        "name": image.name,
        "filepath": bpy.path.abspath(image.filepath) if image.filepath else "",
        "source": image.source,
        "colorspace": image.colorspace_settings.name if image.colorspace_settings else "",
        "size": size,
        "packed": packed,
        "packed_size": int(image.packed_file.size) if packed else 0,
    }


def node_info(node):
    info = {
        "name": node.name,
        "label": node.label,
        "type": node.type,
        "bl_idname": node.bl_idname,
    }

    if node.type == "TEX_IMAGE":
        info["image"] = image_info(node.image)

    inputs = {}
    for socket in node.inputs:
        value = safe_default_value(socket)
        if value is not None:
            inputs[socket.name] = value
    if inputs:
        info["inputs"] = inputs

    outputs = [socket.name for socket in node.outputs]
    if outputs:
        info["outputs"] = outputs

    return info


def material_info(material):
    info = {
        "name": material.name,
        "diffuse_color": [float(v) for v in material.diffuse_color],
        "use_nodes": bool(material.use_nodes),
        "node_count": 0,
        "nodes": [],
        "links": [],
    }

    if not material.use_nodes or material.node_tree is None:
        return info

    nodes = list(material.node_tree.nodes)
    info["node_count"] = len(nodes)
    info["nodes"] = [node_info(node) for node in nodes]
    for link in material.node_tree.links:
        info["links"].append(
            {
                "from_node": link.from_node.name,
                "from_socket": link.from_socket.name,
                "to_node": link.to_node.name,
                "to_socket": link.to_socket.name,
            }
        )

    return info


def object_info(obj):
    mesh = obj.data
    materials = []
    for slot in obj.material_slots:
        materials.append(slot.material.name if slot.material else "")

    return {
        "name": obj.name,
        "mesh": mesh.name if mesh else "",
        "materials": materials,
        "uv_layers": [layer.name for layer in mesh.uv_layers] if mesh else [],
        "color_attributes": [attr.name for attr in mesh.color_attributes] if mesh else [],
        "attributes": [attr.name for attr in mesh.attributes] if mesh else [],
        "vertex_count": len(mesh.vertices) if mesh else 0,
        "polygon_count": len(mesh.polygons) if mesh else 0,
    }


def main():
    if "--" in sys.argv:
        args = sys.argv[sys.argv.index("--") + 1 :]
    else:
        args = []

    output_path = args[0] if args else os.path.join(os.getcwd(), "AC6BlendMaterialReport.json")

    all_materials = list(bpy.data.materials)
    all_images = list(bpy.data.images)
    all_objects = [obj for obj in bpy.data.objects if obj.type == "MESH"]

    relevant_materials = []
    relevant_material_names = set()
    for material in all_materials:
        if contains_target(material.name):
            relevant_materials.append(material)
            relevant_material_names.add(material.name)

    relevant_objects = []
    for obj in all_objects:
        if contains_target(obj.name) or contains_target(obj.data.name):
            relevant_objects.append(obj)
            for slot in obj.material_slots:
                if slot.material is not None:
                    relevant_material_names.add(slot.material.name)

    for material in all_materials:
        if material.name in relevant_material_names and material not in relevant_materials:
            relevant_materials.append(material)

    relevant_images = []
    seen_images = set()
    for material in relevant_materials:
        if material.use_nodes and material.node_tree:
            for node in material.node_tree.nodes:
                if node.type == "TEX_IMAGE" and node.image is not None and node.image.name not in seen_images:
                    relevant_images.append(node.image)
                    seen_images.add(node.image.name)

    for image in all_images:
        if contains_target(image.name) or contains_target(image.filepath):
            if image.name not in seen_images:
                relevant_images.append(image)
                seen_images.add(image.name)

    report = {
        "blend_file": bpy.data.filepath,
        "counts": {
            "materials": len(all_materials),
            "images": len(all_images),
            "mesh_objects": len(all_objects),
            "relevant_materials": len(relevant_materials),
            "relevant_images": len(relevant_images),
            "relevant_objects": len(relevant_objects),
        },
        "material_names_sample": [material.name for material in all_materials[:200]],
        "image_names_sample": [image.name for image in all_images[:200]],
        "relevant_materials": [material_info(material) for material in relevant_materials],
        "relevant_images": [image_info(image) for image in relevant_images],
        "relevant_objects": [object_info(obj) for obj in relevant_objects[:300]],
    }

    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    with open(output_path, "w", encoding="utf-8") as handle:
        json.dump(report, handle, ensure_ascii=False, indent=2)

    print(f"AC6 blend material report written: {output_path}")
    print(json.dumps(report["counts"], ensure_ascii=False))


if __name__ == "__main__":
    main()
