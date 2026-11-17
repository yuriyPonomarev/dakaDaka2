"""Blender background repair for the 2026-11-17 Meshy hero's missing fingers.

Preserves the generated surface, UVs, body skeleton and body weights. Adds thirty
deforming finger joints fitted to the separated fingers of this specific source.
Run Blender --background --factory-startup --python this.py -- input.glb output.fbx.
"""
import bpy
import json
import sys
from pathlib import Path
import numpy as np
from mathutils import Vector

source, destination = map(Path, sys.argv[sys.argv.index('--') + 1:])
bpy.ops.object.select_all(action='SELECT')
bpy.ops.object.delete(use_global=False)
bpy.ops.import_scene.gltf(filepath=str(source.resolve()))
arm = next(o for o in bpy.context.scene.objects if o.type == 'ARMATURE')
mesh = next(o for o in bpy.context.scene.objects if o.type == 'MESH')
points = np.empty(len(mesh.data.vertices) * 3, dtype=np.float64)
mesh.data.vertices.foreach_get('co', points)
points = points.reshape(-1, 3)
world = np.array(mesh.matrix_world)
points = points @ world[:3, :3].T + world[:3, 3]
# Blender world -> glTF world (X right, Y up, Z front).
points = points[:, [0, 2, 1]] * [1, 1, -1]

# Cross-section centers were fitted to this generated mesh's open-hand contour.
# Each chain contains MCP/CMC, PIP/MCP, DIP/IP, and the fingertip.
landmarks = {
    'Thumb': [[.811, 1.440], [.838, 1.479], [.852, 1.508], [.858, 1.529]],
    'Index': [[.885, 1.462], [.933, 1.476], [.962, 1.479], [.985, 1.478]],
    'Middle': [[.890, 1.420], [.945, 1.429], [.979, 1.430], [1.005, 1.430]],
    'Ring': [[.884, 1.385], [.935, 1.382], [.967, 1.379], [.989, 1.378]],
    'Little': [[.868, 1.359], [.905, 1.341], [.930, 1.333], [.948, 1.331]],
}
chains = {}
for side, sign, offset in [('Right', -1, 0), ('Left', 1, .008)]:
    for finger, values in landmarks.items():
        xy = np.array(values, dtype=float)
        xy[:, 0] = (xy[:, 0] + offset) * sign
        centers = []
        for x, y in xy:
            near = points[(points[:, 0]-x)**2 + (points[:, 1]-y)**2 < .009**2]
            if len(near) < 6:
                raise RuntimeError('Missing finger cross-section: ' + side + finger)
            # Midpoint of front/back surfaces keeps the hinge inside the finger.
            z = (np.quantile(near[:, 2], .1) + np.quantile(near[:, 2], .9)) * .5
            centers.append([x, y, z])
        chains[(side, finger)] = np.array(centers)

bpy.context.view_layer.objects.active = arm
arm.select_set(True)
bpy.ops.object.mode_set(mode='EDIT')
inverse = arm.matrix_world.inverted()
for (side, finger), chain in chains.items():
    parent = arm.data.edit_bones[side + 'Hand']
    for i, suffix in enumerate(['Proximal', 'Intermediate', 'Distal']):
        name = side + finger + suffix
        bone = arm.data.edit_bones.new(name)
        bone.head = inverse @ Vector((chain[i, 0], -chain[i, 2], chain[i, 1]))
        bone.tail = inverse @ Vector((chain[i+1, 0], -chain[i+1, 2], chain[i+1, 1]))
        bone.parent = parent
        bone.use_connect = i > 0
        bone.align_roll(inverse.to_3x3() @ Vector((0, -1, 0)))
        parent = bone
bpy.ops.object.mode_set(mode='OBJECT')

statistics = {}
for side, sign, offset in [('Right', -1, 0), ('Left', 1, .008)]:
    # Retain the imported lower-arm blend. Replace only hand/end contributions.
    hand_names = {side + 'Hand'} | {b.name for b in arm.data.bones[side+'Hand'].children
                                   if not any(f in b.name for f in landmarks)}
    old_groups = [g for g in mesh.vertex_groups if g.name in hand_names]
    group_ids = {g.index for g in old_groups}
    mass = np.array([sum(g.weight for g in v.groups if g.group in group_ids)
                     for v in mesh.data.vertices])
    eligible = np.flatnonzero((mass > .001) & (points[:, 0]*sign > .79+offset))
    p = points[eligible]
    distances, parameters, lengths = [], [], []
    for finger in landmarks:
        c = chains[(side, finger)]
        # Work in the palm plane so front/back surfaces receive identical weights.
        delta = np.diff(c[:, :2], axis=0)
        lens = np.linalg.norm(delta, axis=1)
        all_d, all_u = [], []
        cumulative = 0.
        for i in range(3):
            t = np.sum((p[:, :2]-c[i, :2])*delta[i], axis=1) / lens[i]**2
            # Extend the first segment into the palm for a smooth MCP transition.
            t = np.clip(t, -.8 if i == 0 else 0, 1)
            projection = c[i, :2] + t[:, None]*delta[i]
            all_d.append(np.sum((p[:, :2]-projection)**2, axis=1))
            all_u.append(cumulative + t*lens[i])
            cumulative += lens[i]
        all_d = np.array(all_d).T
        choice = np.argmin(all_d, axis=1)
        distances.append(all_d[np.arange(len(p)), choice])
        parameters.append(np.array(all_u).T[np.arange(len(p)), choice])
        lengths.append(lens)
    choice = np.argmin(np.array(distances).T, axis=1)
    for fi, finger in enumerate(landmarks):
        selected = np.flatnonzero(choice == fi)
        ids = eligible[selected]
        u = parameters[fi][selected]
        lens = lengths[fi]
        blend = np.clip((u+.014)/.030, 0, 1)
        blend = blend*blend*(3-2*blend)
        centers = np.cumsum(lens) - .5*lens
        weights = np.zeros((len(ids), 3))
        t0 = np.clip((u-centers[0])/(centers[1]-centers[0]), 0, 1)
        t1 = np.clip((u-centers[1])/(centers[2]-centers[1]), 0, 1)
        weights[:, 0] = 1-t0
        weights[:, 1] = t0*(1-t1)
        weights[:, 2] = t1
        weights *= (blend*mass[ids])[:, None]
        for g in old_groups:
            g.remove(ids.tolist())
        palm = mesh.vertex_groups.get(side+'Hand')
        for index, weight in zip(ids, (1-blend)*mass[ids]):
            if weight > .00001:
                palm.add([int(index)], float(weight), 'REPLACE')
        for i, suffix in enumerate(['Proximal', 'Intermediate', 'Distal']):
            group = mesh.vertex_groups.new(name=side+finger+suffix)
            for index, weight in zip(ids, weights[:, i]):
                if weight > .00001:
                    group.add([int(index)], float(weight), 'REPLACE')
        statistics[side+finger] = {'vertices': len(ids), 'joint_centers': chains[(side,finger)].tolist()}

# Smooth the webbing across the dense surface, while preserving the original
# total hand influence at every vertex and all original lower-arm weights.
mesh.data.calc_loop_triangles()
triangles = np.empty(len(mesh.data.loop_triangles)*3, dtype=np.int32)
mesh.data.loop_triangles.foreach_get('vertices', triangles)
triangles = triangles.reshape(-1,3)
for side, sign in [('Right',-1),('Left',1)]:
    names = [side+'Hand'] + [side+f+s for f in landmarks for s in ['Proximal','Intermediate','Distal']]
    groups = [mesh.vertex_groups[n] for n in names]
    lookup = {g.index:i for i,g in enumerate(groups)}
    ids = np.flatnonzero(points[:,0]*sign > .79)
    remap = np.full(len(points), -1, dtype=np.int32)
    remap[ids] = np.arange(len(ids))
    w = np.zeros((len(ids),len(groups)))
    for row,index in enumerate(ids):
        for g in mesh.data.vertices[int(index)].groups:
            if g.group in lookup:
                w[row,lookup[g.group]] = g.weight
    mass = w.sum(axis=1)
    edges = np.concatenate([triangles[:,[0,1]],triangles[:,[1,2]],triangles[:,[2,0]]])
    edges = remap[edges]
    edges = edges[(edges>=0).all(axis=1)]
    edges = np.concatenate([edges,edges[:,::-1]])
    degree = np.bincount(edges[:,0],minlength=len(ids))
    for _ in range(8):
        sums = np.zeros_like(w)
        np.add.at(sums,edges[:,0],w[edges[:,1]])
        average = sums / np.maximum(degree[:,None],1)
        w = .45*w + .55*average
        w *= (mass / np.maximum(w.sum(axis=1),1e-12))[:,None]
    for column,g in enumerate(groups):
        g.remove(ids.tolist())
        for row in np.flatnonzero(w[:,column] > .00001):
            g.add([int(ids[row])],float(w[row,column]),'REPLACE')

# Normalize retained forearm weights plus the new finger contributions.
bpy.context.view_layer.objects.active = mesh
bpy.ops.object.mode_set(mode='WEIGHT_PAINT')
bpy.ops.object.vertex_group_normalize_all(lock_active=False)
bpy.ops.object.mode_set(mode='OBJECT')
# glTF splits vertices at UV/normal seams. They must keep identical weights or
# the apparently continuous palm opens along those seams when the fingers bend.
ids = np.flatnonzero(np.abs(points[:,0]) > .79)
_, equivalent, counts = np.unique(np.round(points[ids]*1e6).astype(np.int64),
                                axis=0,return_inverse=True,return_counts=True)
groups = list(mesh.vertex_groups)
weights = np.zeros((len(ids), len(groups)))
for row,index in enumerate(ids):
    for g in mesh.data.vertices[int(index)].groups:
        weights[row,g.group] = g.weight
sums = np.zeros((len(counts),len(groups)))
np.add.at(sums,equivalent,weights)
weights = (sums/counts[:,None])[equivalent]
for column,g in enumerate(groups):
    g.remove(ids.tolist())
    for row in np.flatnonzero(weights[:,column]>.00001):
        g.add([int(ids[row])],float(weights[row,column]),'REPLACE')
bpy.ops.object.select_all(action='DESELECT')
mesh.select_set(True)
arm.select_set(True)
bpy.context.view_layer.objects.active = arm
destination.parent.mkdir(parents=True, exist_ok=True)
bpy.ops.export_scene.fbx(filepath=str(destination.resolve()), use_selection=True,
    object_types={'ARMATURE','MESH'}, add_leaf_bones=False, bake_anim=False,
    axis_forward='-Z', axis_up='Y', apply_unit_scale=True,
    apply_scale_options='FBX_SCALE_UNITS', use_armature_deform_only=True,
    use_mesh_modifiers=False, mesh_smooth_type='OFF', path_mode='STRIP')
destination.with_suffix('.repair.json').write_text(json.dumps({
    'source': str(source), 'finger_joints_added': 30,
    'surface_vertices_preserved': len(mesh.data.vertices), 'fingers': statistics,
}, indent=2))
print('REPAIRED_FINGER_JOINTS=30; VERTICES=' + str(len(mesh.data.vertices)))
