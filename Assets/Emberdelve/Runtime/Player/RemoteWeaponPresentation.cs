using System.Collections.Generic;
using Emberdelve.Combat;
using Emberdelve.Data;
using Emberdelve.Level;
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
        private Transform _spine;
        private AttackTelegraphVisual _attackTell;
        private SwingPatternData _tellPattern;
        private readonly List<BonePose> _modified = new List<BonePose>(7);
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
        private Transform _body;
        public void Bind(PlayerRig rig,GameObject model)
        {
            _rig=rig; _body=rig.transform; _drink=PotionDrinkPresentation.Ensure(rig); BindBones(model);
        }
        /// <summary>A body without a rig (edit-mode review renders): the same weapon and arm posing, driven by <see cref="PoseNow"/>.</summary>
        public void BindBody(Transform body,GameObject model) { _rig=null; _body=body; _drink=null; BindBones(model); }
        /// <summary>Poses the weapons and arms immediately from the last <see cref="Present"/> (review renders; the game does it in LateUpdate).</summary>
        public void PoseNow() { TwistSpine(); PoseHeld(); }
        private static void Discard(Object o) { if(o==null) return; if(Application.isPlaying) Destroy(o); else DestroyImmediate(o); }
        private void BindBones(GameObject model)
        {
            _models=WeaponViewModelSet.Load();
            _right=_left=_rightElbow=_leftElbow=_rightShoulder=_leftShoulder=_spine=null;
            _index=_little=_middle=null; _holdWeight=0f; _swivelSet=false; _rightArm=default; _leftArm=default;
            var animator=model.GetComponentInChildren<Animator>();
            if(animator!=null && animator.isHuman)
            {
                _right=animator.GetBoneTransform(HumanBodyBones.RightHand); _left=animator.GetBoneTransform(HumanBodyBones.LeftHand);
                _rightElbow=animator.GetBoneTransform(HumanBodyBones.RightLowerArm); _leftElbow=animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
                _rightShoulder=animator.GetBoneTransform(HumanBodyBones.RightUpperArm); _leftShoulder=animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                _spine=animator.GetBoneTransform(HumanBodyBones.Chest) ?? animator.GetBoneTransform(HumanBodyBones.Spine);
                _index=animator.GetBoneTransform(HumanBodyBones.RightIndexProximal);
                _little=animator.GetBoneTransform(HumanBodyBones.RightLittleProximal);
                _middle=animator.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
                if(_index==null || _little==null || _middle==null) _index=_little=_middle=null;
                for(int finger=0;finger<4;finger++) for(int joint=0;joint<3;joint++)
                    _fingers[finger*3+joint]=animator.GetBoneTransform(HumanBodyBones.RightIndexProximal+finger*3+joint);
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
                if(bone.name=="spine03") _spine=bone;
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
        private bool _recoil, _bot;
        /// <summary>A melee weapon is in the main hand (not fists, a torch or a ranged weapon).</summary>
        public bool HoldsMelee => _main!=null && !_main.IsRanged && !_main.isUnarmed && _main.weaponId!=HandheldTorchVisual.WeaponId;
        public bool HoldsShield => _off!=null && _off.IsShield;
        private float _stopFraction, _recoilProgress;
        public void Present(string main,string off,string pattern,bool guard,bool swing,float elapsed,SwingTiming timing,SwingFrame frame,
            bool recoil=false,float stopFraction=0f,float recoilProgress=0f,bool bot=false)
        {
            _bot=bot;
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
                Emberdelve.Feedback.WorldAudio.PlayAttached(Emberdelve.Feedback.WorldAudio.SwingFor(_mainId), _rig.transform, Vector3.up * 1.4f, .7f);
            }
            _guard=guard; _swing=swing && _pattern!=null; _elapsed=elapsed; _timing=timing; _frame=frame;
        }
        private void Replace(ref GameObject target,ref Renderer[] renderers,WeaponData weapon,bool main)
        {
            if(target!=null) { target.SetActive(false); Discard(target); } target=null; renderers=null;
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
            foreach(var collider in target.GetComponentsInChildren<Collider>()) Discard(collider);
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
            bool visible=!_rig.IsLocallyControlled && !_rig.IsExtracted && !_rig.IsDead
                && !DungeonModuleSceneAssembler.IsPositionOnRetiredFloor(transform.position);
            UpdateBotSwing(visible && !_rig.IsInFloorTransit);
            Visibility(_mainRenderers,visible); Visibility(_offRenderers,visible);
            // The quiver belongs to a carried bow; PoseBow shows and places it.
            if(_quiver!=null && _quiver.activeSelf && !(visible && _main!=null && _main.CanBowGuard)) _quiver.SetActive(false);
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
            PoseHeld();
        }

        // ---- The arm as a skeleton, not two free bones ------------------------------------------------------------------------
        // The elbow is a hinge. Its axis in each bone's own space is a constant of the rig; it is read from the animated pose
        // (upper arm x forearm, once the elbow is visibly bent) and every posed arm is then built on that axis: the upper arm
        // rolls so that the forearm flexes the way it does in the animation, never sideways or backwards.
        private struct ArmRig { public bool Known; public float Bend; public Quaternion Upper, Lower; }
        private ArmRig _rightArm,_leftArm;
        private const float HingeSeenFrom=12f, HingeSureAt=60f;

        private static void LearnHinge(ref ArmRig rig,Transform shoulder,Transform elbow,Transform wrist)
        {
            if(shoulder==null || elbow==null || wrist==null || rig.Bend>=HingeSureAt) return;
            Vector3 upper=elbow.position-shoulder.position, lower=wrist.position-elbow.position;
            float bend=Vector3.Angle(upper,lower);
            if(bend<HingeSeenFrom || bend<=rig.Bend) return;
            Vector3 hinge=Vector3.Cross(upper,lower).normalized;
            rig.Upper=Quaternion.Inverse(Quaternion.LookRotation(upper.normalized,hinge))*shoulder.rotation;
            rig.Lower=Quaternion.Inverse(Quaternion.LookRotation(lower.normalized,hinge))*elbow.rotation;
            rig.Known=true; rig.Bend=bend;
        }

        // Places the two arm bones for an elbow at `elbowAt` and a wrist at `wristAt`; the elbow points along `bend`.
        private static void SetArm(in ArmRig rig,Transform shoulder,Transform elbow,Transform wrist,Vector3 elbowAt,Vector3 wristAt,Vector3 bend)
        {
            Vector3 upper=elbowAt-shoulder.position, lower=wristAt-elbowAt;
            Vector3 hinge=Vector3.Cross(upper,-bend);
            if(rig.Known && hinge.sqrMagnitude>1e-8f && upper.sqrMagnitude>1e-8f && lower.sqrMagnitude>1e-8f)
            {
                hinge.Normalize();
                shoulder.rotation=Quaternion.LookRotation(upper.normalized,hinge)*rig.Upper;
                elbow.rotation=Quaternion.LookRotation(lower.normalized,hinge)*rig.Lower;
                return;
            }
            shoulder.rotation=Quaternion.FromToRotation(elbow.position-shoulder.position,upper)*shoulder.rotation;
            elbow.rotation=Quaternion.FromToRotation(wrist.position-elbow.position,wristAt-elbow.position)*elbow.rotation;
        }

        private void PoseHeld()
        {
            LearnHinge(ref _rightArm,_rightShoulder,_rightElbow,_right);
            LearnHinge(ref _leftArm,_leftShoulder,_leftElbow,_left);
            if(_mainObject!=null)
            {
                var t=_mainObject.transform;
                if(_guard && _main.CanBowGuard)
                {
                    BowGuardPose.ApplyModel(t,_frame.Eye,_frame.Rotation);
                    var bow=t.GetComponentInChildren<BowPresentation>();
                    if(bow!=null) { bow.SetDraw(0f); bow.ShowArrow(false); }
                    PlaceQuiver();
                    HoldPoint(_leftShoulder,_leftElbow,_left,t.TransformPoint(BowGuardPose.LeftGrip),LeftElbowHint,1f);
                    HoldPoint(_rightShoulder,_rightElbow,_right,t.TransformPoint(BowGuardPose.RightGrip),RightElbowHint,1f);
                }
                else if(_main.CanBowGuard)
                {
                    PoseBow(t);
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
                    var ranged=_rig!=null ? _rig.GetComponent<RangedCombat>() : null;
                    var crossbow = t.GetComponent<WeaponVisualRig>();
                    if (crossbow != null) crossbow.SetCrossbowState(_crossbowLoaded,
                        ranged != null && ranged.IsReloading, ranged != null ? ranged.ReloadProgress : 0f);
                    bool reload=!_guard && ranged!=null && ranged.IsReloading;
                    // The arms keep their lengths: a grip out of reach is approached, the bones are never stretched to it.
                    HoldPoint(_rightShoulder,_rightElbow,_right,t.TransformPoint(FirstPersonArmsDriver.CrossbowRightGrip),RightElbowHint,1f);
                    HoldPoint(_leftShoulder,_leftElbow,_left,t.TransformPoint(FirstPersonArmsDriver.CrossbowLeftGrip(reload,ranged!=null ? ranged.ReloadProgress : 0f)),LeftElbowHint,1f);
                }
                else if(HasHandFrame && !_main.IsRanged && _main.weaponId!=HandheldTorchVisual.WeaponId)
                {
                    PoseMelee(t);
                }
                else if(_recoil && _pattern!=null)
                {
                    WeaponVisualRig.SampleRecoil(_pattern,_stopFraction,_recoilProgress,_guard,out var position,out var rotation,_main!=null&&_main.twoHanded);
                    Vector3 start=_frame.Eye+_frame.Rotation*position;
                    t.localScale=Vector3.one*(_pattern.BladeLength(_main)/_modelLength);
                    t.SetPositionAndRotation(ReachAndHold(start),_frame.Rotation*rotation*Quaternion.Euler(90,0,0));
                }
                else if(_swing && _elapsed<_timing.Total && _pattern!=null)
                {
                    var pose=_pattern.Evaluate(_elapsed,_timing);
                    SwingGeometry.BladeSegment(_frame,pose,_pattern.BladeLength(_main),out var start,out _);
                    t.localScale=Vector3.one*(_pattern.BladeLength(_main)/_modelLength);
                    t.SetPositionAndRotation(ReachAndHold(start),_frame.Rotation*pose.LocalRotation*Quaternion.Euler(90,0,0));
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
                    t.SetPositionAndRotation(_right!=null ? _right.position : _body.position+Vector3.up,
                        _body.rotation*((_main.IsRanged || _main.weaponId == HandheldTorchVisual.WeaponId) ? Quaternion.identity : Quaternion.Euler(150,0,-12)));
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
                    _offObject.transform.SetPositionAndRotation(_left!=null ? _left.position : _body.position+Vector3.up,
                        _body.rotation*Quaternion.Euler(0,-30,0));
                }
            }
        }

        // ---- A bow held by a body -------------------------------------------------------------------------------------------
        // Carried low in the bow hand. To shoot, the bow arm is raised and held out towards the target, the string hand takes
        // the string and draws it back to the face, the elbow out to the side and level with the shoulder.
        public static readonly Vector3 BowCarry=new Vector3(-.27f,-.60f,.28f), BowAim=new Vector3(-.05f,-.14f,.56f);
        public static readonly Vector3 BowCarryEuler=new Vector3(18f,-25f,-40f), BowAimEuler=new Vector3(0f,-5f,-8f);
        /// <summary>The pull of the string on a body (first person frames its own): straight back to the face.</summary>
        public const float BodyDrawSide=0f, BodyDrawDepth=.34f;
        /// <summary>Raising and lowering the bow (1/s), and how far behind a held point the wrist stays (m).</summary>
        public const float BowRaiseRate=5f, PalmBehind=.07f;
        /// <summary>How long the bow stays raised after a shot (seconds).</summary>
        public const float BowFollowThrough=.45f;
        private float _bowRaise,_lastDrawAt=float.NegativeInfinity;
        private Vector3 _releasePoint;
        // A quiver on the right hip of whoever carries a bow (body space), and where its mouth is (quiver space).
        public static readonly Vector3 QuiverPosition=new Vector3(.21f,.90f,-.03f), QuiverEuler=new Vector3(-16f,0f,-7f), QuiverMouth=new Vector3(0f,.20f,0f);
        private GameObject _quiver;

        private void PlaceQuiver()
        {
            if(_quiver==null)
            {
                _quiver=new GameObject("HipQuiver");
                _quiver.transform.SetParent(transform,false);
                QuiverPart("Case",new Vector3(0f,0f,0f),new Vector3(.075f,.36f,.075f),_main.gripMaterial);
                QuiverPart("Rim",new Vector3(0f,.175f,0f),new Vector3(.085f,.02f,.085f),_main.shaftMaterial);
                for(int i=0;i<4;i++)
                {
                    Vector3 at=new Vector3((i%2-.5f)*.03f,.24f,(i/2-.5f)*.03f);
                    QuiverPart("Shaft"+i,at,new Vector3(.008f,.12f,.008f),_main.shaftMaterial);
                    QuiverPart("Feather"+i,at+Vector3.up*.03f,new Vector3(.03f,.06f,.003f),_main.gripMaterial);
                }
            }
            if(!_quiver.activeSelf) _quiver.SetActive(true);
            _quiver.transform.SetPositionAndRotation(_body.TransformPoint(QuiverPosition),_body.rotation*Quaternion.Euler(QuiverEuler));
        }

        private void QuiverPart(string name,Vector3 position,Vector3 size,Material material)
        {
            var part=GameObject.CreatePrimitive(PrimitiveType.Cube); part.name=name;
            var collider=part.GetComponent<Collider>(); collider.enabled=false; Discard(collider);
            part.transform.SetParent(_quiver.transform,false); part.transform.localPosition=position; part.transform.localScale=size;
            part.layer=gameObject.layer;
            if(material!=null) part.GetComponent<Renderer>().sharedMaterial=material;
        }
        private static readonly Vector3 BowArmHint=new Vector3(-.5f,-1f,-.15f), DrawArmHint=new Vector3(1f,.1f,-.35f);

        private void PoseBow(Transform t)
        {
            var ranged=_rig!=null ? _rig.GetComponent<RangedCombat>() : null;
            bool draw=_drawing && (ranged==null || !ranged.IsReloading);
            // After the release the bow arm stays up for a moment (the string has snapped forward, the arrow is on its way).
            if(draw) _lastDrawAt=Time.time;
            bool up=draw || (Application.isPlaying && Time.time-_lastDrawAt<BowFollowThrough);
            _bowRaise=Application.isPlaying ? Mathf.MoveTowards(_bowRaise,up ? 1f : 0f,Time.deltaTime*BowRaiseRate) : (draw ? 1f : 0f);
            float raised=Mathf.SmoothStep(0f,1f,_bowRaise);
            t.localScale=Vector3.one;
            Quaternion rotation=_frame.Rotation*Quaternion.Slerp(Quaternion.Euler(BowCarryEuler),Quaternion.Euler(BowAimEuler),raised);
            Vector3 grip=_frame.Eye+_frame.Rotation*Vector3.Lerp(BowCarry,BowAim,raised);
            t.SetPositionAndRotation(grip,rotation);
            var bow=t.GetComponentInChildren<BowPresentation>();
            if(bow==null) return;
            t.position+=grip-bow.transform.position;   // the bow hand holds the grip of the bow, wherever the root of the model is
            bow.SetDrawShape(BodyDrawSide,BodyDrawDepth);
            float progress=draw ? _drawProgress : 0f;
            bow.SetDraw(BowPresentation.StringDraw(progress)*raised);
            Vector3 animatedHand=_right!=null ? _right.position : grip;
            HoldPoint(_leftShoulder,_leftElbow,_left,grip,BowArmHint,1f);
            PlaceQuiver();
            if(draw && progress<BowPresentation.ArrowNockedAt)
            {
                // The draw begins at the quiver: down for an arrow, up with it to the string.
                Vector3 mouth=_quiver.transform.TransformPoint(QuiverMouth), string0=bow.transform.TransformPoint(bow.Nock), target;
                if(progress<BowPresentation.ArrowTakenAt)
                {
                    target=Vector3.Lerp(animatedHand,mouth,Mathf.SmoothStep(0f,1f,progress/BowPresentation.ArrowTakenAt));
                    bow.ShowArrow(false);
                }
                else
                {
                    float k=Mathf.SmoothStep(0f,1f,(progress-BowPresentation.ArrowTakenAt)/(BowPresentation.ArrowNockedAt-BowPresentation.ArrowTakenAt));
                    target=Vector3.Lerp(mouth,string0,k)+_body.right*(.10f*Mathf.Sin(Mathf.PI*k));
                    bow.PlaceArrow(target,Quaternion.Slerp(_quiver.transform.rotation*Quaternion.LookRotation(Vector3.down,Vector3.forward),bow.ArrowOnStringRotation,k*k));
                }
                _releasePoint=_body.InverseTransformPoint(target);
                HoldPoint(_rightShoulder,_rightElbow,_right,target,progress<BowPresentation.ArrowTakenAt ? RightElbowHint : DrawArmHint,1f);
                return;
            }
            bow.ShowArrow(draw);
            // The string hand lets go with the shot: it stays where the string was released while the bow is still up.
            Vector3 nock=bow.transform.TransformPoint(bow.Nock);
            if(draw) _releasePoint=_body.InverseTransformPoint(nock);
            HoldPoint(_rightShoulder,_rightElbow,_right,draw ? nock : _body.TransformPoint(_releasePoint),DrawArmHint,raised);
        }

        // The hand (not the wrist) on a point: the wrist stays a palm behind it, the bones keep their lengths.
        private void HoldPoint(Transform shoulder,Transform elbow,Transform wrist,Vector3 point,Vector3 hint,float weight)
        {
            if(shoulder==null || elbow==null || wrist==null || weight<=0f) return;
            Remember(shoulder); Remember(elbow); Remember(wrist);
            Vector3 toward=point-shoulder.position;
            Vector3 target=point-toward.normalized*PalmBehind;
            if(weight<1f) target=Vector3.Lerp(wrist.position,target,weight);
            ReachArm(shoulder,elbow,wrist,target,_body.TransformDirection(hint));
        }

        private void UpdateBotSwing(bool visible)
        {
            bool posing = visible && _bot && _pattern != null && (_recoil || (_swing && _elapsed < _timing.Total));
            bool warning = posing && _swing && _elapsed < _timing.Windup;
            if (warning && _tellPattern != _pattern)
            {
                if (_attackTell != null)
                {
                    _attackTell.gameObject.SetActive(false);
                    Destroy(_attackTell.gameObject);
                }
                _pattern.YawRange(out float minYaw, out float maxYaw);
                _attackTell = AttackTelegraphVisual.Create(transform, minYaw, maxYaw, new Color(1f, .32f, .18f));
                _tellPattern = _pattern;
            }
            if (_attackTell != null)
                _attackTell.SetState(warning, warning ? _elapsed / _timing.Windup : 0f, _frame.Rotation * Vector3.forward,
                    warning ? _pattern.EffectiveReach(_main) : 0f, false);

            if (posing) TwistSpine();
        }

        // The torso follows the strike: a part of the blade's turn and pitch away from its rest pose.
        private void TwistSpine()
        {
            bool posing = _bot && _pattern != null && (_recoil || (_swing && _elapsed < _timing.Total));
            if (!posing || _spine == null) return;
            SwingPose rest = _pattern.RestPose;
            SwingPose swing = _recoil
                ? SwingPose.Lerp(_pattern.ActivePose(_stopFraction), rest, _recoilProgress)
                : _pattern.Evaluate(_elapsed, _timing);
            float yaw = Mathf.Clamp((swing.Yaw - rest.Yaw) * .3f, -35f, 35f);
            float pitch = Mathf.Clamp((swing.Pitch - rest.Pitch) * .25f, -25f, 25f);
            Remember(_spine);
            Quaternion facing = _body.rotation;
            _spine.rotation = facing * Quaternion.Euler(pitch, yaw, 0f) * Quaternion.Inverse(facing) * _spine.rotation;
        }
        // ---- A melee weapon held by a humanoid body -------------------------------------------------------------------------
        // The weapon is always drawn in the hand (the hand's own frame: blade out of the thumb side of the fist). A strike, its
        // recoil and a guard only tell the right arm where to carry it and which way the blade points; the arm blends into and
        // out of that over a few frames, so nothing snaps between the carried pose and a strike.
        private Transform _index,_little,_middle;
        private float _holdWeight;
        private Vector3 _holdGrip,_holdBlade;   // body space, kept while the arm blends back out
        private float _holdReach=.8f,_holdScale=1f;
        private bool HasHandFrame => _index!=null && _right!=null && _rightElbow!=null && _rightShoulder!=null;
        private static readonly Vector3 RightElbowHint=new Vector3(.6f,-1f,-.35f), LeftElbowHint=new Vector3(-.6f,-1f,-.35f);
        /// <summary>Speed (1/s) at which the arm takes up and gives up a strike or guard pose.</summary>
        public const float HoldBlendIn=14f, HoldBlendOut=7f;

        private readonly Transform[] _fingers=new Transform[12];   // right index, middle, ring, little: proximal, intermediate, distal
        private static readonly float[] FistCurl={ 65f, 150f, 215f };
        // Palm side of a right hand: fingers x knuckle line (index minus little), whatever the rig's bone axes are.
        private Vector3 PalmNormal => Vector3.Cross(_middle.position-_right.position,KnuckleAxis).normalized;
        private Vector3 PalmCentre => Vector3.Lerp(_right.position,_middle.position,.8f)+PalmNormal*.025f;

        // Closes the right hand's fingers around the handle: every segment is turned from the hand's finger direction towards the palm.
        private void CloseFist()
        {
            Vector3 along=Vector3.ProjectOnPlane(_middle.position-_right.position,KnuckleAxis).normalized;
            Vector3 palm=PalmNormal;
            for(int finger=0;finger<4;finger++)
            {
                for(int joint=0;joint<3;joint++)
                {
                    Transform bone=_fingers[finger*3+joint];
                    if(bone==null) break;
                    Transform next=joint<2 ? _fingers[finger*3+joint+1] : (bone.childCount>0 ? bone.GetChild(0) : null);
                    Remember(bone);
                    float angle=FistCurl[joint]*Mathf.Deg2Rad;
                    Vector3 wanted=along*Mathf.Cos(angle)+palm*Mathf.Sin(angle);
                    Vector3 current=next!=null ? next.position-bone.position : Vector3.zero;
                    if(current.sqrMagnitude<1e-10f)
                    {
                        // No tip bone: carry the previous segment's turn on.
                        if(joint>0 && bone.parent!=null) bone.localRotation=Quaternion.identity;
                        continue;
                    }
                    bone.rotation=Quaternion.FromToRotation(current,wanted)*bone.rotation;
                }
            }
        }
        private Vector3 KnuckleAxis => (_index.position-_little.position).normalized;

        /// <summary>How far the arm is extended (fraction of its length) through a strike: drawn back in the wind-up, thrown out in the strike, eased back after it.</summary>
        public static readonly Vector3 GuardGrip=new Vector3(.17f,-.36f,.38f), GuardBlade=new Vector3(-.50f,.80f,.30f);
        public static readonly Vector3 TwoHandedGuardGrip=new Vector3(.08f,-.42f,.36f), TwoHandedGuardBlade=new Vector3(-.36f,.90f,.24f);

        public static float StrikeReach(float elapsed,SwingTiming timing)
        {
            float windup=Mathf.Max(.01f,timing.Windup), end=timing.ActiveEnd;
            if(elapsed<timing.Windup) return Mathf.Lerp(.72f,.58f,Mathf.SmoothStep(0f,1f,elapsed/windup));
            if(elapsed<end) return Mathf.Lerp(.58f,.95f,Mathf.SmoothStep(0f,1f,(elapsed-timing.Windup)/Mathf.Max(.01f,timing.Active*.6f)));
            return Mathf.Lerp(.95f,.72f,Mathf.SmoothStep(0f,1f,(elapsed-end)/Mathf.Max(.01f,timing.Total-end)));
        }

        private void PoseMelee(Transform t)
        {
            bool carried=false, thrusting=false; Vector3 grip=default, blade=default; float reach=.8f, scale=1f, carriedWeight=1f;
            if(_recoil && _pattern!=null)
            {
                WeaponVisualRig.SampleRecoil(_pattern,_stopFraction,_recoilProgress,_guard,out var position,out var rotation,_main.twoHanded);
                grip=_frame.Eye+_frame.Rotation*position; blade=_frame.Rotation*rotation*Vector3.forward;
                reach=Mathf.Lerp(.9f,.7f,_recoilProgress); scale=_pattern.BladeLength(_main)/_modelLength; carried=true;
            }
            else if(_swing && _elapsed<_timing.Total && _pattern!=null)
            {
                scale=_pattern.BladeLength(_main)/_modelLength; carried=true;
                if(SwingMotion.TrySample(_pattern,_timing,_elapsed,SwingMotion.For(true,_main.twoHanded),out var shownHand,out var shownBlade,out var shownTravel,out float shown))
                {
                    thrusting=shownTravel.sqrMagnitude<.01f;
                    // A cut is shown the way the player's own arms show it: drawn back, swept across with the blade trailing, followed through.
                    grip=_frame.Eye+_frame.Rotation*shownHand; blade=_frame.Rotation*shownBlade; reach=.97f; carriedWeight=shown;
                }
                else
                {
                    var pose=_pattern.Evaluate(_elapsed,_timing);
                    SwingGeometry.BladeSegment(_frame,pose,_pattern.BladeLength(_main),out grip,out _);
                    blade=_frame.Rotation*pose.LocalRotation*Vector3.forward;
                    reach=StrikeReach(_elapsed,_timing);
                }
            }
            else if(_guard && !HoldsShield)   // behind a shield the weapon arm keeps its carried stance: the shield is the guard
            {
                // Shown guard: the hands in front of the chest, the blade up and across the body. The block volume is the
                // authority's own (laid out for the defender's view, above an arm's natural reach); a body keeps a human stance.
                grip=_frame.Eye+_frame.Rotation*(_main.twoHanded ? TwoHandedGuardGrip : GuardGrip);
                blade=_frame.Rotation*(_main.twoHanded ? TwoHandedGuardBlade : GuardBlade).normalized; reach=.92f;
                scale=GuardBladePose.BladeSizeFor(_main.twoHanded).z/_modelLength; carried=true;
            }
            if(carried)
            {
                _holdGrip=_body.InverseTransformPoint(grip); _holdBlade=_body.InverseTransformDirection(blade);
                _holdReach=reach; _holdScale=scale;
            }
            float holdTarget=carried ? carriedWeight : 0f;
            _holdWeight=!Application.isPlaying ? holdTarget
                : Mathf.MoveTowards(_holdWeight,holdTarget,Time.deltaTime*(holdTarget>=_holdWeight ? HoldBlendIn : HoldBlendOut));
            if(_holdWeight<=0f) _swivelSet=false;
            // A thrust is made with the handle laid along the palm (the blade continues the forearm); a cut with it across the palm.
            if(carried) _cantWanted=thrusting ? ThrustCant : GripCant;
            float cantTarget=Mathf.Lerp(GripCant,_cantWanted,_holdWeight);
            _cant=Application.isPlaying ? Mathf.MoveTowards(_cant,cantTarget,Time.deltaTime*CantRate) : cantTarget;

            if(_holdWeight>0f)
            {
                Remember(_rightShoulder); Remember(_rightElbow); Remember(_right);
                Quaternion shoulder=_rightShoulder.localRotation, elbow=_rightElbow.localRotation, hand=_right.localRotation;
                CarryTo(_body.TransformPoint(_holdGrip),_body.TransformDirection(_holdBlade).normalized,_holdReach);
                if(_holdWeight<1f)
                {
                    float w=Mathf.SmoothStep(0f,1f,_holdWeight);
                    _rightShoulder.localRotation=Quaternion.Slerp(shoulder,_rightShoulder.localRotation,w);
                    _rightElbow.localRotation=Quaternion.Slerp(elbow,_rightElbow.localRotation,w);
                    _right.localRotation=Quaternion.Slerp(hand,_right.localRotation,w);
                }
            }

            CloseFist();
            Vector3 knuckles=KnuckleAxis;
            Vector3 fingers=Vector3.ProjectOnPlane(_middle.position-_right.position,knuckles).normalized;
            t.localScale=Vector3.one*Mathf.Lerp(1f,_holdScale,_holdWeight);
            // Blade out of the thumb side of the fist, leaning towards the fingers by the grip's cant, its edge towards the knuckles.
            Vector3 along=BladeInHand(knuckles,fingers);
            t.SetPositionAndRotation(PalmCentre,Quaternion.LookRotation(along,Vector3.Cross(along,fingers))*Quaternion.Euler(90,0,0));

            if(_main.twoHanded && _left!=null && _leftElbow!=null && _leftShoulder!=null)
            {
                Remember(_leftShoulder); Remember(_leftElbow); Remember(_left);
                ReachArm(_leftShoulder,_leftElbow,_left,PalmCentre-along*.15f,_body.TransformDirection(LeftElbowHint));
            }
        }

        /// <summary>Carries the fist's centre towards <paramref name="grip"/> (as far as <paramref name="reach"/> of the arm allows) with the blade along <paramref name="blade"/>.</summary>
        private void CarryTo(Vector3 grip,Vector3 blade,float reach)
        {
            Vector3 shoulder=_rightShoulder.position;
            float arm=Vector3.Distance(shoulder,_rightElbow.position)+Vector3.Distance(_rightElbow.position,_right.position);
            Vector3 toward=grip-shoulder;
            float distance=toward.magnitude;
            Vector3 target=distance>1e-4f ? shoulder+toward/distance*Mathf.Min(distance,arm*reach) : grip;
            float upper=Vector3.Distance(shoulder,_rightElbow.position), lower=Vector3.Distance(_rightElbow.position,_right.position);

            // Where the fist's centre sits from the wrist, in the hand's own frame (fingers, knuckle line, palm).
            Vector3 k=KnuckleAxis, f=Vector3.ProjectOnPlane(_middle.position-_right.position,k).normalized, n=Vector3.Cross(f,k);
            Vector3 offset=PalmCentre-_right.position;
            Vector3 palm=new Vector3(Vector3.Dot(offset,f),Vector3.Dot(offset,k),Vector3.Dot(offset,n));

            // The elbow's swing about the shoulder-wrist line is free: take the one that asks least of the forearm and the wrist.
            Vector3 hint=_body.TransformDirection(RightElbowHint), outward=_body.right;
            float best=0f, bestCost=float.MaxValue;
            for(int i=0;i<=18;i++)
            {
                float swing=-90f+i*10f;
                float cost=SolveWrist(shoulder,target,upper,lower,hint,outward,swing,blade,out _,out _,out _,out _);
                if(cost<bestCost) { bestCost=cost; best=swing; }
            }
            _swivel=Application.isPlaying && _swivelSet ? Mathf.MoveTowards(_swivel,best,Time.deltaTime*SwivelRate) : best;
            _swivelSet=true;

            Vector3 wristTarget=target, fingers=f, knuckles=k, bend=hint; float twist=0f;
            for(int pass=0;pass<2;pass++)
            {
                SolveWrist(shoulder,wristTarget,upper,lower,hint,outward,_swivel,blade,out fingers,out knuckles,out bend,out twist);
                Vector3 normal=Vector3.Cross(fingers,knuckles);
                wristTarget=target-(fingers*palm.x+knuckles*palm.y+normal*palm.z);
            }
            ReachArm(_rightShoulder,_rightElbow,_right,wristTarget,bend);
            // Half of the turn is the forearm's own (pronation / supination), the rest shows at the wrist.
            Vector3 forearm=(_right.position-_rightElbow.position).normalized;
            _rightElbow.rotation=Quaternion.AngleAxis(twist*.5f,forearm)*_rightElbow.rotation;
            Vector3 nowK=KnuckleAxis, nowF=Vector3.ProjectOnPlane(_middle.position-_right.position,nowK);
            if(nowF.sqrMagnitude<1e-8f) return;
            _right.rotation=Quaternion.LookRotation(knuckles,fingers)*Quaternion.Inverse(Quaternion.LookRotation(nowK,nowF.normalized))*_right.rotation;
        }

        // ---- Joint limits of the weapon arm (degrees) -------------------------------------------------------------------------
        /// <summary>The handle lies across the palm: the blade leans from the knuckle line towards the fingers by this much.</summary>
        public const float GripCant=20f;
        /// <summary>The same lean in a thrust (the handle along the palm), and how fast the grip shifts (degrees per second).</summary>
        public const float ThrustCant=50f, CantRate=240f;
        private float _cant=GripCant, _cantWanted=GripCant;
        /// <summary>Sideways bend of the wrist: towards the little finger (blade forward) and towards the thumb (blade back).</summary>
        public const float WristUlnar=40f, WristRadial=20f;
        /// <summary>Turn of the forearm either way from thumb-up (palm down / palm up).</summary>
        public const float ForearmTwist=80f;
        /// <summary>Turn of the upper arm about its own length: forearm swung outward / inward across the body.</summary>
        public const float ShoulderOutward=85f, ShoulderInward=65f;
        /// <summary>Degrees per second the elbow may swing to a better position.</summary>
        public const float SwivelRate=360f;
        private float _swivel; private bool _swivelSet;

        private Vector3 BladeInHand(Vector3 knuckles,Vector3 fingers) =>
            (knuckles*Mathf.Cos(_cant*Mathf.Deg2Rad)+fingers*Mathf.Sin(_cant*Mathf.Deg2Rad)).normalized;

        /// <summary>
        /// The hand a human arm can give a blade direction with the elbow swung by <paramref name="swing"/> degrees about the
        /// shoulder-wrist line. The wrist does not twist: the forearm turns (thumb-up is neutral, the knuckle line towards the
        /// shoulder) by at most <see cref="ForearmTwist"/>, and the wrist bends sideways within <see cref="WristRadial"/> and
        /// <see cref="WristUlnar"/>. What the limits cannot give is left out: the blade then points where the hand can hold it.
        /// Returns how much was asked beyond the limits (plus a small price for effort), for choosing the elbow's swing.
        /// </summary>
        private float SolveWrist(Vector3 shoulder,Vector3 wrist,float upper,float lower,Vector3 hint,Vector3 outward,float swing,Vector3 blade,
            out Vector3 fingers,out Vector3 knuckles,out Vector3 bend,out float twist)
        {
            fingers=knuckles=bend=Vector3.zero; twist=0f;
            Vector3 line=wrist-shoulder;
            float distance=line.magnitude;
            if(distance<1e-4f) return float.MaxValue;
            line/=distance;
            float reach=Mathf.Clamp(distance,Mathf.Abs(upper-lower)+.001f,upper+lower-.001f);
            bend=Vector3.ProjectOnPlane(hint,line);
            if(bend.sqrMagnitude<1e-4f) bend=Vector3.ProjectOnPlane(-_body.forward,line);
            bend=Quaternion.AngleAxis(swing,line)*bend.normalized;
            float along=(upper*upper+reach*reach-lower*lower)/(2f*reach);
            Vector3 elbow=shoulder+line*along+bend*Mathf.Sqrt(Mathf.Max(0f,upper*upper-along*along));
            Vector3 forearm=(shoulder+line*reach-elbow).normalized;
            Vector3 thumbUp=Vector3.ProjectOnPlane(shoulder-elbow,forearm);
            if(thumbUp.sqrMagnitude<1e-4f) thumbUp=Vector3.ProjectOnPlane(-bend,forearm);
            thumbUp.Normalize();

            float bendWanted=90f-_cant-Vector3.Angle(forearm,blade);
            float sideways=Mathf.Clamp(bendWanted,-WristRadial,WristUlnar);
            Vector3 across=Vector3.ProjectOnPlane(blade,forearm);
            float twistWanted=across.sqrMagnitude>1e-4f ? Vector3.SignedAngle(thumbUp,across,forearm) : 0f;
            twist=Mathf.Clamp(twistWanted,-ForearmTwist,ForearmTwist);

            Vector3 neutral=Quaternion.AngleAxis(twist,forearm)*thumbUp;
            float c=Mathf.Cos(sideways*Mathf.Deg2Rad), s=Mathf.Sin(sideways*Mathf.Deg2Rad);
            fingers=(forearm*c-neutral*s).normalized;
            knuckles=(neutral*c+forearm*s).normalized;

            // The upper arm's own turn that this elbow position needs, from the neutral where a hanging arm flexes forward.
            Vector3 upperArm=(elbow-shoulder).normalized;
            Vector3 hinge=Vector3.Cross(upperArm,-bend), neutralHinge=Quaternion.FromToRotation(-_body.up,upperArm)*-outward;
            float roll=hinge.sqrMagnitude>1e-6f ? Vector3.SignedAngle(neutralHinge,hinge,upperArm) : 0f;
            float rollOver=roll<-ShoulderOutward ? -ShoulderOutward-roll : roll>ShoulderInward ? roll-ShoulderInward : 0f;

            float over=(bendWanted-sideways)*(bendWanted-sideways)+(twistWanted-twist)*(twistWanted-twist)+4f*rollOver*rollOver;
            float effort=.04f*twist*twist+.12f*swing*swing;
            return over+effort+(Vector3.Dot(bend,outward)<-.25f ? 1e6f : 0f);   // the elbow does not cross in front of the chest
        }

        // Two-bone reach with the elbow towards `bend` (world). Bone lengths are kept: an unreachable target is approached, never stretched to.
        private void ReachArm(Transform shoulder,Transform elbow,Transform wrist,Vector3 target,Vector3 bendDirection)
        {
            Vector3 direction=target-shoulder.position;
            float distance=direction.magnitude;
            float upper=Vector3.Distance(shoulder.position,elbow.position);
            float lower=Vector3.Distance(elbow.position,wrist.position);
            if(distance<.001f || upper<.001f || lower<.001f) return;
            direction/=distance;
            Vector3 bend=Vector3.ProjectOnPlane(bendDirection,direction);
            if(bend.sqrMagnitude<.0001f) bend=Vector3.ProjectOnPlane(-_body.forward,direction);
            float reach=Mathf.Clamp(distance,Mathf.Abs(upper-lower)+.001f,upper+lower-.001f);
            float along=(upper*upper+reach*reach-lower*lower)/(2*reach);
            Vector3 elbowTarget=shoulder.position+direction*along+bend.normalized*Mathf.Sqrt(Mathf.Max(0,upper*upper-along*along));
            SetArm(shoulder==_rightShoulder ? _rightArm : _leftArm,shoulder,elbow,wrist,elbowTarget,shoulder.position+direction*reach,bend.normalized);
        }

        /// <summary>
        /// The damage segment of a swing starts further from the body than an arm is long (it is laid out for the first-person
        /// view). The right arm reaches towards it only as far as it can with a slightly bent elbow, and the weapon is held in
        /// that hand: the blade keeps the swing's direction, the hand keeps the weapon. Returns where the weapon's grip goes.
        /// </summary>
        private Vector3 ReachAndHold(Vector3 segmentStart)
        {
            if(_rightShoulder==null || _rightElbow==null || _right==null) return segmentStart;
            float arm=Vector3.Distance(_rightShoulder.position,_rightElbow.position)+Vector3.Distance(_rightElbow.position,_right.position);
            Vector3 toward=segmentStart-_rightShoulder.position;
            float reach=Mathf.Min(toward.magnitude,arm*.94f);
            Vector3 grip=toward.sqrMagnitude>1e-6f ? _rightShoulder.position+toward.normalized*reach : segmentStart;
            Reach(_rightShoulder,_rightElbow,_right,grip);
            return _right.position;
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
                    if(bend.sqrMagnitude<.0001f) bend=Vector3.ProjectOnPlane(-_body.forward,direction);
                    if(bend.sqrMagnitude<.0001f) bend=Vector3.Cross(direction,Vector3.up);
                    float reach=Mathf.Clamp(distance,Mathf.Abs(upper-lower)+.001f,upper+lower-.001f);
                    float along=(upper*upper+reach*reach-lower*lower)/(2*reach);
                    Vector3 elbowTarget=shoulder.position+direction*along+bend.normalized*Mathf.Sqrt(Mathf.Max(0,upper*upper-along*along));
                    SetArm(shoulder==_rightShoulder ? _rightArm : _leftArm,shoulder,elbow,wrist,elbowTarget,shoulder.position+direction*reach,bend.normalized);
                }
            }
            // Keep the grip on the actual hit segment; the replacement model can improve reach proportions.
            wrist.position=target;
        }
        private void OnDestroy() { if(_quiver!=null) Discard(_quiver); if(_mainObject!=null) Destroy(_mainObject); if(_offObject!=null) Destroy(_offObject); if(_utilityObject!=null) Destroy(_utilityObject); }
    }
}
