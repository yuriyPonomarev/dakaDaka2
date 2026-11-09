using UnityEngine;
using Emberdelve.Data;
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
            _lastPosition=transform.position; _state=null;
        }
        private void LateUpdate()
        {
            RefreshModel(); if(_model==null) return;
            bool visible=!_rig.IsLocallyControlled && !_rig.IsExtracted;
            foreach(var renderer in _renderers) if(renderer.enabled!=visible) renderer.enabled=visible;
            Vector3 delta=transform.position-_lastPosition; _lastPosition=transform.position; delta.y=0;
            float speed=Time.deltaTime>0 ? delta.magnitude/Time.deltaTime : 0;
            string next=_rig.IsDead ? "Death" : _rig.IsGuarding ? "Block" : speed>3.8f ? "Run" : speed>.15f ? "Walk" : "Idle";
            if(_animation!=null && next!=_state) { _state=next; _animation.CrossFade(next,.15f); }
        }
    }
}
