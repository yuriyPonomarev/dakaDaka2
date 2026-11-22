"""Generate visual-only traversal meshes using Blender (no Unity or prefab writes).

Run after WeatheredTraversalSurfaces.ExportDimensions():
  blender --background --python Tools/Art/BevelTraversal.py
  blender --background --python Tools/Art/BevelTraversal.py -- --manifest PATH

All intermediate files stay in SourceArt/Environment/WeatheredTraversal, outside Assets.
Bevel in physical metres, then normalize geometry to the original centered unit box.
Unity keeps every existing transform, material and collider unchanged on import.
"""
import argparse
import json
import math
import sys
from pathlib import Path

import bpy
import bmesh
from mathutils import Vector


VERSION = 1
SEGMENTS = 3
PROJECT_ROOT = Path(__file__).resolve().parents[2]


def vector_json(vector):
    return dict(zip(("x", "y", "z"), map(float, vector)))


def read_variant(row):
    size = Vector(tuple(float(row["size"][axis]) for axis in "xyz"))
    bevel = float(row["bevel"])
    requested_bevel = min((.025, .04), key=lambda value: abs(value - bevel))
    if (not all(math.isfinite(value) and value >= .06 for value in size)
            or not math.isfinite(bevel) or abs(bevel - requested_bevel) > 1e-6
            or int(row["segments"]) != SEGMENTS or bevel >= min(size) * .5):
        raise ValueError(f"Unsupported traversal dimensions/bevel: {row}")
    bevel = requested_bevel
    key = "Traversal_v1_" + "_".join(str(round(value * 100000)) for value in size)
    key += "_b" + str(round(bevel * 1000))
    if row["key"] != key:
        raise ValueError(f"Dimension key does not match geometry: {row['key']} != {key}")
    return size, bevel


def generate(row):
    size, bevel_width = read_variant(row)
    mesh = bpy.data.meshes.new(row["key"])
    bm = bmesh.new()
    try:
        bmesh.ops.create_cube(bm, size=1.0)
        bmesh.ops.scale(bm, vec=size, verts=list(bm.verts))
        bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
        bm.to_mesh(mesh)
    finally:
        bm.free()

    obj = bpy.data.objects.new(row["key"], mesh)
    bpy.context.collection.objects.link(obj)
    for selected in list(bpy.context.selected_objects):
        selected.select_set(False)
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    evaluated = None
    try:
        # With unit object scale, modifier widths are actual metres, not fractions
        # of a very tall stair support or a long gallery parapet.
        bevel = obj.modifiers.new("Worn traversal edges in metres", "BEVEL")
        bevel.width = bevel_width
        bevel.segments = SEGMENTS
        bevel.limit_method = "NONE"
        bevel.use_clamp_overlap = True
        bevel.harden_normals = True
        bevel.profile = .5
        for face in mesh.polygons:
            face.use_smooth = True
        normals = obj.modifiers.new("Preserve broad stone faces", "WEIGHTED_NORMAL")
        normals.keep_sharp = True
        normals.weight = 50

        evaluated = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
        result = evaluated.to_mesh()
        result.calc_loop_triangles()
        out = dict(version=VERSION, key=row["key"], size=vector_json(size),
                   bevel=bevel_width, segments=SEGMENTS,
                   vertices=[], normals=[], uv=[], triangles=[])
        top_area = 0.0
        minimum_normal_alignment = 1.0
        max_inverse_scale_normal_error = 0.0
        minimum = Vector((math.inf,) * 3)
        maximum = Vector((-math.inf,) * 3)
        corrected_normals = 0
        for tri in result.loop_triangles:
            a, b, c = (result.vertices[index].co.copy() for index in tri.vertices)
            cross = (b - a).cross(c - a)
            if cross.length_squared < 1e-16 or cross.dot((a + b + c) / 3.0) <= 0:
                raise ValueError(f"Degenerate/inverted triangle in {row['key']}")
            face_normal = cross.normalized()
            if all(abs(point.y - size.y * .5) < 1e-6 for point in (a, b, c)):
                top_area += cross.length * .5
            # Retain the cube's normalized planar coordinates for ordinary
            # materials as well as the project's world-projected stone shader.
            dominant = max(range(3), key=lambda axis: abs(face_normal[axis]))
            axes = ((2, 1), (0, 2), (0, 1))[dominant]
            for loop_index in tri.loops:
                point = result.vertices[result.loops[loop_index].vertex_index].co
                physical_normal = result.corner_normals[loop_index].vector.copy().normalized()
                if physical_normal.dot(face_normal) < .05:
                    physical_normal = face_normal.copy()
                    corrected_normals += 1
                normalized_point = Vector(tuple(point[i] / size[i] for i in range(3)))
                # The import transform is S=diag(size). Its normal transform is
                # S^-T, so encode S^T*n here (NOT n/size). This recovers the
                # original physical bevel normal under nonuniform Unity scale.
                local_normal = Vector(tuple(physical_normal[i] * size[i] for i in range(3))).normalized()
                placed_normal = Vector(tuple(local_normal[i] / size[i] for i in range(3))).normalized()
                error = (placed_normal - physical_normal).length
                max_inverse_scale_normal_error = max(max_inverse_scale_normal_error, error)
                alignment = placed_normal.dot(face_normal)
                minimum_normal_alignment = min(minimum_normal_alignment, alignment)
                if not all(math.isfinite(value) for value in (*normalized_point, *local_normal)):
                    raise ValueError(f"Nonfinite geometry in {row['key']}")
                if max(abs(value) for value in normalized_point) > .50002 or alignment < .01 or error > 1e-5:
                    raise ValueError(f"Invalid normalized geometry in {row['key']}")
                for axis in range(3):
                    minimum[axis] = min(minimum[axis], normalized_point[axis])
                    maximum[axis] = max(maximum[axis], normalized_point[axis])
                out["triangles"].append(len(out["vertices"]))
                out["vertices"].append(vector_json(normalized_point))
                out["normals"].append(vector_json(local_normal))
                out["uv"].append(dict(x=float(normalized_point[axes[0]] + .5),
                                      y=float(normalized_point[axes[1]] + .5)))

        if max(abs(minimum[i] + .5) for i in range(3)) > 1e-5 or max(abs(maximum[i] - .5) for i in range(3)) > 1e-5:
            raise ValueError(f"Original box extents changed in {row['key']}")
        core_area = (size.x - 2 * bevel_width) * (size.z - 2 * bevel_width)
        if top_area < core_area * .99:
            raise ValueError(f"Horizontal walk-plane core lost in {row['key']}")
        triangles = len(out["triangles"]) // 3
        if triangles <= 12 or len(out["vertices"]) > 20000:
            raise ValueError(f"Unexpected bevel complexity in {row['key']}: {triangles}")
        report = dict(key=row["key"], dimensions=vector_json(size), bevelMetres=bevel_width,
                      segments=SEGMENTS, vertices=len(out["vertices"]), triangles=triangles,
                      unitBoundsMin=vector_json(minimum), unitBoundsMax=vector_json(maximum),
                      topPlaneY=float(size.y * .5), flatTopArea=top_area,
                      requiredFlatCoreArea=core_area, minimumNormalAlignment=minimum_normal_alignment,
                      maxInverseScaleNormalError=max_inverse_scale_normal_error,
                      correctedCornerNormals=corrected_normals, instances=row["instances"])
        return out, report
    finally:
        if evaluated is not None:
            evaluated.to_mesh_clear()
        bpy.data.objects.remove(obj, do_unlink=True)
        if mesh.users == 0:
            bpy.data.meshes.remove(mesh)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path,
                        default=PROJECT_ROOT / "SourceArt/Environment/WeatheredTraversal/dimensions.json")
    arguments = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    args = parser.parse_args(arguments)
    manifest_path = args.manifest.resolve()
    if PROJECT_ROOT / "Assets" in manifest_path.parents:
        raise ValueError("Traversal intermediates must remain outside Assets")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    variants = manifest.get("variants", [])
    if (manifest.get("version") != VERSION or len(manifest.get("prefabs", [])) != 30
            or not variants or len({row["key"] for row in variants}) != len(variants)
            or manifest["surfaceCount"] != sum(row["instances"] for row in variants)
            or manifest["surfaceCount"] != sum(row["surfaces"] for row in manifest["prefabs"])):
        raise ValueError("Incomplete or stale traversal manifest; run ExportDimensions again")
    reports = []
    for row in variants:
        output, report = generate(row)
        destination = manifest_path.parent / (row["key"] + ".bevel.json")
        destination.write_text(json.dumps(output, separators=(",", ":"), allow_nan=False), encoding="utf-8")
        reports.append(report)
        print(f"[Traversal] {row['key']} triangles={report['triangles']} instances={row['instances']} "
              f"normalError={report['maxInverseScaleNormalError']:.3g} flatTopArea={report['flatTopArea']:.5f}")
    summary = dict(version=VERSION, blenderVersion=bpy.app.version_string, prefabs=30,
                   surfaces=manifest["surfaceCount"], variants=len(reports),
                   instancedTriangleCount=sum(row["triangles"] * row["instances"] for row in reports),
                   reports=reports)
    (manifest_path.parent / "numeric-qa.json").write_text(json.dumps(summary, indent=2, allow_nan=False), encoding="utf-8")
    print(f"[Traversal] generated {len(reports)} shared variants for {manifest['surfaceCount']} surfaces. "
          "No Unity assets, transforms or colliders written. Next: WeatheredTraversalSurfaces.ImportAndApply().")


if __name__ == "__main__":
    main()
