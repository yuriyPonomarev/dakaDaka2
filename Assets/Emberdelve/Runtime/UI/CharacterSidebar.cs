using System.Collections.Generic;
using System.Text;
using Emberdelve.Data;
using Emberdelve.Core;
using Emberdelve.Skills;
using Emberdelve.Player;
using Emberdelve.Stats;
using UnityEngine;

namespace Emberdelve.UI
{
    public interface ICharacterManaView
    {
        bool HasCharacterMana { get; }
        float CharacterMana { get; }
        float CharacterMaxMana { get; }
    }

    // Read-only character summary. Preview is a separate cosmetic actor, never a copy of the live rig.
    public sealed class CharacterSidebar : MonoBehaviour
    {
        private UnityEngine.UI.Text _title, _stats;
        private readonly UnityEngine.UI.Text[] _skills = new UnityEngine.UI.Text[4];
        private readonly StringBuilder _text = new StringBuilder(512);
        private GameObject _stage;
        private RenderTexture _texture;
        private Material _material;
        private PlayerRig _rig;
        private CharacterClassData _previewClass;
        private UnityEngine.UI.RawImage _portrait;
        private readonly UnityEngine.UI.Image[] _icons = new UnityEngine.UI.Image[4];
        private float _refreshAt;
        private readonly StatsComponent _displayAttributes = new StatsComponent();
        private CharacterClassData _displayClass;
        private static readonly string[] AttributeNames={"Strength","Dexterity","Constitution","Intelligence","Wisdom","Charisma"};

        public void Build()
        {
            var panel = UiFactory.CreateImage(transform, "CharacterPanel", LootPanelStyle.Ink,
                new Vector2(360,650),Vector2.zero,Vector2.one*.5f,Vector2.one*.5f);
            LootPanelStyle.Frame(panel,new Color(.48f,.47f,.42f));
            _title=Label("Class", "CHARACTER", 20, new Vector2(0,296),new Vector2(330,30));
            var portrait = new GameObject("CharacterPreview",typeof(RectTransform),typeof(UnityEngine.UI.RawImage)).GetComponent<UnityEngine.UI.RawImage>();
            portrait.transform.SetParent(transform,false);
            portrait.rectTransform.sizeDelta=new Vector2(220,250);
            portrait.rectTransform.anchoredPosition=new Vector2(0,153);
            portrait.raycastTarget=false;
            _portrait=portrait;
            BuildPreview(portrait);
            Label("ActiveHeading","ACTIVE",12,new Vector2(0,12),new Vector2(320,20));
            Label("PassiveHeading","PASSIVE",12,new Vector2(0,-77),new Vector2(320,20));
            for(int i=0;i<4;i++)
            {
                float x=i%2==0 ? -84 : 84, y=i<2 ? -31 : -120;
                var card=UiFactory.CreateImage(transform,"SelectedSkill"+i,new Color(.11f,.105f,.085f),new Vector2(160,60),new Vector2(x,y),Vector2.one*.5f,Vector2.one*.5f);
                card.raycastTarget=false; LootPanelStyle.Frame(card,new Color(.27f,.25f,.19f));
                _icons[i]=UiFactory.CreateImage(transform,"SkillIcon"+i,Color.white,new Vector2(48,48),new Vector2(x-53,y),Vector2.one*.5f,Vector2.one*.5f);
                _icons[i].raycastTarget=false;
                _skills[i]=Label("Skill"+i,"",11,new Vector2(x+26,y),new Vector2(99,56));
            }
            _stats=Label("LiveAttributes","",15,new Vector2(0,-233),new Vector2(324,158));
            _stats.alignment=TextAnchor.UpperLeft;
        }

        private UnityEngine.UI.Text Label(string name,string value,int size,Vector2 position,Vector2 extent)
        {
            var label=UiFactory.CreateText(transform,name,value,size,new Color(.85f,.81f,.69f),TextAnchor.MiddleCenter);
            label.rectTransform.anchoredPosition=position; label.rectTransform.sizeDelta=extent;
            label.raycastTarget=false; label.horizontalOverflow=HorizontalWrapMode.Wrap; return label;
        }

        public void Bind(PlayerRig rig) { _rig=rig; Refresh(); }
        private void Update() { if(Time.unscaledTime>=_refreshAt) { _refreshAt=Time.unscaledTime+.2f; Refresh(); } }
        private void OnEnable() { if(_stage!=null) _stage.SetActive(true); }
        private void OnDisable() { if(_stage!=null) _stage.SetActive(false); }
        private void Refresh()
        {
            if(_title==null) return;
            _title.text=_rig!=null && _rig.Class!=null ? _rig.Class.displayName : "CHARACTER";
            if(_rig!=null && _rig.Class!=_previewClass && _rig.Class!=null && _rig.Class.characterVisual!=null)
            {
                _previewClass=_rig.Class;
                if(_stage!=null) Destroy(_stage);
                if(_texture!=null) { _texture.Release(); Destroy(_texture); }
                BuildPreview(_portrait);
            }
            IReadOnlyList<SkillData> active=_rig!=null && _rig.Skills!=null ? _rig.Skills.ActiveSkills : null;
            IReadOnlyList<SkillData> passive=_rig!=null && _rig.Skills!=null ? _rig.Skills.PassiveSkills : null;
            if (NetRole.IsClientOnly && _rig!=null && _rig.Class!=null)
            {
                active=_rig.Class.activeSkills;
                passive=_rig.Class.passiveSkills;
            }
            for(int i=0;i<4;i++)
            {
                var list=i<2 ? active : passive; int index=i%2;
                SkillData skill=list!=null && index<list.Count ? list[index] : null;
                _icons[i].sprite=skill!=null ? SkillIconAtlas.Get(skill.skillId) : null;
                _icons[i].enabled=_icons[i].sprite!=null;
                _skills[i].text=skill==null ? "Empty slot" : skill.displayName+"\n"+(i<2 ? skill.EffectiveManaCost+" MP  /  "+skill.EffectiveCooldown.ToString("0.#")+"s" : skill.ShortSummary);
            }
            _text.Clear();
            if(_rig!=null)
            {
                var snapshot=_rig.EffectiveStats;
                var attributes=snapshot.Attributes;
                bool ready=snapshot.AttributesReady;
                if(NetRole.IsClientOnly && _rig.Class!=null)
                {
                    // A separate read model uses replicated worn instances. Never initialize the simulation for UI.
                    if(_displayClass!=_rig.Class) { _displayClass=_rig.Class; _displayAttributes.Initialize(_displayClass); }
                    _displayAttributes.RebuildFromInstances(_rig.Equipment!=null ? _rig.Equipment.WornInstances : null);
                    attributes=_displayAttributes.Snapshot; ready=_displayAttributes.IsReady;
                }
                for(int i=0;i<AttributeRules.AttributeCount;i++)
                    _text.Append(AttributeNames[i]).Append("    ").Append(ready ? attributes.Effective.Get((SixAttribute)i).ToString() : "-").Append('\n');
                _text.Append("Health   ").Append(_rig.CurrentHealth.ToString("0")).Append(" / ").Append(_rig.MaxHealth.ToString("0"));
                var remoteSkills=NetRole.IsClientOnly ? _rig.GetComponent<ICharacterManaView>() : null;
                if(remoteSkills!=null && remoteSkills.HasCharacterMana)
                    _text.Append("     Mana   ").Append(remoteSkills.CharacterMana.ToString("0")).Append(" / ").Append(remoteSkills.CharacterMaxMana.ToString("0"));
                else if(!NetRole.IsClientOnly && _rig.Mana!=null)
                    _text.Append("     Mana   ").Append(_rig.Mana.CurrentMana.ToString("0")).Append(" / ").Append(_rig.Mana.MaxMana);
                else _text.Append("-");
                _text.Append("\nArmour   ").Append(_rig.GetArmorRating().ToString("0.#"));
            }
            _stats.text=_text.ToString();
        }

        private void BuildPreview(UnityEngine.UI.RawImage image)
        {
            var prefab=_previewClass!=null ? _previewClass.characterVisual : Resources.Load<GameObject>("Emberdelve/UI/CharacterPreview");
            if(prefab==null) { Label("PreviewUnavailable","Preview unavailable",14,new Vector2(0,160),new Vector2(300,40)); return; }
            _stage=new GameObject("InventoryPreviewStage"); _stage.transform.position=new Vector3(0,-10000,0);
            var model=Instantiate(prefab,_stage.transform); model.transform.localPosition=Vector3.zero; model.transform.localRotation=Quaternion.Euler(0,15,0);
            foreach(var child in model.GetComponentsInChildren<Transform>(true)) child.gameObject.layer=31;

            var renderers=model.GetComponentsInChildren<Renderer>();
            Bounds bounds=new Bounds(model.transform.position+Vector3.up*.9f,new Vector3(1,1.8f,1));

            var cameraObject=new GameObject("PortraitCamera",typeof(Camera)); cameraObject.transform.SetParent(_stage.transform,false);
            var camera=cameraObject.GetComponent<Camera>(); camera.cullingMask=1<<31; camera.clearFlags=CameraClearFlags.SolidColor;
            camera.backgroundColor=new Color(.035f,.036f,.033f,1); camera.orthographic=true;
            camera.orthographicSize=Mathf.Max(.5f,bounds.extents.y*1.12f); camera.nearClipPlane=.01f; camera.farClipPlane=20;
            camera.transform.position=bounds.center+new Vector3(0,0,4); camera.transform.LookAt(bounds.center);
            _texture=new RenderTexture(352,400,24); _texture.name="InventoryPortrait"; camera.targetTexture=_texture; image.texture=_texture;
            var lamp=new GameObject("PortraitLight",typeof(Light)); lamp.transform.SetParent(_stage.transform,false);
            lamp.transform.localPosition=new Vector3(1,3,2); lamp.transform.LookAt(bounds.center);
            var light=lamp.GetComponent<Light>(); light.type=LightType.Spot; light.range=8; light.spotAngle=85; light.intensity=16; light.cullingMask=1<<31;
            var fill=new GameObject("PortraitFill",typeof(Light)); fill.transform.SetParent(_stage.transform,false);
            fill.transform.localPosition=new Vector3(-1,1.8f,1.5f);
            var fillLight=fill.GetComponent<Light>(); fillLight.type=LightType.Point; fillLight.range=5;
            fillLight.intensity=4; fillLight.color=new Color(.68f,.76f,1f); fillLight.cullingMask=1<<31;
            _stage.SetActive(gameObject.activeInHierarchy);
        }
        private void OnDestroy()
        {
            if(_stage!=null) Destroy(_stage);
            if(_texture!=null) { _texture.Release(); Destroy(_texture); }
            if(_material!=null) Destroy(_material);
        }
    }
}
