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
        private LocomotionStrides _strides;
        private bool _hasLocoScale;
        private Vector3 _velocity;
        private float _bodyTurn;
        private int _armLayer=-1;
        private string _armState;
        private float _armWeight;
        private static readonly int LocoScaleHash=Animator.StringToHash(LocomotionGait.ScaleParameter);
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
            _strides=_model.GetComponent<LocomotionStrides>();
            _hasLocoScale=false;
            if(_animator!=null) foreach(var p in _animator.parameters) if(p.nameHash==LocoScaleHash && p.type==AnimatorControllerParameterType.Float) _hasLocoScale=true;
            _armor=_model.GetComponent<EquippedArmorView>() ?? _model.AddComponent<EquippedArmorView>(); _armor.Bind(_rig.Equipment);
            if(_hasArmorIds) _armor.BindVisualIds(_armorIds[0],_armorIds[1],_armorIds[2],_armorIds[3],_armorIds[4],_armorIds[5]);
            else _armor.Refresh();
            _renderers=_model.GetComponentsInChildren<Renderer>(true);
            if(_weapons==null) _weapons=gameObject.AddComponent<RemoteWeaponPresentation>();
            _weapons.Bind(_rig,_model);
            _lastPosition=transform.position; _state=null;
            _armLayer=_animator!=null ? _animator.GetLayerIndex(AvatarArms.Layer) : -1; _armState=null; _armWeight=0f;
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
            float dt=Time.deltaTime;
            if(dt>0) _velocity=Vector3.Lerp(_velocity,delta/dt,1f-Mathf.Exp(-dt/.08f));
            float speed=_velocity.magnitude;
            bool fighting=_remoteGuard || _rig.IsGuarding || _remoteSwing;
            // Speed-matched gait (LocomotionGait): run from 1.9 m/s instead of walking a 1 m/s loop at full player speed, the loop's rate follows
            // the ground speed (backward for a retreat), and the body turns toward a sideways move unless it is guarding or swinging.
            bool guarding=_remoteGuard || _rig.IsGuarding;
            string gait=speed>LocomotionGait.RunFrom ? "Run" : speed>LocomotionGait.StandBelow ? "Walk" : "Idle";
            // With the arm layer the legs keep their gait under a guard or a strike (they used to stand still and slide); the arms
            // take the weapon stance or the guard from the layer. A controller without it keeps the whole-body states.
            string next=_rig.IsDead ? "Death" : _armLayer>=0 ? gait : guarding ? "Block" : _remoteSwing ? "Idle" : gait;
            if(_armLayer>=0)
            {
                string arms=_rig.IsDead || _weapons==null || !_weapons.HoldsMelee ? AvatarArms.Free
                    : guarding ? (_weapons.HoldsShield ? AvatarArms.ShieldGuard : AvatarArms.Guard) : AvatarArms.Armed;
                if(arms!=_armState) { _armState=arms; _animator.CrossFadeInFixedTime(arms,AvatarArms.Fade,_armLayer); }
                _armWeight=Mathf.MoveTowards(_armWeight,arms==AvatarArms.Free ? 0f : 1f,dt*AvatarArms.WeightRate);
                _animator.SetLayerWeight(_armLayer,_armWeight);
            }
            if(next!=_state)
            {
                _state=next;
                if(_animator!=null) _animator.CrossFadeInFixedTime(next,.15f);
                else if(_animation!=null) _animation.CrossFade(next,.15f);
            }
            bool moving=_state=="Walk" || _state=="Run";
            LocomotionGait.Resolve(_velocity,transform.forward,LocomotionStrides.StrideOf(_model.transform,_strides,_state=="Run"),1f,out float rate,out float turn);
            if(_animator!=null)
            {
                if(_hasLocoScale){ _animator.SetFloat(LocoScaleHash,moving ? rate : 1f); _animator.speed=1f; }
                else _animator.speed=moving ? Mathf.Abs(rate) : 1f;   // older controllers: rate only, no reverse
            }
            _bodyTurn=Mathf.Lerp(_bodyTurn,moving && !fighting && !_rig.IsDead ? turn : 0f,1f-Mathf.Exp(-dt/.15f));
            _model.transform.localRotation=Quaternion.Euler(0f,_bodyTurn,0f);
        }

        private RangedCombat _ranged;

        private void PresentBotCombat()
        {
            var weapon = _rig.ActiveWeapon;
            // The draw of a locally simulated body (a rival bot) comes straight from its ranged state; remote players get it
            // from the network pose. Without this a bot's bow never moved (user report, 25 Nov 2026).
            if (_ranged == null) _ranged = _rig.GetComponent<RangedCombat>();
            if (_weapons != null)
            {
                bool ranged = weapon != null && weapon.IsRanged && _ranged != null && !_rig.IsDead;
                _weapons.PresentCrossbowLoaded(weapon != null ? weapon.weaponId : "", ranged && _ranged.Magazine > 0);
                _weapons.PresentDraw(weapon != null ? weapon.weaponId : "", ranged && _ranged.IsDrawing, ranged ? Mathf.Clamp01(_ranged.Charge) : 0f);
            }
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
