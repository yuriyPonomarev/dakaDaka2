"""Run with Blender: blender -b --python Tools/OptimizeGrab3DSkeleton.py -- input.glb output-dir.

Derives a skinned FBX and original base-colour texture from Grab3D's CC0 rigged GLB.
The FBX keeps the vertex groups; animations are supplied by the project's Humanoid library.
"""
import bpy
import os
import sys

args = sys.argv[sys.argv.index('--') + 1:]
source, destination = map(os.path.abspath, args)
os.makedirs(destination, exist_ok=True)
bpy.ops.object.select_all(action='SELECT')
bpy.ops.object.delete(use_global=False)
bpy.ops.import_scene.gltf(filepath=source)
mesh = next(o for o in bpy.context.scene.objects if o.type == 'MESH')
bpy.context.view_layer.objects.active = mesh
mesh.select_set(True)
modifier = mesh.modifiers.new('Game polygon reduction', 'DECIMATE')
modifier.ratio = 0.085
bpy.ops.object.modifier_move_to_index(modifier=modifier.name, index=0)
bpy.ops.object.modifier_apply(modifier=modifier.name)
print('RESULT triangles:', sum(len(p.vertices)-2 for p in mesh.data.polygons))
image = bpy.data.images['rig_basecolor']
image.filepath_raw = os.path.join(destination, 'Skeleton_BaseColor.png')
image.file_format = 'PNG'
image.save()
bpy.ops.object.select_all(action='SELECT')
bpy.ops.export_scene.fbx(filepath=os.path.join(destination, 'Skeleton_Optimized.fbx'),
    use_selection=True, object_types={'ARMATURE', 'MESH'}, add_leaf_bones=False,
    bake_anim=False, path_mode='AUTO')
