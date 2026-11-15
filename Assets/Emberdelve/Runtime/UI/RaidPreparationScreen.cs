using System;
using System.Collections.Generic;
using System.Linq;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Emberdelve.UI
{
    /// <summary>Offline safe-room UI over the same item catalog, snapshots and artwork as the raid inventory.</summary>
    public sealed class RaidPreparationScreen : MonoBehaviour
    {
        private OfflineProfile _profile;
        private PreparationState _snapshot;
        private RectTransform _root, _stash, _bag, _belt, _gear, _stock;
        private const float BagStep = 40f, BagTop = -28f;
        private static readonly Vector2 BeltSlotSize = new Vector2(48,48);
        private RectTransform _ammoPanel, _ammoRows;
        private UnityEngine.UI.Text _ammoStatus;
        private UnityEngine.UI.Image _ghost;
        private UnityEngine.UI.Text _title, _gold, _status, _detail, _pageText;
        private UnityEngine.UI.Button _sell, _equip, _store, _take, _depart;
        private Action _onDepart;
        private PreparationArea _selectedArea;
        private int _selected = -1, _stockPage;
        private long _selectionRevision;
        private bool _dragging;
        private int _suppressClickFrame = -1;
        private readonly List<Target> _targets = new List<Target>();
        private sealed class Target { public RectTransform Rect; public PreparationArea Area; public Vector2Int Cell; public EquipmentSlot Slot; public int BeltSlot = -1; }
        private static readonly Color TextColor = new Color(.91f,.87f,.79f);
        private static readonly Color Muted = new Color(.67f,.67f,.62f);
        public bool IsOpen => _root != null && _root.gameObject.activeSelf;

        public void Open(GameConfig config, CharacterClassData character, Action depart)
        {
            Build(); _root.gameObject.SetActive(true); _root.SetAsLastSibling(); _onDepart = depart;
            _selected = -1; _stockPage = 0; _profile = null; _snapshot = null;
            if (_ammoPanel != null) _ammoPanel.gameObject.SetActive(false);
            RenderDetail();
            Clear(_stock); Clear(_stash); Clear(_bag); Clear(_belt); Clear(_gear);
            string reason = null;
            if (character == null || !OfflineProfileSession.Select(config, character, out reason))
            {
                ShowFailure(character == null ? "Choose a class first" : reason);
                _depart.interactable = false; return;
            }
            _profile = OfflineProfileSession.Current;
            _title.text = (RunContext.SelectedRoyaleMode.HasValue ? "SQUIRE ROYALE / " + RunContext.SelectedRoyaleMode.Value.ToString().ToUpperInvariant() + " / " : "RAID PREPARATION / ") + character.displayName.ToUpperInvariant();
            Render(); _status.text = RunContext.SelectedRoyaleMode.HasValue
                ? "Royale starts with base gear. Your prepared items and ammo move safely to the stash. Make room before departing."
                : "Saved locally. Only your equipped items, utility belt and raid bag enter the raid.";
            _depart.GetComponentInChildren<UnityEngine.UI.Text>().text = RunContext.SelectedRoyaleMode.HasValue ? "ENTER ROYALE / BASE GEAR" : "ENTER RAID";
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }
        public void ShowFailure(string reason)
        { if (_status != null) { _status.text = reason; _status.color = new Color(1f,.55f,.4f); }
          if (_ammoStatus != null) _ammoStatus.text = reason; }
        public void Close() { if (_root != null) _root.gameObject.SetActive(false); _selected = -1; }
        private void Update()
        {
            if (!IsOpen) return;
            if (_ghost != null && _ghost.gameObject.activeSelf && Mouse.current != null &&
                RectTransformUtility.ScreenPointToLocalPointInRectangle(_root, Mouse.current.position.ReadValue(), null, out var point))
                _ghost.rectTransform.anchoredPosition = point;
            _root.localScale = Vector3.one * Mathf.Min(1f, Screen.width / 1440f, Screen.height / 800f);
            if (Keyboard.current?.escapeKey.wasPressedThisFrame == true)
            { if (_ammoPanel != null && _ammoPanel.gameObject.activeSelf) CloseAmmo();
              else if (_selected >= 0) { _selected = -1; RenderDetail(); } else Close(); }
            
        }
        private void Build()
        {
            if (_root != null) return;
            var canvas = UiFactory.EnsureCanvas("Emberdelve_Preparation", 35);
            _root = UiFactory.CreateGroup(canvas.transform, "OfflinePreparation");
            _root.sizeDelta = new Vector2(1440,800);
            UiFactory.CreateImage(_root,"Backdrop",new Color(.018f,.018f,.016f,1f),new Vector2(6000,4000),Vector2.zero,Vector2.one*.5f,Vector2.one*.5f);
            _title = Label(_root,"Title","RAID PREPARATION",27,new Vector2(-130,360),new Vector2(1100,42));
            _gold = Label(_root,"Gold","",24,new Vector2(530,360),new Vector2(270,42)); _gold.color = new Color(.85f,.7f,.39f);
            var left = Panel("StashPanel",new Vector2(490,-5),new Vector2(450,650));
            var middle = Panel("LoadoutPanel",new Vector2(-460,-5),new Vector2(500,650));
            var right = Panel("QuartermasterPanel",new Vector2(20,-5),new Vector2(440,650));
            Label(left,"StashHeading","STASH  /  SAFE BETWEEN RAIDS",18,new Vector2(0,292),new Vector2(430,32));
            Label(middle,"LoadoutHeading","EQUIPMENT / BELT / RAID BAG",18,new Vector2(0,292),new Vector2(460,32));
            Label(right,"MerchantHeading","ROOK  /  QUARTERMASTER",20,new Vector2(0,292),new Vector2(430,32));
            Label(right,"MerchantDescription","Basic supplies in. Recovered treasures out.",14,new Vector2(0,264),new Vector2(430,25));
            _stash = UiFactory.CreateGroup(left,"StashItems");
            _bag = UiFactory.CreateGroup(middle,"BagItems");
            _belt = UiFactory.CreateGroup(middle,"BeltItems");
            _gear = UiFactory.CreateGroup(middle,"EquipmentItems");
            _stock = UiFactory.CreateGroup(right,"MerchantStock");
            GridCells(left,PreparationArea.Stash,12,20,26,260);
            GridCells(middle,PreparationArea.Bag,10,5,BagStep,BagTop);
            BeltCells(middle);
            for (int i=1;i<Equipment.SlotCount;i++)
            {
                var slot=(EquipmentSlot)i;
                var image=UiFactory.CreateImage(middle,"Slot_"+slot,LootPanelStyle.Ink,LootPanelStyle.SlotSize(slot)*.86f,LootPanelStyle.SlotPosition(slot)*.86f+new Vector2(0,30),Vector2.one*.5f,Vector2.one*.5f);
                LootPanelStyle.Decorate(image,null); LootPanelStyle.EmptySlot(image,slot);
                var target=new Target {Rect=image.rectTransform,Area=PreparationArea.Equipment,Slot=slot};
                _targets.Add(target); var button=image.gameObject.AddComponent<UnityEngine.UI.Button>(); button.targetGraphic=image;
                button.onClick.AddListener(()=>ClickTarget(target));
                HookDrag(image.gameObject,()=>SelectAt(target));
                image.gameObject.GetComponent<PreparationDrag>().Right=()=>QuickAction(target);
            }
            _stash.SetAsLastSibling(); _bag.SetAsLastSibling(); _belt.SetAsLastSibling(); _gear.SetAsLastSibling();
            _store=Button(left,"Store","STORE BAG & BELT",new Vector2(-103,-285),new Vector2(204,34),()=>Result(_profile.StoreBag(_profile.Revision,out var why),why));
            _take=Button(left,"Take","TAKE SELECTED",new Vector2(107,-285),new Vector2(204,34),()=>MoveSelected(PreparationArea.Bag,EquipmentSlot.None,null));
            _equip=Button(middle,"Equip","EQUIP SELECTED",new Vector2(-115,-285),new Vector2(220,34),EquipSelected);
            Button(middle,"PutAway","PUT IN STASH",new Vector2(115,-285),new Vector2(220,34),()=>MoveSelected(PreparationArea.Stash,EquipmentSlot.None,null));
            Label(middle,"Controls","Drag between bag, belt, stash and equipment.\nRMB equips / belts / removes. Shift+RMB transfers.",13,new Vector2(0,-252),new Vector2(470,38));
            Button(right,"Previous","<",new Vector2(-100,-100),new Vector2(40,28),()=>{_stockPage=Mathf.Max(0,_stockPage-1);RenderStock();});
            Button(right,"Next",">",new Vector2(100,-100),new Vector2(40,28),()=>{_stockPage++;RenderStock();});
            _pageText=Label(right,"Page","",14,new Vector2(0,-100),new Vector2(130,28));
            _detail=Label(right,"SelectedItem","Select an item to inspect or sell.",15,new Vector2(0,-190),new Vector2(414,142));
            _detail.alignment=TextAnchor.UpperLeft; _detail.horizontalOverflow=HorizontalWrapMode.Wrap;
            _sell=Button(right,"Sell","SELL SELECTED",new Vector2(0,-285),new Vector2(414,34),SellSelected);
            _status=Label(_root,"Status","",15,new Vector2(-60,-344),new Vector2(1320,38));
            _status.horizontalOverflow=HorizontalWrapMode.Wrap;
            Button(_root,"BaseWeapons","SQUIRE / BASE WEAPONS",new Vector2(0,-377),new Vector2(300,34),()=>{if(_profile!=null)Result(_profile.EquipBaseWeapons(_profile.Revision,out var why),why);});
            Button(_root,"Ammunition","AMMUNITION",new Vector2(-265,-377),new Vector2(190,34),OpenAmmo);
            Button(_root,"Back","BACK TO CLASSES",new Vector2(-520,-377),new Vector2(280,34),Close);
            _depart=Button(_root,"EnterRaid","ENTER RAID",new Vector2(500,-377),new Vector2(320,38),()=>{if(_profile!=null)_onDepart?.Invoke();});
        }
        private void OpenAmmo()
        {
            if (_profile == null) return;
            if (_ammoPanel == null)
            {
                _ammoPanel = UiFactory.CreateGroup(_root, "AmmunitionPanel");
                _ammoPanel.sizeDelta = new Vector2(1440,800);
                UiFactory.CreateImage(_ammoPanel,"Shade",new Color(0,0,0,.85f),new Vector2(1440,800),Vector2.zero,Vector2.one*.5f,Vector2.one*.5f);
                var box = UiFactory.CreateImage(_ammoPanel,"Window",LootPanelStyle.Ink,new Vector2(920,540),Vector2.zero,Vector2.one*.5f,Vector2.one*.5f);
                DungeonUiStyle.Surface(box);
                Label(_ammoPanel,"Heading","AMMUNITION",24,new Vector2(0,230),new Vector2(800,36));
                Label(_ammoPanel,"Help","Select a weapon or ammo pack before opening this window.\nLoaded rounds stay with the weapon. Carried reserve enters the raid.",15,new Vector2(0,184),new Vector2(840,48));
                _ammoRows = UiFactory.CreateGroup(_ammoPanel,"Rows");
                _ammoStatus = Label(_ammoPanel,"AmmoStatus","",15,new Vector2(0,-177),new Vector2(850,60));
                _ammoStatus.horizontalOverflow = HorizontalWrapMode.Wrap;
                Button(_ammoPanel,"CloseAmmo","BACK TO PREPARATION",new Vector2(0,-230),new Vector2(300,34),CloseAmmo);
            }
            _ammoPanel.gameObject.SetActive(true); _ammoPanel.SetAsLastSibling();
            if (_ghost != null) _ghost.gameObject.SetActive(false);
            _ammoStatus.text = "Only carried ammunition is lost on death. Stored ammunition stays safe.";
            RenderAmmo();
        }
        private void CloseAmmo()
        { _ammoPanel.gameObject.SetActive(false); RenderDetail(); }
        private void AmmoResult(bool success, string reason)
        {
            if (!success) { ShowFailure(reason); return; }
            Selected(out var selected, out _);
            string uid = selected?.item.uid, id = selected?.item.itemId;
            _snapshot = _profile.Snapshot;
            _selected = Array.FindIndex(_snapshot.Container(_selectedArea).entries,
                e => !string.IsNullOrEmpty(uid) ? e.item.uid == uid : e.item.itemId == id);
            _selectionRevision = _snapshot.Revision;
            Render(); RenderAmmo(); _ammoStatus.text = "Ammunition saved.";
        }
        private void RenderAmmo()
        {
            Clear(_ammoRows);
            bool selected = Selected(out var entry, out var item);
            WeaponData weapon = selected ? item.grantedWeapon : null;
            bool ranged = weapon != null && weapon.IsRanged;
            string text = ranged ? item.displayName + "  |  Loaded " + _snapshot.Ammunition.LoadedCount(entry.item.uid)
                + " / " + weapon.magazineSize + "  |  " + _selectedArea : selected ? item.displayName + "  x" + entry.item.quantity : "No item selected";
            Label(_ammoRows,"SelectedAmmo",text,16,new Vector2(-100,120),new Vector2(620,34));
            var load = Button(_ammoRows,"Load","LOAD",new Vector2(265,120),new Vector2(96,32),
                ()=>AmmoResult(_profile.LoadAmmo(_selectedArea,_selected,_selectionRevision,out var why),why));
            var unload = Button(_ammoRows,"Unload","UNLOAD",new Vector2(370,120),new Vector2(96,32),
                ()=>AmmoResult(_profile.UnloadAmmo(_selectedArea,_selected,_selectionRevision,out var why),why));
            load.interactable = unload.interactable = ranged;
            int row = 0;
            foreach (var type in _profile.AmmoWeapons)
            {
                float y = 40 - row++ * 90;
                int carried = _snapshot.Ammunition.CarriedCount(type.weaponId), stored = _snapshot.Ammunition.StoredCount(type.weaponId);
                int capacity = _profile.AmmoCapacity(type.weaponId);
                Label(_ammoRows,"Ammo_"+type.weaponId,type.ammoDisplayName + " / " + type.displayName + "\nRAID " + carried + "/" + capacity + "     STASH " + stored,
                    15,new Vector2(-230,y),new Vector2(370,60));
                int take = Math.Min(10,Math.Min(stored,Math.Max(0,capacity-carried))), put = Math.Min(10,carried);
                var takeButton = Button(_ammoRows,"Take_"+type.weaponId,"TAKE "+take,new Vector2(30,y),new Vector2(120,34),
                    ()=>AmmoResult(_profile.TransferAmmo(type.weaponId,take,false,_profile.Revision,out var why),why));
                var storeButton = Button(_ammoRows,"Store_"+type.weaponId,"STORE "+put,new Vector2(160,y),new Vector2(120,34),
                    ()=>AmmoResult(_profile.TransferAmmo(type.weaponId,put,true,_profile.Revision,out var why),why));
                var unpack = Button(_ammoRows,"Unpack_"+type.weaponId,"OPEN PACK",new Vector2(320,y),new Vector2(170,34),
                    ()=>AmmoResult(_profile.UnpackAmmo(_selectedArea,_selected,type.weaponId,_selectionRevision,out var why),why));
                takeButton.interactable = take > 0; storeButton.interactable = put > 0;
                unpack.interactable = selected && item.ammoGrant > 0 && item.MatchesAmmo(type);
            }
        }
        private RectTransform Panel(string name,Vector2 at,Vector2 size)
        {
            var image=UiFactory.CreateImage(_root,name,LootPanelStyle.Ink,size,at,Vector2.one*.5f,Vector2.one*.5f);
            image.color = new Color(.065f,.063f,.057f);
            DungeonUiStyle.Surface(image); return image.rectTransform;
        }
        private static UnityEngine.UI.Text Label(Transform parent,string name,string text,int font,Vector2 at,Vector2 size)
        {
            var label=UiFactory.CreateText(parent,name,text,font,TextColor,TextAnchor.MiddleCenter);
            label.rectTransform.anchoredPosition=at; label.rectTransform.sizeDelta=size; label.raycastTarget=false; if(font>=18) DungeonUiStyle.Heading(label); return label;
        }
        private static UnityEngine.UI.Button Button(Transform parent,string name,string title,Vector2 at,Vector2 size,UnityEngine.Events.UnityAction action)
        {
            var button=UiFactory.CreateButton(parent,name,title,action,size,new Color(.17f,.145f,.105f),TextColor,15);
            ((RectTransform)button.transform).anchoredPosition=at; return button;
        }
        private void GridCells(Transform parent,PreparationArea area,int width,int height,float step,float top)
        {
            for(int y=0;y<height;y++) for(int x=0;x<width;x++)
            {
                var image=UiFactory.CreateImage(parent,area+"Cell_"+x+"_"+y,new Color(.085f,.081f,.071f),Vector2.one*(step-2),
                    new Vector2(-width*step*.5f+(x+.5f)*step,top-(y+.5f)*step),Vector2.one*.5f,Vector2.one*.5f);
                var target=new Target{Rect=image.rectTransform,Area=area,Cell=new Vector2Int(x,y)};
                _targets.Add(target); var button=image.gameObject.AddComponent<UnityEngine.UI.Button>(); button.targetGraphic=image;
                button.onClick.AddListener(()=>ClickTarget(target)); HookDrag(image.gameObject,()=>SelectAt(target));
                image.gameObject.GetComponent<PreparationDrag>().Right=()=>QuickAction(target);
            }
        }
        private static Vector2 BeltPosition(int slot)
        { return new Vector2((slot < 3 ? -176f : 64f) + (slot % 3) * 56f,20f); }
        private void BeltCells(Transform parent)
        {
            Label(parent,"QuickGroup3","3",20,new Vector2(-218,20),new Vector2(28,36));
            Label(parent,"QuickGroup4","4",20,new Vector2(22,20),new Vector2(28,36));
            for(int slot=0;slot<QuickBeltRules.SlotCount;slot++)
            {
                var image=UiFactory.CreateImage(parent,"BeltSlot_"+slot,LootPanelStyle.Ink,BeltSlotSize,
                    BeltPosition(slot),Vector2.one*.5f,Vector2.one*.5f);
                LootPanelStyle.Decorate(image,null);
                var empty=Label(image.transform,"Empty","EMPTY",10,Vector2.zero,new Vector2(44,20));empty.color=Muted;
                var target=new Target{Rect=image.rectTransform,Area=PreparationArea.Bag,Cell=QuickBeltRules.Cell(slot,10),BeltSlot=slot};
                _targets.Add(target);
                var button=image.gameObject.AddComponent<UnityEngine.UI.Button>();button.targetGraphic=image;
                button.onClick.AddListener(()=>ClickTarget(target));HookDrag(image.gameObject,()=>SelectAt(target));
                image.gameObject.GetComponent<PreparationDrag>().Right=()=>QuickAction(target);
            }
        }
        private void BeltSelected()
        {
            if(!Selected(out _,out var item) || !QuickBeltRules.CanStore(item))return;
            for(int slot=0;slot<QuickBeltRules.SlotCount;slot++)
            {
                var cell=QuickBeltRules.Cell(slot,_snapshot.Ready.bag.width);
                if(_snapshot.Ready.bag.entries.Any(e=>e.x==cell.x && e.y==cell.y))continue;
                MoveSelected(PreparationArea.Bag,EquipmentSlot.None,cell);return;
            }
            ShowFailure("All six utility slots are occupied. Drag onto a matching stack to combine, or free a slot.");
        }
        private void HookDrag(GameObject obj,Action select)
        {
            var drag=obj.AddComponent<PreparationDrag>();
            drag.Begin=()=>{select();_dragging=true;};
            drag.End=position=>
            {
                _dragging=false; _suppressClickFrame=Time.frameCount;
                var target=_targets.FirstOrDefault(t=>RectTransformUtility.RectangleContainsScreenPoint(t.Rect,position));
                if(target!=null && _selected>=0) MoveSelected(target.Area,target.Slot,target.Area==PreparationArea.Equipment?(Vector2Int?)null:target.Cell);
                else { _selected=-1;RenderDetail(); }
            };
        }
        private void QuickAction(Target target)
        {
            if (_profile == null) return;
            if (_selected >= 0) { _selected = -1; RenderDetail(); return; }
            SelectAt(target);
            if (!Selected(out _, out var item)) return;
            bool shift = Keyboard.current != null && (Keyboard.current.leftShiftKey.isPressed || Keyboard.current.rightShiftKey.isPressed);
            if (shift) MoveSelected(target.Area == PreparationArea.Stash ? PreparationArea.Bag : PreparationArea.Stash, EquipmentSlot.None, null);
            else if (target.Area == PreparationArea.Equipment || target.BeltSlot >= 0) MoveSelected(PreparationArea.Bag, EquipmentSlot.None, null);
            else if (target.Area == PreparationArea.Bag && QuickBeltRules.CanStore(item)) BeltSelected();
            else if (item.IsEquippable) EquipSelected();
            else if (target.Area == PreparationArea.Stash) MoveSelected(PreparationArea.Bag, EquipmentSlot.None, null);
            else { _selected = -1; RenderDetail(); }
        }
        private int IndexAt(Target target)
        {
            if(_snapshot==null || _profile==null) return -1;
            var entries=_snapshot.Container(target.Area).entries;
            for(int i=0;i<entries.Length;i++)
            {
                var entry=entries[i];
                if(target.Area==PreparationArea.Equipment) {if(entry.slot==target.Slot.ToString())return i;continue;}
                if(!_profile.Catalog.TryResolveItem(entry.item.itemId,out var definition,out _))continue;
                bool belt=target.Area==PreparationArea.Bag && QuickBeltRules.TryGetSlot(new Vector2Int(entry.x,entry.y),_snapshot.Ready.bag.width,out _);
                if(target.BeltSlot>=0) {if(entry.x==target.Cell.x && entry.y==target.Cell.y)return i;continue;}
                if(belt)continue;
                var size=definition.EffectiveFootprint;
                if(target.Cell.x>=entry.x && target.Cell.x<entry.x+size.x && target.Cell.y>=entry.y && target.Cell.y<entry.y+size.y)return i;
            }
            return -1;
        }
        private void SelectAt(Target target)
        { _selected=IndexAt(target);_selectedArea=target.Area;_selectionRevision=_profile?.Revision??0;RenderDetail(); }
        private void ClickTarget(Target target)
        {
            if(_profile==null || _dragging || _suppressClickFrame==Time.frameCount)return;
            if(_selected<0) {SelectAt(target);return;}
            if(target.Area==_selectedArea && IndexAt(target)==_selected) {_selected=-1;RenderDetail();return;}
            MoveSelected(target.Area,target.Slot,target.Area==PreparationArea.Equipment?(Vector2Int?)null:target.Cell);
        }
        private void MoveSelected(PreparationArea target,EquipmentSlot slot,Vector2Int? cell)
        {
            if(_profile==null || _selected<0)return;
            Result(_profile.Move(_selectedArea,_selected,target,slot,cell,_selectionRevision,out string reason),reason);
        }
        private void EquipSelected()
        {
            if(!Selected(out var entry,out var definition))return;
            var slot=definition.equipSlot;
            if(slot==EquipmentSlot.RingLeft && _snapshot.Ready.equipment.entries.Any(e=>e.slot==slot.ToString()))slot=EquipmentSlot.RingRight;
            MoveSelected(PreparationArea.Equipment,slot,null);
        }
        private void SellSelected()
        { if(_profile!=null && _selected>=0)Result(_profile.Sell(_selectedArea,_selected,_selectionRevision,out string reason),reason); }
        private void Result(bool success,string reason)
        {
            if(!success) {ShowFailure(reason);return;}
            _selected=-1;Render();_status.color=Muted;_status.text="Saved. Your stash and prepared equipment are up to date.";
        }
        private void Render()
        {
            if(_profile==null)return;
            _snapshot=_profile.Snapshot;
            _gold.text=_snapshot.Gold+" GOLD";
            foreach(var target in _targets) if(target.BeltSlot>=0)
            {
                target.Cell=QuickBeltRules.Cell(target.BeltSlot,_snapshot.Ready.bag.width);
                var empty=target.Rect.Find("Empty");
                if(empty!=null)empty.gameObject.SetActive(!_snapshot.Ready.bag.entries.Any(e=>e.x==target.Cell.x && e.y==target.Cell.y));
            }
            RenderItems(_stash,_snapshot.Stash,PreparationArea.Stash,26,260);
            RenderItems(_bag,_snapshot.Ready.bag,PreparationArea.Bag,BagStep,BagTop);
            RenderItems(_belt,_snapshot.Ready.bag,PreparationArea.Bag,0,0,true);
            RenderItems(_gear,_snapshot.Ready.equipment,PreparationArea.Equipment,0,0);
            RenderStock();RenderDetail();_depart.interactable=string.IsNullOrEmpty(_profile.ActiveRaid);
        }
        private static void Clear(Transform parent)
        { foreach(Transform child in parent) {child.gameObject.SetActive(false);Destroy(child.gameObject);} }
        private void RenderItems(RectTransform parent,ContainerSnapshot container,PreparationArea area,float step,float top,bool beltOnly=false)
        {
            Clear(parent);
            foreach(var entry in container.entries)
            {
                if(!_profile.Catalog.TryResolveItem(entry.item.itemId,out var item,out _))continue;
                int beltSlot=-1;
                bool isBelt=area==PreparationArea.Bag && QuickBeltRules.TryGetSlot(new Vector2Int(entry.x,entry.y),container.width,out beltSlot);
                if(beltOnly!=isBelt)continue;
                Vector2 size,at;
                if(isBelt) {size=BeltSlotSize;at=BeltPosition(beltSlot);}
                else if(area==PreparationArea.Equipment)
                {
                    var slot=(EquipmentSlot)Enum.Parse(typeof(EquipmentSlot),entry.slot);
                    size=LootPanelStyle.SlotSize(slot)*.86f;at=LootPanelStyle.SlotPosition(slot)*.86f+new Vector2(0,30);
                }
                else {var shape=item.EffectiveFootprint;size=new Vector2(shape.x*step-2,shape.y*step-2);at=new Vector2(-container.width*step*.5f+entry.x*step+size.x*.5f,top-entry.y*step-size.y*.5f);}
                var image=UiFactory.CreateImage(parent,"Item_"+entry.item.itemId,LootPanelStyle.Ink,size,at,Vector2.one*.5f,Vector2.one*.5f);
                ItemInstance instance=null;if(item.IsEquippable)_profile.Catalog.TryRestore(entry.item,out instance,out _);
                LootPanelStyle.Decorate(image,item,instance); image.raycastTarget=false;
                if(isBelt)
                {
                    string count=item.healAmount>0 ? entry.item.quantity+"/"+QuickBeltRules.StackLimit(item) : "x"+entry.item.quantity;
                    var amount=Label(image.transform,"Count",count,12,new Vector2(0,-size.y*.5f+8),new Vector2(size.x-4,16));
                    amount.alignment=TextAnchor.MiddleRight;
                }
                else if(entry.item.quantity>1)Label(image.transform,"Count",entry.item.quantity.ToString(),13,new Vector2(size.x*.5f-10,-size.y*.5f+10),new Vector2(22,18));
            }
        }
        private void RenderStock()
        {
            if(_profile==null)return;Clear(_stock);
            int pages=Mathf.Max(1,Mathf.CeilToInt(_profile.Stock.Count/7f));_stockPage=Mathf.Clamp(_stockPage,0,pages-1);
            _pageText.text=(_stockPage+1)+" / "+pages;
            for(int row=0;row<7;row++)
            {
                int index=_stockPage*7+row;if(index>=_profile.Stock.Count)break;
                var item=_profile.Stock[index];float y=222-row*44;
                var label=Label(_stock,"Offer_"+item.itemId,item.displayName,14,new Vector2(-58,y),new Vector2(280,38));label.alignment=TextAnchor.MiddleLeft;
                int price=OfflineProfile.BuyPrice(item);
                var buy=Button(_stock,"Buy_"+item.itemId,"BUY  "+price+"g",new Vector2(151,y),new Vector2(110,34),()=>Result(_profile.Buy(item.itemId,_profile.Revision,out var why),why));
                buy.interactable=_snapshot.Gold>=price;
            }
        }
        private bool Selected(out ContainerEntry entry,out ItemData item)
        {
            entry=null;item=null;if(_snapshot==null || _selected<0)return false;
            var entries=_snapshot.Container(_selectedArea).entries;if(_selected>=entries.Length)return false;
            entry=entries[_selected];return _profile.Catalog.TryResolveItem(entry.item.itemId,out item,out _);
        }
        private void RenderDetail()
        {
            if(_detail==null)return;
            if (_ghost != null) { Destroy(_ghost.gameObject); _ghost = null; }
            if ((_ammoPanel == null || !_ammoPanel.gameObject.activeSelf) && Selected(out var carried, out var carriedDefinition))
            {
                _ghost = UiFactory.CreateImage(_root, "CarriedItem", LootPanelStyle.Ink,
                    (Vector2)carriedDefinition.EffectiveFootprint * 42, Vector2.zero, Vector2.one*.5f, Vector2.one*.5f);
                ItemInstance carriedInstance = null;
                if (carriedDefinition.IsEquippable) _profile.Catalog.TryRestore(carried.item, out carriedInstance, out _);
                LootPanelStyle.Decorate(_ghost, carriedDefinition, carriedInstance);
                foreach (var graphic in _ghost.GetComponentsInChildren<UnityEngine.UI.Graphic>()) graphic.raycastTarget = false;
                _ghost.gameObject.AddComponent<CanvasGroup>().alpha = .75f;
            }
            bool selected=Selected(out var entry,out var item);
            _sell.interactable=selected;_equip.interactable=selected && item.IsEquippable;
            _take.interactable=selected && (_selectedArea!=PreparationArea.Bag || QuickBeltRules.TryGetSlot(new Vector2Int(entry.x,entry.y),_snapshot.Ready.bag.width,out _));_store.interactable=_profile!=null && (_snapshot?.Ready.bag.entries.Length??0)>0;
            if(!selected){_detail.text="Select an item to inspect.\nPurchases go to the stash.\nOnly carried gear is lost on death.";_sell.GetComponentInChildren<UnityEngine.UI.Text>().text="SELL SELECTED";return;}
            ItemTooltipModel tip;
            if(item.IsEquippable && _profile.Catalog.TryRestore(entry.item,out var instance,out _))tip=ItemTooltip.ForInstance(instance);
            else tip=ItemTooltip.ForDefinition(item);
            _detail.text=item.displayName+"  x"+entry.item.quantity+"\n"+ (tip==null?"":string.Join("\n",tip.FixedLines.Take(3)))+
                (tip!=null && tip.RolledDisplayLines.Count>0?"\n"+string.Join("\n",tip.RolledDisplayLines.Take(2)):"");
            if (item.grantedWeapon != null && item.grantedWeapon.IsRanged)
                _detail.text += "\nLoaded: " + _snapshot.Ammunition.LoadedCount(entry.item.uid) + "/" + item.grantedWeapon.magazineSize;
            _sell.GetComponentInChildren<UnityEngine.UI.Text>().text="SELL STACK  +"+_profile.SaleValue(item,entry.item)+"g";
        }
        private void OnDestroy(){if(_root!=null)Destroy(_root.gameObject);}
    }
    public sealed class PreparationDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerDownHandler
    {
        public Action Begin;
        public Action Right;
        public void OnPointerDown(PointerEventData e){if(e.button==PointerEventData.InputButton.Right)Right?.Invoke();}
        public Action<Vector2> End;
        public void OnBeginDrag(PointerEventData e){if(e.button==PointerEventData.InputButton.Left)Begin?.Invoke();}
        public void OnDrag(PointerEventData e){}
        public void OnEndDrag(PointerEventData e){if(e.button==PointerEventData.InputButton.Left)End?.Invoke(e.position);}
    }
}
