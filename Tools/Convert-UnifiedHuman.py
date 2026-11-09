import bpy,bmesh,hashlib,json
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
OUT=ROOT/'Assets/Emberdelve/Art/ThirdParty/Innerscene/Human'
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=str(OUT/'Source.glb'))
body=bpy.data.objects['base_male_body']; eyes=bpy.data.objects['base_male_body.high-poly']; rig=bpy.data.objects['base_male_body.rig']
for o in list(bpy.context.scene.objects):
 if o not in (body,eyes,rig): bpy.data.objects.remove(o,do_unlink=True)
# Imported glTF vertices are split at UV boundaries; weld coincident geometry without discarding loop UVs.
bm=bmesh.new();bm.from_mesh(body.data);bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=0.000001);bm.to_mesh(body.data);bm.free()
hand_groups={g.index for g in body.vertex_groups if g.name.startswith('finger') or g.name.startswith('wrist')}
protected=[v.index for v in body.data.vertices if sum(g.weight for g in v.groups if g.group in hand_groups)>.05]
reduce=body.vertex_groups.new(name='OptimizeBody');reduce.add(list(range(len(body.data.vertices))),1.,'REPLACE');reduce.add(protected,0.,'REPLACE')
bpy.context.view_layer.objects.active=body
mod=body.modifiers.new('PreserveHandsReduction','DECIMATE');mod.ratio=.30;mod.vertex_group=reduce.name;mod.vertex_group_factor=1000.;mod.use_collapse_triangulate=True
bpy.ops.object.modifier_apply(modifier=mod.name)
body.vertex_groups.remove(body.vertex_groups['OptimizeBody'])
# One skin/renderer avoids detached eye or first-person mesh substitution issues.
bpy.ops.object.select_all(action='DESELECT');body.select_set(True);eyes.select_set(True);bpy.context.view_layer.objects.active=body;bpy.ops.object.join()
body.name='HumanBody';body.data.name='HumanBody';rig.name='HumanRig'
for f in body.data.polygons:f.use_smooth=True
bpy.ops.object.select_all(action='DESELECT');body.select_set(True);rig.select_set(True);bpy.context.view_layer.objects.active=rig
bpy.ops.export_scene.fbx(filepath=str(OUT/'Human.fbx'),use_selection=True,object_types={'ARMATURE','MESH'},add_leaf_bones=False,bake_anim=False,axis_forward='-Z',axis_up='Y',apply_scale_options='FBX_SCALE_UNITS',use_mesh_modifiers=True,path_mode='AUTO')
manifest={'source_page':'https://www.innerscene.com/tools/library/3d-parts/male-base-mesh-3d-person-rigged-a-pose-380e6d89','source_download':'https://files.innerscene.com/library/male-base-mesh-3d-person-rigged-a-pose-380e6d89/038f3a526b2fc3efd012/male-base-mesh-base_male.glb','author':'Innerscene, adapted from MakeHuman CC0 assets','license':'CC0-1.0','retrieved':'2026-11-09','source_sha256':hashlib.sha256((OUT/'Source.glb').read_bytes()).hexdigest(),'fbx_sha256':hashlib.sha256((OUT/'Human.fbx').read_bytes()).hexdigest(),'vertices':len(body.data.vertices),'triangles':sum(len(f.vertices)-2 for f in body.data.polygons),'bones':len(rig.data.bones),'finger_bones':[b.name for b in rig.data.bones if b.name.startswith('finger')],'blender_front':'-Y','blender_up':'+Z','fbx_axis_forward':'-Z','fbx_axis_up':'Y','changes':'Removed importer helper, welded UV seam positions, weighted decimation protecting hand/finger weights, joined eyes and body. UVs and skin weights retained. No animation or texture images in source.'}
(OUT/'SourceManifest.json').write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
print('HUMAN_EXPORT',json.dumps(manifest))
