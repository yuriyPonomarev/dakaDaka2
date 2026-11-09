using UnityEngine;
using Emberdelve.Data;
using Emberdelve.Combat;
namespace Emberdelve.Player
{
    // Cosmetic only: follows the existing rig and cannot move it or add hit shapes.
    public sealed class PlayerAvatar : MonoBehaviour
    {
        private PlayerRig _rig;
        private CharacterClassData _class;
        private GameObject _model;
        private Animation _animation;
        private Renderer[] _renderers;
        private Vector3 _lastPosition;
        private string _state;
        private RemoteWeaponPresentation _weapons;
        private bool _remoteGuard,_remoteSwing;
        public void PresentCombat(string main,string off,string pattern,bool guard,bool swing,float elapsed,SwingTiming timing,SwingFrame frame)
        {
            _remoteGuard=guard; _remoteSwing=swing;
            if(_weapons!=null) _weapons.Present(main,off,pattern,guard,swing,elapsed,timing,frame);
        }
        public static void Bind(PlayerRig rig)
        {
            var view=rig.GetComponent<PlayerAvatar>(); if(view==null) view=rig.gameObject.AddComponent<PlayerAvatar>();
            view._rig=rig; view.RefreshModel();
        }
        private void RefreshModel()
        {
            if(_rig==null || _class==_rig.Class) return;
            _class=_rig.Class; if(_model!=null) Destroy(_model);
            if(_class==null || _class.characterVisual==null) return;
            _model=Instantiate(_class.characterVisual,transform); _model.name="ClassAvatar";
            _model.transform.localPosition=Vector3.zero; _model.transform.localRotation=Quaternion.identity;
            _animation=_model.GetComponentInChildren<Animation>(); _renderers=_model.GetComponentsInChildren<Renderer>();
            if(_weapons==null) _weapons=gameObject.AddComponent<RemoteWeaponPresentation>();
            _weapons.Bind(_rig,_model);
            _lastPosition=transform.position; _state=null;
        }
        private void LateUpdate()
        {
            RefreshModel(); if(_model==null) return;
            bool visible=!_rig.IsLocallyControlled && !_rig.IsExtracted;
            foreach(var renderer in _renderers) if(renderer.enabled!=visible) renderer.enabled=visible;
            Vector3 delta=transform.position-_lastPosition; _lastPosition=transform.position; delta.y=0;
            float speed=Time.deltaTime>0 ? delta.magnitude/Time.deltaTime : 0;
            string next=_rig.IsDead ? "Death" : (_remoteGuard || _rig.IsGuarding) ? "Block" : _remoteSwing ? "Idle" : speed>3.8f ? "Run" : speed>.15f ? "Walk" : "Idle";
            if(_animation!=null && next!=_state) { _state=next; _animation.CrossFade(next,.15f); }
        }
    }
}
