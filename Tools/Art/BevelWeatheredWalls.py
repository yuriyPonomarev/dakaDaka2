"""Blender 4.3 background: --python Tools/Art/BevelWeatheredWalls.py.
Derive visual-only rounded edges from existing authored wall profiles.
Source collision meshes remain untouched. Canonical Z is scaled to its .25m placement metric.
"""
import bpy
import bmesh
import json
import math
from pathlib import Path

root = Path("SourceArt/Environment/WeatheredWalls")
for source in sorted(root.glob("*.source.json")):
    data = json.loads(source.read_text(encoding="utf-8"))
    points = [(p["x"], p["y"], p["z"] * .25) for p in data["vertices"]]
    # Each original prism consists of six quads. Remove the coincident internal
    # ends between adjoining body sections before welding and beveling.
    quads = [list(range(i, i + 4)) for i in range(0, len(points), 4)]
    groups = {}
    for face in quads:
        key = tuple(sorted(tuple(round(c, 5) for c in points[i]) for i in face))
        groups.setdefault(key, []).append(face)
    faces = [group[0] for group in groups.values() if len(group) == 1]
    mesh = bpy.data.meshes.new(source.stem)
    mesh.from_pydata(points, [], faces)
    bm = bmesh.new(); bm.from_mesh(mesh)
    bmesh.ops.remove_doubles(bm, verts=list(bm.verts), dist=.00001)
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    bm.to_mesh(mesh); bm.free()
    obj = bpy.data.objects.new(source.stem, mesh)
    bpy.context.collection.objects.link(obj)
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bevel = obj.modifiers.new("Worn stone edges", "BEVEL")
    bevel.width = .035
    bevel.segments = 3
    bevel.limit_method = "ANGLE"
    bevel.angle_limit = math.radians(24)
    bevel.use_clamp_overlap = True
    bevel.harden_normals = True
    for poly in mesh.polygons: poly.use_smooth = True
    normal = obj.modifiers.new("Keep broad stone faces flat", "WEIGHTED_NORMAL")
    normal.keep_sharp = True
    normal.weight = 50
    evaluated = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
    result = evaluated.to_mesh(); result.calc_loop_triangles()
    out = dict(vertices=[], normals=[], uv=[], triangles=[])
    # Split loop corners retain the bevel's custom shading. World stone shader
    # uses world projection; planar UVs are retained for conventional materials.
    for tri in result.loop_triangles:
        a, b, c = (result.vertices[i].co for i in tri.vertices)
        face_normal = (b - a).cross(c - a).normalized()
        for loop_id in tri.loops:
            loop = result.loops[loop_id]
            p = result.vertices[loop.vertex_index].co
            n = result.corner_normals[loop_id].vector.copy()
            # Keep custom normals on the triangle's outward hemisphere, including
            # tiny clamped bevel junctions where weighted normals can overshoot.
            alignment = n.dot(face_normal)
            if alignment < .05:
                n += face_normal * (.05 - alignment)
                n.normalize()
            # The same profile is also compressed into diagonal corner piers.
            # Keep weighted normals outward after those nonuniform placements.
            for sx, sy in ((.13, .81), (.17, .81), (1.5, 1), (1.6, 1.18)):
                placed_n, placed_face = n.copy(), face_normal.copy()
                placed_n.x /= sx; placed_n.y /= sy; placed_n.normalize()
                placed_face.x /= sx; placed_face.y /= sy; placed_face.normalize()
                if placed_n.dot(placed_face) < .1:
                    n = face_normal.copy()
                    break
            n.z *= .25; n.normalize() # inverse-transpose of export Z scale
            out["triangles"].append(len(out["vertices"]))
            out["vertices"].append(dict(x=p.x, y=p.y, z=p.z/.25))
            out["normals"].append(dict(x=n.x, y=n.y, z=n.z))
            if abs(face_normal.y) > abs(face_normal.x) + abs(face_normal.z): uv = (p.x, p.z/.25)
            else: uv = (p.z/.25 if abs(face_normal.x) > abs(face_normal.z) else p.x, p.y)
            out["uv"].append(dict(x=uv[0], y=uv[1]))
    target = source.with_name(source.name.replace(".source.json", ".bevel.json"))
    target.write_text(json.dumps(out, separators=(",", ":")), encoding="utf-8")
    print(source.stem, "triangles", len(data["triangles"])//3, "->", len(out["triangles"])//3)
    evaluated.to_mesh_clear()
    bpy.data.objects.remove(obj, do_unlink=True)
