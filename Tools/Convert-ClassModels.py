# Run with portable Blender: blender --background --python Tools/Convert-ClassModels.py -- <project-root>
import bpy, sys
from pathlib import Path
root=Path(sys.argv[sys.argv.index('--')+1])/'Assets/Emberdelve/Art/ThirdParty/KayKit/Adventurers'
keep={'Idle','Walking_A','Running_A','Blocking','1H_Melee_Attack_Slice_Horizontal','2H_Ranged_Shoot','Death_A','Death_A_Pose'}
for name in ['Knight','Rogue_Hooded']:
 bpy.ops.wm.read_factory_settings(use_empty=True)
 bpy.ops.import_scene.gltf(filepath=str(root/(name+'.glb')))
 # Source scenes contain all optional weapon variants at once. Export only the dressed body.
 for obj in list(bpy.data.objects):
  if obj.type=='MESH' and not any(m.type=='ARMATURE' for m in obj.modifiers) and not (obj.name.endswith('_Cape') or obj.name=='Knight_Helmet'):
   bpy.data.objects.remove(obj,do_unlink=True)
 for obj in bpy.data.objects:
  if obj.animation_data:
   obj.animation_data.action=None
   for track in list(obj.animation_data.nla_tracks): obj.animation_data.nla_tracks.remove(track)
 for action in list(bpy.data.actions):
  if action.name.removesuffix('_Rig') not in keep: bpy.data.actions.remove(action)
 print('EXPORT',name,[a.name for a in bpy.data.actions])
 bpy.ops.export_scene.fbx(filepath=str(root/(name+'.fbx')),use_selection=False,object_types={'ARMATURE','MESH'},add_leaf_bones=False,bake_anim=True,bake_anim_use_all_actions=True,bake_anim_use_nla_strips=False,bake_anim_simplify_factor=0.5,axis_forward='-Z',axis_up='Y',path_mode='STRIP',apply_scale_options='FBX_SCALE_ALL')
