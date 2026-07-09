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


def args_after_double_dash():
    if "--" not in sys.argv:
        return []
    return sys.argv[sys.argv.index("--") + 1 :]


def read_hkx_bone_names(path):
    with open(path, "r", encoding="utf-8") as handle:
        data = json.load(handle)
    return {bone["Name"] for bone in data["Bones"]}


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


def analyze_mesh(obj, hkx_bone_names):
    group_by_index = {group.index: group.name for group in obj.vertex_groups}
    hkx_groups = {group.index for group in obj.vertex_groups if group.name in hkx_bone_names}
    non_hkx_groups = {group.index for group in obj.vertex_groups if group.name not in hkx_bone_names}

    vertices_with_hkx_weight = 0
    vertices_only_non_hkx = 0
    vertices_with_non_hkx_weight = 0
    vertices_without_weights = 0
    non_hkx_weight_sum_by_group = {}
    non_hkx_vertex_count_by_group = {}

    for vertex in obj.data.vertices:
        hkx_weight = 0.0
        non_hkx_weight = 0.0
        if not vertex.groups:
            vertices_without_weights += 1
            continue

        for assignment in vertex.groups:
            if assignment.group in hkx_groups:
                hkx_weight += assignment.weight
            elif assignment.group in non_hkx_groups:
                non_hkx_weight += assignment.weight
                group_name = group_by_index[assignment.group]
                non_hkx_weight_sum_by_group[group_name] = non_hkx_weight_sum_by_group.get(group_name, 0.0) + assignment.weight
                non_hkx_vertex_count_by_group[group_name] = non_hkx_vertex_count_by_group.get(group_name, 0) + 1

        if hkx_weight > 0.0:
            vertices_with_hkx_weight += 1
        if non_hkx_weight > 0.0:
            vertices_with_non_hkx_weight += 1
        if hkx_weight <= 0.0 and non_hkx_weight > 0.0:
            vertices_only_non_hkx += 1

    non_hkx_groups_report = [
        {
            "group": name,
            "weighted_vertices": non_hkx_vertex_count_by_group.get(name, 0),
            "weight_sum": non_hkx_weight_sum_by_group.get(name, 0.0),
        }
        for name in sorted(non_hkx_weight_sum_by_group)
    ]
    non_hkx_groups_report.sort(key=lambda row: (row["weighted_vertices"], row["weight_sum"]), reverse=True)

    return {
        "mesh": obj.name,
        "vertex_count": len(obj.data.vertices),
        "vertex_group_count": len(obj.vertex_groups),
        "hkx_vertex_group_count": len(hkx_groups),
        "non_hkx_vertex_group_count": len(non_hkx_groups),
        "vertices_with_hkx_weight": vertices_with_hkx_weight,
        "vertices_with_non_hkx_weight": vertices_with_non_hkx_weight,
        "vertices_only_non_hkx": vertices_only_non_hkx,
        "vertices_without_weights": vertices_without_weights,
        "weighted_non_hkx_groups_top": non_hkx_groups_report[:40],
    }


def main():
    args = args_after_double_dash()
    if len(args) < 2:
        raise RuntimeError("Usage: blender <blend> --background --python DiagnoseAC6MelanderVertexGroups.py -- <pose_json> <report_json>")

    pose_json = args[0]
    report_json = args[1]
    hkx_bone_names = read_hkx_bone_names(pose_json)
    meshes = source_meshes()
    reports = [analyze_mesh(mesh, hkx_bone_names) for mesh in meshes]

    summary = {
        "mesh_count": len(reports),
        "vertices": sum(row["vertex_count"] for row in reports),
        "vertices_only_non_hkx": sum(row["vertices_only_non_hkx"] for row in reports),
        "vertices_without_weights": sum(row["vertices_without_weights"] for row in reports),
        "meshes_with_only_non_hkx_vertices": [
            row["mesh"] for row in reports if row["vertices_only_non_hkx"] > 0
        ],
    }

    os.makedirs(os.path.dirname(report_json), exist_ok=True)
    with open(report_json, "w", encoding="utf-8") as handle:
        json.dump({"pose_json": pose_json, "summary": summary, "meshes": reports}, handle, ensure_ascii=False, indent=2)

    print(f"Wrote vertex-group diagnostic report: {report_json}")
    print(
        f"Meshes {summary['mesh_count']}, vertices {summary['vertices']}, "
        f"only non-HKX vertices {summary['vertices_only_non_hkx']}, "
        f"unweighted vertices {summary['vertices_without_weights']}"
    )


if __name__ == "__main__":
    main()
