using UnityEngine;
using Emberdelve.AI;
using Emberdelve.Data;
using Emberdelve.Combat;
using Emberdelve.Level;
using Emberdelve.Core;
namespace Emberdelve.Player
{
    // Cosmetic only: follows the existing rig and cannot move it or add hit shapes.
    public sealed class PlayerAvatar : MonoBehaviour
    {
        private PlayerRig _rig;
        private CharacterClassData _class;
        private GameObject _model;
        private Animation _animation;
        private Animator _animator;
        private Renderer[] _renderers;
        private Vector3 _lastPosition;
        private string _state;
        private RemoteWeaponPresentation _weapons;
        private MeleeCombat _melee;
        private EquippedArmorView _armor;
        private readonly string[] _armorIds = new string[6];
        private bool _hasArmorIds;
        private bool _remoteGuard,_remoteSwing;
        public void PresentArmor(string head, string chest, string legs, string hands, string feet, string back)
        {
            _hasArmorIds=true;
            _armorIds[0]=head; _armorIds[1]=chest; _armorIds[2]=legs;
            _armorIds[3]=hands; _armorIds[4]=feet; _armorIds[5]=back;
            if(_armor!=null && _armor.BindVisualIds(head,chest,legs,hands,feet,back))
                _renderers=_model.GetComponentsInChildren<Renderer>(true);
        }
        public void PresentUtility(string itemId) { if (_weapons != null) _weapons.PresentUtility(itemId); }
        public void PresentCombat(string main,string off,string pattern,bool guard,bool swing,float elapsed,SwingTiming timing,SwingFrame frame,
            bool recoil=false,float stopFraction=0f,float recoilProgress=0f,bool bot=false)
        {
            _remoteGuard=guard; _remoteSwing=swing || recoil;
            if(_weapons!=null) _weapons.Present(main,off,pattern,guard,swing,elapsed,timing,frame,recoil,stopFraction,recoilProgress,bot);
        }
        public static void Bind(PlayerRig rig)
        {
            var view=rig.GetComponent<PlayerAvatar>(); if(view==null) view=rig.gameObject.AddComponent<PlayerAvatar>();
            view._rig=rig; view._melee=rig.GetComponent<MeleeCombat>(); view.RefreshModel();
        }
        private void RefreshModel()
        {
            if(_rig==null || _class==_rig.Class) return;
            _class=_rig.Class; if(_model!=null) Destroy(_model);
            if(_class==null || _class.characterVisual==null) return;
            _model=Instantiate(_class.characterVisual,transform); _model.name="ClassAvatar";
            _model.transform.localPosition=Vector3.zero; _model.transform.localRotation=Quaternion.identity;
            _animator=_model.GetComponentInChildren<Animator>();
            _animation=_model.GetComponentInChildren<Animation>();
            _armor=_model.GetComponent<EquippedArmorView>() ?? _model.AddComponent<EquippedArmorView>(); _armor.Bind(_rig.Equipment);
            if(_hasArmorIds) _armor.BindVisualIds(_armorIds[0],_armorIds[1],_armorIds[2],_armorIds[3],_armorIds[4],_armorIds[5]);
            else _armor.Refresh();
            _renderers=_model.GetComponentsInChildren<Renderer>(true);
            if(_weapons==null) _weapons=gameObject.AddComponent<RemoteWeaponPresentation>();
            _weapons.Bind(_rig,_model);
            _lastPosition=transform.position; _state=null;
        }
        private void LateUpdate()
        {
            RefreshModel(); if(_model==null) return;
            if(_armor!=null && _armor.Refresh()) _renderers=_model.GetComponentsInChildren<Renderer>(true);
            bool visible=!_rig.IsLocallyControlled && !_rig.IsExtracted
                && !DungeonModuleSceneAssembler.IsPositionOnRetiredFloor(transform.position);
            if (visible && NetRole.RunsSimulation && TryGetComponent<RoyaleBotController>(out _))
                PresentBotCombat();
            foreach(var renderer in _renderers) if(renderer!=null && renderer.enabled!=visible) renderer.enabled=visible;
            Vector3 delta=transform.position-_lastPosition; _lastPosition=transform.position; delta.y=0;
            float speed=Time.deltaTime>0 ? delta.magnitude/Time.deltaTime : 0;
            string next=_rig.IsDead ? "Death" : (_remoteGuard || _rig.IsGuarding) ? "Block" : _remoteSwing ? "Idle" : speed>3.8f ? "Run" : speed>.15f ? "Walk" : "Idle";
            if(next!=_state)
            {
                _state=next;
                if(_animator!=null) _animator.CrossFadeInFixedTime(next,.15f);
                else if(_animation!=null) _animation.CrossFade(next,.15f);
            }
        }

        private void PresentBotCombat()
        {
            var weapon = _rig.ActiveWeapon;
            var offhand = _rig.OffhandWeapon;
            string main = weapon != null ? weapon.weaponId : "";
            string off = !_rig.IsHoldingUtility && offhand != null && (weapon == null || !weapon.twoHanded)
                ? offhand.weaponId : "";
            if (_melee != null && _melee.TryGetSwingPresentation(out var swing))
            {
                float stop = Mathf.Clamp01((float)swing.WallContactSample / Mathf.Max(1, swing.SampleCount));
                float recoil = swing.Timing.WallRecoil > 0f
                    ? Mathf.Clamp01((swing.Elapsed - swing.Timing.Windup - swing.Timing.Active * stop) / swing.Timing.WallRecoil) : 1f;
                PresentCombat(main, off, _melee.ActivePattern.patternId, false, !swing.Recoiled, swing.Elapsed,
                    swing.Timing, swing.PresentationFrame, swing.Recoiled, stop, recoil, true);
                return;
            }
            var frame = new SwingFrame(_rig.AimOrigin, Quaternion.LookRotation(_rig.AimDirection));
            PresentCombat(main, off, "", _rig.IsGuarding, false, 0f, default, frame, bot: true);
        }
    }
}
