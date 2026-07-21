import bpy


def node_label(node):
    return f"{node.bl_idname}:{node.name}"


def print_ramp(node, prefix):
    if node.bl_idname != "ShaderNodeValToRGB":
        return
    values = []
    for element in node.color_ramp.elements:
        color = tuple(round(value, 6) for value in element.color)
        values.append((round(element.position, 6), color))
    print(f"{prefix} RAMP interpolation={node.color_ramp.interpolation} values={values}")


def walk_from_socket(socket, depth, visited, prefix):
    if depth < 0:
        return
    for link in socket.links:
        target = link.to_node
        key = (target.id_data.name, target.name, link.to_socket.name)
        if key in visited:
            continue
        visited.add(key)
        print(
            f"{prefix} -> {node_label(target)} socket={link.to_socket.name} "
            f"tree={target.id_data.name}"
        )
        print_ramp(target, prefix + "   ")
        if target.bl_idname == "ShaderNodeGroup" and target.node_tree:
            dump_tree(target.node_tree, depth - 1, visited, prefix + "   ")
        for output in target.outputs:
            if output.is_linked:
                walk_from_socket(output, depth - 1, visited, prefix + "   ")


def dump_tree(tree, depth, visited, prefix):
    for node in tree.nodes:
        image_name = ""
        if node.bl_idname == "ShaderNodeTexImage" and node.image:
            image_name = node.image.name
        interesting = (
            "3m" in image_name.lower()
            or "mask" in node.name.lower()
            or "color" in node.name.lower()
            or node.bl_idname == "ShaderNodeValToRGB"
        )
        if not interesting:
            continue
        print(f"{prefix}NODE {node_label(node)} image={image_name} tree={tree.name}")
        print_ramp(node, prefix + "  ")
        if "3m" in image_name.lower():
            for output in node.outputs:
                if output.is_linked:
                    walk_from_socket(output, depth, visited, prefix + "  ")


print("AC6_PAINT_INSPECT_BEGIN")
visited = set()
detail_written = False
for material in bpy.data.materials:
    if not material.use_nodes or not material.node_tree:
        continue
    has_3m = any(
        node.bl_idname == "ShaderNodeTexImage"
        and node.image
        and "3m" in node.image.name.lower()
        for node in material.node_tree.nodes
    )
    if has_3m:
        print(f"MATERIAL {material.name}")
        dump_tree(material.node_tree, 8, visited, "  ")
        if not detail_written and "1010" in material.name:
            detail_written = True
            print(f"DETAIL_BEGIN {material.name}")
            for node in material.node_tree.nodes:
                inputs = []
                for socket in node.inputs:
                    value = getattr(socket, "default_value", None)
                    if hasattr(value, "__len__") and not isinstance(value, str):
                        try:
                            value = tuple(round(float(item), 6) for item in value)
                        except (TypeError, ValueError):
                            value = str(value)
                    inputs.append((socket.name, value, socket.is_linked))
                print(f"DETAIL_NODE {node_label(node)} inputs={inputs}")
            for link in material.node_tree.links:
                print(
                    f"DETAIL_LINK {link.from_node.name}.{link.from_socket.name} -> "
                    f"{link.to_node.name}.{link.to_socket.name}"
                )
            print("DETAIL_END")
            shader_group = next(
                (node for node in material.node_tree.nodes if node.bl_idname == "ShaderNodeGroup" and node.name == "Group"),
                None,
            )
            if shader_group and shader_group.node_tree:
                tree = shader_group.node_tree
                print(f"GROUP_TRACE_BEGIN {tree.name}")
                for node in tree.nodes:
                    if node.bl_idname == "ShaderNodeGroup" and node.node_tree:
                        print(
                            f"GROUP_NODE {node.name} tree={node.node_tree.name} "
                            f"inputs={[socket.name for socket in node.inputs]} "
                            f"outputs={[socket.name for socket in node.outputs]}"
                        )
                group_input = next(
                    (node for node in tree.nodes if node.bl_idname == "NodeGroupInput"),
                    None,
                )
                if group_input:
                    for socket_name in ("Mask_0", "Mask_1"):
                        socket = group_input.outputs.get(socket_name)
                        if socket:
                            print(f"GROUP_TRACE_SOCKET {socket_name}")
                            walk_from_socket(socket, 14, set(), "  ")
                print("GROUP_TRACE_END")
                for tree_name in ("Infinite Masking Masks.001", "Infinite Color.001"):
                    target_tree = bpy.data.node_groups.get(tree_name)
                    if not target_tree:
                        continue
                    print(f"SUBGROUP_DETAIL_BEGIN {tree_name}")
                    for node in target_tree.nodes:
                        inputs = []
                        for socket in node.inputs:
                            value = getattr(socket, "default_value", None)
                            if hasattr(value, "__len__") and not isinstance(value, str):
                                try:
                                    value = tuple(round(float(item), 6) for item in value)
                                except (TypeError, ValueError):
                                    value = str(value)
                            inputs.append((socket.name, value, socket.is_linked))
                        print(f"SUBGROUP_NODE {node_label(node)} inputs={inputs}")
                    for link in target_tree.links:
                        print(
                            f"SUBGROUP_LINK {link.from_node.name}.{link.from_socket.name} -> "
                            f"{link.to_node.name}.{link.to_socket.name}"
                        )
                    print("SUBGROUP_DETAIL_END")

for group in bpy.data.node_groups:
    name = group.name.lower()
    if "cm" in name or "mask" in name or "color" in name:
        print(f"GROUP {group.name}")
        dump_tree(group, 4, visited, "  ")
print("AC6_PAINT_INSPECT_END")
