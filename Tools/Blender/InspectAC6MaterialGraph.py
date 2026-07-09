import json
import os
import sys

import bpy


def default_value(socket):
    if not hasattr(socket, "default_value"):
        return None

    value = socket.default_value
    try:
        if hasattr(value, "__len__") and not isinstance(value, str):
            return [float(v) for v in value]
        return float(value)
    except Exception:
        return str(value)


def color_ramp_info(node):
    ramp = node.color_ramp
    return {
        "interpolation": ramp.interpolation,
        "elements": [
            {
                "position": float(element.position),
                "color": [float(v) for v in element.color],
            }
            for element in ramp.elements
        ],
    }


def image_info(node):
    image = node.image
    if image is None:
        return None

    return {
        "image": image.name,
        "filepath": bpy.path.abspath(image.filepath) if image.filepath else "",
        "colorspace": image.colorspace_settings.name if image.colorspace_settings else "",
        "size": [int(image.size[0]), int(image.size[1])],
        "packed": image.packed_file is not None,
    }


def node_info(node):
    info = {
        "name": node.name,
        "label": node.label,
        "type": node.type,
        "bl_idname": node.bl_idname,
        "inputs": {},
        "outputs": [socket.name for socket in node.outputs],
    }

    for socket in node.inputs:
        value = default_value(socket)
        if value is not None:
            info["inputs"][socket.name] = value

    if node.type == "VALTORGB":
        info["color_ramp"] = color_ramp_info(node)
    elif node.type == "TEX_IMAGE":
        info["image"] = image_info(node)
    elif node.type == "GROUP" and node.node_tree is not None:
        info["node_tree"] = node.node_tree.name

    return info


def group_tree_info(node_tree):
    return {
        "name": node_tree.name,
        "nodes": [node_info(node) for node in node_tree.nodes],
        "links": [
            {
                "from_node": link.from_node.name,
                "from_socket": link.from_socket.name,
                "to_node": link.to_node.name,
                "to_socket": link.to_socket.name,
            }
            for link in node_tree.links
        ],
    }


def main():
    args = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []
    output_path = args[0] if args else os.path.join(os.getcwd(), "AC6MaterialGraph.json")
    material_token = args[1] if len(args) > 1 else "BD_M_1010][CM].matxml"

    material = next((m for m in bpy.data.materials if material_token in m.name), None)
    if material is None:
        raise RuntimeError(f"Material token not found: {material_token}")

    groups = []
    seen = set()

    def collect_groups(node_tree):
        for node in node_tree.nodes:
            if node.type != "GROUP" or node.node_tree is None or node.node_tree.name in seen:
                continue

            seen.add(node.node_tree.name)
            groups.append(group_tree_info(node.node_tree))
            collect_groups(node.node_tree)

    collect_groups(material.node_tree)

    report = {
        "blend_file": bpy.data.filepath,
        "material": material.name,
        "nodes": [node_info(node) for node in material.node_tree.nodes],
        "links": [
            {
                "from_node": link.from_node.name,
                "from_socket": link.from_socket.name,
                "to_node": link.to_node.name,
                "to_socket": link.to_socket.name,
            }
            for link in material.node_tree.links
        ],
        "groups": groups,
    }

    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    with open(output_path, "w", encoding="utf-8") as handle:
        json.dump(report, handle, ensure_ascii=False, indent=2)

    print(f"AC6 material graph report written: {output_path}")
    print(material.name)


if __name__ == "__main__":
    main()
