using UnityEngine;
namespace Emberdelve.UI
{
    public static class SkillIconAtlas
    {
        private static readonly string[] Ids={"warrior_dash","warrior_battle_cry","warrior_ashen_might","warrior_bulwark_training","archer_multi_shot","archer_piercing_shot","archer_farshot","archer_fleet_footing"};
        private static readonly Sprite[] Icons=new Sprite[8];
        private static Texture2D _atlas;
        public static Sprite Get(string id)
        {
            int index=System.Array.IndexOf(Ids,id); if(index<0) return null;
            if(Icons[index]!=null) return Icons[index];
            if(_atlas==null) _atlas=Resources.Load<Texture2D>("Emberdelve/SkillIcons/Abilities");
            if(_atlas==null) return null;
            float w=_atlas.width/4f,h=_atlas.height/2f;
            return Icons[index]=Sprite.Create(_atlas,new Rect(index%4*w,(1-index/4)*h,w,h),Vector2.one*.5f,100);
        }
    }
}
