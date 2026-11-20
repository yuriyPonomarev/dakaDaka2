using System.Collections.Generic;
using Emberdelve.Combat;
using Emberdelve.Data;
using UnityEngine;
namespace Emberdelve.Player
{
    [DefaultExecutionOrder(100)]
    public sealed class RemoteWeaponPresentation : MonoBehaviour
    {
        private PlayerRig _rig;
        private PotionDrinkPresentation _drink;
        private float _drinkRaise;
        private Transform _right, _left, _rightElbow, _leftElbow, _rightShoulder, _leftShoulder;
        private readonly List<BonePose> _modified = new List<BonePose>(6);
        private struct BonePose { public Transform Bone; public Vector3 Position; public Quaternion Rotation; }
        private void Remember(Transform bone)
        {
            if(bone!=null) _modified.Add(new BonePose { Bone=bone, Position=bone.localPosition, Rotation=bone.localRotation });
        }
        private void Update()
        {
            // Undo last frame's cosmetic IK before legacy Animation samples the next pose.
            foreach(var pose in _modified) if(pose.Bone!=null) { pose.Bone.localPosition=pose.Position; pose.Bone.localRotation=pose.Rotation; }
            _modified.Clear();
        }
        private GameObject _mainObject,_offObject,_utilityObject;
        private string _utilityId;
        public void PresentUtility(string itemId)
        {
            if (_utilityId == itemId && (string.IsNullOrEmpty(itemId) || _utilityObject != null)) return;
            _utilityId=itemId;
            if (_utilityObject != null) { _utilityObject.SetActive(false); Destroy(_utilityObject); }
            _utilityObject=HandheldUtilityVisual.Create(HandheldUtilityVisual.Find(itemId),transform);
        }
        private Renderer[] _mainRenderers,_offRenderers;
        private WeaponData _main,_off;
        private string _mainId,_offId,_patternId;
        private SwingPatternData _pattern;
        private bool _guard,_swing;
        private bool _drawing;
        private float _drawProgress;
        private bool _crossbowLoaded;
        public void PresentCrossbowLoaded(string weapon, bool loaded) =>
            _crossbowLoaded = loaded && weapon == _mainId && _main != null && _main.CanCrossbowGuard;
        public void PresentDraw(string weapon, bool drawing, float progress)
        {
            _drawing = drawing && weapon == _mainId && !_guard && !_swing && !_recoil
                && !float.IsNaN(progress) && !float.IsInfinity(progress) && progress >= 0f && progress <= 1f;
            _drawProgress = _drawing ? progress : 0f;
        }
        private bool _soundPlayed;
        private float _elapsed,_modelLength=1;
        private SwingTiming _timing;
        private SwingFrame _frame;
        private WeaponViewModelSet _models;
        private static readonly Dictionary<string,WeaponData> Weapons=new Dictionary<string,WeaponData>();
        public void Bind(PlayerRig rig,GameObject model)
        {
            _rig=rig; _drink=PotionDrinkPresentation.Ensure(rig); _models=WeaponViewModelSet.Load();
            _right=_left=_rightElbow=_leftElbow=_rightShoulder=_leftShoulder=null;
            var animator=model.GetComponentInChildren<Animator>();
            if(animator!=null && animator.isHuman)
            {
                _right=animator.GetBoneTransform(HumanBodyBones.RightHand); _left=animator.GetBoneTransform(HumanBodyBones.LeftHand);
                _rightElbow=animator.GetBoneTransform(HumanBodyBones.RightLowerArm); _leftElbow=animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
                _rightShoulder=animator.GetBoneTransform(HumanBodyBones.RightUpperArm); _leftShoulder=animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                return;
            }
            foreach(var bone in model.GetComponentsInChildren<Transform>())
            {
                if(bone.name=="wrist.r") _right=bone;
                if(bone.name=="wrist.l") _left=bone;
                if(bone.name=="upperarm.r") _rightShoulder=bone;
                if(bone.name=="upperarm.l") _leftShoulder=bone;
                if(bone.name=="lowerarm.r") _rightElbow=bone;
                if(bone.name=="lowerarm.l") _leftElbow=bone;
            }
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ClearCache() => Weapons.Clear();
        private static WeaponData Resolve(string id)
        {
            if(string.IsNullOrEmpty(id)) return null;
            if(Weapons.TryGetValue(id,out var found) && found!=null) return found;
            foreach(var weapon in Resources.FindObjectsOfTypeAll<WeaponData>()) Weapons[weapon.weaponId]=weapon;
            Weapons.TryGetValue(id,out found); return found;
        }
        private bool _recoil;
        private float _stopFraction, _recoilProgress;
        public void Present(string main,string off,string pattern,bool guard,bool swing,float elapsed,SwingTiming timing,SwingFrame frame,
            bool recoil=false,float stopFraction=0f,float recoilProgress=0f)
        {
            _recoil=recoil; _stopFraction=stopFraction; _recoilProgress=recoilProgress;
            if(main!=_mainId) { _mainId=main; _main=Resolve(main); Replace(ref _mainObject,ref _mainRenderers,_main,true); _patternId=null; }
            if(off!=_offId) { _offId=off; _off=Resolve(off); Replace(ref _offObject,ref _offRenderers,_off,false); }
            if(pattern!=_patternId)
            {
                _patternId=pattern; _pattern=null;
                if(_main!=null && _main.swingPatterns!=null) foreach(var candidate in _main.swingPatterns)
                    if(candidate!=null && candidate.patternId==pattern) { _pattern=candidate; break; }
            }
            if (!swing || !_swing || elapsed < _elapsed - .03f) _soundPlayed = false;
            if (swing && !_soundPlayed && elapsed >= timing.Windup && elapsed <= timing.ActiveEnd &&
                _rig != null && !_rig.IsLocallyControlled)
            {
                _soundPlayed = true;
                Emberdelve.Feedback.WorldAudio.Play(Emberdelve.Feedback.WorldSound.Swing, _rig.AimOrigin, .7f);
            }
            _guard=guard; _swing=swing && _pattern!=null; _elapsed=elapsed; _timing=timing; _frame=frame;
        }
        private void Replace(ref GameObject target,ref Renderer[] renderers,WeaponData weapon,bool main)
        {
            if(target!=null) { target.SetActive(false); Destroy(target); } target=null; renderers=null;
            if(weapon==null || weapon.isUnarmed) return;
            var entry=_models!=null ? _models.Find(weapon.weaponId) : null;
            if(entry!=null)
            {
                target=Instantiate(entry.prefab,transform);
                if(main) _modelLength=Mathf.Max(.01f,entry.length);
            }
            else
            {
                var visual=WeaponVisualBuilder.Build(weapon,transform);
                if(visual==null) return;
                visual.enabled=false; target=visual.gameObject; if(main) _modelLength=1;
            }
            target.name=main ? "RemoteMainHand" : "RemoteOffHand";
            foreach(var collider in target.GetComponentsInChildren<Collider>()) Destroy(collider);
            renderers=target.GetComponentsInChildren<Renderer>(true);
        }
        private static void Visibility(Renderer[] renderers,bool visible)
        {
            if(renderers==null) return;
            foreach(var renderer in renderers) if(renderer!=null) renderer.enabled=visible;
        }
        private void LateUpdate()
        {
            if(_rig==null) return;
            bool visible=!_rig.IsLocallyControlled && !_rig.IsExtracted && !_rig.IsDead;
            Visibility(_mainRenderers,visible); Visibility(_offRenderers,visible);
            // The owner's remote model must not leave a second point light/flame running.
            if (_mainObject != null && _main != null && _main.weaponId == HandheldTorchVisual.WeaponId)
                _mainObject.SetActive(visible);
            if (_utilityObject != null)
            {
                _utilityObject.SetActive(visible);
                if (visible)
                {
                    Vector3 grip=_rig.transform.TransformPoint(new Vector3(.24f,1.02f,.30f));
                    Quaternion rotation = _rig.transform.rotation;
                    var bottle = _utilityObject.GetComponent<PotionBottleVisual>();
                    if (bottle != null)
                    {
                        bool drinking = _drink != null && _drink.IsDrinking;
                        float progress = drinking ? _drink.Progress : 0f;
                        _drinkRaise = Mathf.MoveTowards(_drinkRaise, drinking ? PotionDrinkPresentation.Raise(progress) : 0f, Time.deltaTime * 7f);
                        // Bottle lip reaches the face while the base tips outwards.
                        grip = Vector3.Lerp(grip, _rig.AimOrigin + _rig.transform.TransformVector(new Vector3(.05f,-.17f,.25f)), _drinkRaise);
                        rotation *= Quaternion.Euler(-95f * _drinkRaise, 0f, -10f * _drinkRaise);
                        bottle.SetDrinkProgress(progress, drinking);
                        if (drinking && progress < .25f)
                            Reach(_leftShoulder,_leftElbow,_left,grip + rotation * new Vector3(0,.145f,0));
                    }
                    var bandage = _utilityObject.GetComponent<BandageVisual>();
                    if (bandage != null)
                    {
                        bool applying = _drink != null && _drink.IsDrinking;
                        float p = applying ? _drink.Progress : 0f;
                        _drinkRaise = Mathf.MoveTowards(_drinkRaise, applying ? PotionDrinkPresentation.Raise(p) : 0f, Time.deltaTime * 7f);
                        float turn = p * Mathf.PI * 12f;
                        Vector3 wrist = _rig.transform.TransformPoint(new Vector3(-.09f, 1.12f, .38f));
                        Vector3 wrap = wrist + _rig.transform.TransformVector(new Vector3(.075f + .055f * Mathf.Sin(turn), .035f * Mathf.Cos(turn), .015f));
                        grip = Vector3.Lerp(grip, wrap, _drinkRaise);
                        rotation *= Quaternion.Euler(0f, 0f, Mathf.Sin(turn) * 30f * _drinkRaise);
                        if (_drinkRaise > 0f) Reach(_leftShoulder,_leftElbow,_left,wrist);
                        bandage.SetApplyProgress(p, applying);
                    }
                    _utilityObject.transform.SetPositionAndRotation(bottle != null ? grip + rotation * new Vector3(0,-.055f,0) : grip,rotation);
                    Reach(_rightShoulder,_rightElbow,_right,grip);
                }
            }
            if(!visible)
            {
                _drawing=false; _drawProgress=0f;
                var hiddenBow=_mainObject!=null ? _mainObject.GetComponentInChildren<BowPresentation>() : null;
                if(hiddenBow!=null) hiddenBow.SetDraw(0f);
                return;
            }
            if(_mainObject!=null)
            {
                var t=_mainObject.transform;
                if(_guard && _main.CanBowGuard)
                {
                    BowGuardPose.ApplyModel(t,_frame.Eye,_frame.Rotation);
                    var bow=t.GetComponentInChildren<BowPresentation>();
                    if(bow!=null) bow.SetDraw(0f);
                    Reach(_leftShoulder,_leftElbow,_left,t.TransformPoint(BowGuardPose.LeftGrip));
                    Reach(_rightShoulder,_rightElbow,_right,t.TransformPoint(BowGuardPose.RightGrip));
                }
                else if(_main.CanBowGuard)
                {
                    t.localScale=Vector3.one;
                    t.SetPositionAndRotation(_frame.Eye+_frame.Rotation*new Vector3(-.18f,-.30f,.55f),
                        _frame.Rotation*Quaternion.Euler(0f,-8f,0f));
                    var bow=t.GetComponentInChildren<BowPresentation>();
                    if(bow!=null)
                    {
                        var ranged=_rig.GetComponent<RangedCombat>();
                        bool draw=_drawing && (ranged==null || !ranged.IsReloading);
                        bow.SetDraw(draw ? _drawProgress : 0f);
                        Reach(_leftShoulder,_leftElbow,_left,bow.transform.position);
                        Reach(_rightShoulder,_rightElbow,_right,bow.transform.TransformPoint(bow.Nock));
                    }
                }
                else if(_main.CanCrossbowGuard)
                {
                    if(_guard) CrossbowGuardPose.ApplyModel(t,_frame.Eye,_frame.Rotation);
                    else
                    {
                        t.localScale=Vector3.one;
                        // Both clients see the same ready weapon frame used by the authority's
                        // muzzle; the idle wrist must not drive a separate lower weapon pose.
                        t.SetPositionAndRotation(_frame.Eye+_frame.Rotation*CrossbowGuardPose.ReadyPosition,
                            _frame.Rotation*CrossbowGuardPose.ReadyRotation);
                    }
                    var ranged=_rig.GetComponent<RangedCombat>();
                    var crossbow = t.GetComponent<WeaponVisualRig>();
                    if (crossbow != null) crossbow.SetCrossbowState(_crossbowLoaded,
                        ranged != null && ranged.IsReloading, ranged != null ? ranged.ReloadProgress : 0f);
                    bool reload=!_guard && ranged!=null && ranged.IsReloading;
                    Reach(_rightShoulder,_rightElbow,_right,t.TransformPoint(FirstPersonArmsDriver.CrossbowRightGrip));
                    Reach(_leftShoulder,_leftElbow,_left,t.TransformPoint(FirstPersonArmsDriver.CrossbowLeftGrip(reload,ranged!=null ? ranged.ReloadProgress : 0f)));
                }
                else if(_recoil && _pattern!=null)
                {
                    WeaponVisualRig.SampleRecoil(_pattern,_stopFraction,_recoilProgress,_guard,out var position,out var rotation,_main!=null&&_main.twoHanded);
                    Vector3 start=_frame.Eye+_frame.Rotation*position;
                    t.SetPositionAndRotation(start,_frame.Rotation*rotation*Quaternion.Euler(90,0,0));
                    t.localScale=Vector3.one*(_pattern.BladeLength(_main)/_modelLength);
                    Reach(_rightShoulder,_rightElbow,_right,start);
                }
                else if(_swing && _elapsed<_timing.Total && _pattern!=null)
                {
                    var pose=_pattern.Evaluate(_elapsed,_timing);
                    SwingGeometry.BladeSegment(_frame,pose,_pattern.BladeLength(_main),out var start,out _);
                    t.SetPositionAndRotation(start,_frame.Rotation*pose.LocalRotation*Quaternion.Euler(90,0,0));
                    t.localScale=Vector3.one*(_pattern.BladeLength(_main)/_modelLength);
                    Reach(_rightShoulder,_rightElbow,_right,start);
                }
                else if(_guard && !_main.IsRanged)
                {
                    GuardBladePose.ResolveRoot(_frame.Eye,_frame.Rotation,true,out var position,out var rotation,_main.twoHanded);
                    t.SetPositionAndRotation(position,rotation*Quaternion.Euler(90,0,0));
                    t.localScale=Vector3.one*(GuardBladePose.BladeSizeFor(_main.twoHanded).z/_modelLength);
                    Reach(_rightShoulder,_rightElbow,_right,position);
                }
                else
                {
                    t.localScale=Vector3.one;
                    t.SetPositionAndRotation(_right!=null ? _right.position : _rig.transform.position+Vector3.up,
                        _rig.transform.rotation*((_main.IsRanged || _main.weaponId == HandheldTorchVisual.WeaponId) ? Quaternion.identity : Quaternion.Euler(150,0,-12)));
                }
            }
            if(_offObject!=null)
            {
                if(_guard && _off!=null && _off.IsShield)
                {
                    var shield=GuardShieldPose.Resolve(_frame.Eye,_frame.Rotation);
                    GuardShieldPose.ApplyModel(_offObject.transform,shield);
                    Reach(_leftShoulder,_leftElbow,_left,_frame.Eye+_frame.Rotation*GuardShieldPose.Grip);
                }
                else
                {
                    if(_off!=null && _off.IsShield) GuardShieldPose.SetWorldScale(_offObject.transform,GuardShieldPose.Scale);
                    else _offObject.transform.localScale=Vector3.one;
                    _offObject.transform.SetPositionAndRotation(_left!=null ? _left.position : _rig.transform.position+Vector3.up,
                        _rig.transform.rotation*Quaternion.Euler(0,-30,0));
                }
            }
        }
        private void Reach(Transform shoulder,Transform elbow,Transform wrist,Vector3 target)
        {
            if(wrist==null) return;
            Remember(shoulder); Remember(elbow); Remember(wrist);
            if(shoulder!=null && elbow!=null)
            {
                Vector3 direction=target-shoulder.position;
                float distance=direction.magnitude;
                float upper=Vector3.Distance(shoulder.position,elbow.position);
                float lower=Vector3.Distance(elbow.position,wrist.position);
                if(distance>.001f && upper>.001f && lower>.001f)
                {
                    direction/=distance;
                    Vector3 bend=Vector3.ProjectOnPlane(elbow.position-shoulder.position,direction);
                    if(bend.sqrMagnitude<.0001f) bend=Vector3.ProjectOnPlane(-_rig.transform.forward,direction);
                    if(bend.sqrMagnitude<.0001f) bend=Vector3.Cross(direction,Vector3.up);
                    float reach=Mathf.Clamp(distance,Mathf.Abs(upper-lower)+.001f,upper+lower-.001f);
                    float along=(upper*upper+reach*reach-lower*lower)/(2*reach);
                    Vector3 elbowTarget=shoulder.position+direction*along+bend.normalized*Mathf.Sqrt(Mathf.Max(0,upper*upper-along*along));
                    shoulder.rotation=Quaternion.FromToRotation(elbow.position-shoulder.position,elbowTarget-shoulder.position)*shoulder.rotation;
                    elbow.rotation=Quaternion.FromToRotation(wrist.position-elbow.position,target-elbow.position)*elbow.rotation;
                }
            }
            // Keep the grip on the actual hit segment; the replacement model can improve reach proportions.
            wrist.position=target;
        }
        private void OnDestroy() { if(_mainObject!=null) Destroy(_mainObject); if(_offObject!=null) Destroy(_offObject); if(_utilityObject!=null) Destroy(_utilityObject); }
    }
}
