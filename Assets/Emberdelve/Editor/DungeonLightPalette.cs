using System;
using System.Linq;
using Emberdelve.Feedback;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace Emberdelve.EditorTools
{
    // One fire palette for every authored dungeon light. Before this pass each builder
    // picked its own saturated orange, and room braziers (22 intensity, 18 m, no shadows)
    // washed whole modules orange through the walls. Flames are now a desaturated warm
    // white, braziers are local and wall-occluded, and the dark between pools is left to
    // the atmosphere's cool fill (MoonlitRuinsEnvironment).
    public static class DungeonLightPalette
    {
        public enum Kind { Brazier, BrazierBlue, Torch, WallTorch, Candle, ShelfCandle, Votive, CacheLamp, SocketBrazier, SocketCandles }

        public static readonly Color Fire = new Color(1f, .66f, .30f);
        public static readonly Color Candle = new Color(1f, .70f, .36f);
        public static readonly Color GhostFire = new Color(.52f, .68f, 1f);
        const int ShadowTexels = 128;
        public const string SocketBrazierName = "Socket_Light_Brazier";
        public const string SocketCandlesName = "Socket_Light_Candles";

        public static void Configure(Light light, Kind kind)
        {
            light.type = LightType.Point;
            switch (kind)
            {
                case Kind.Brazier: Set(light, Fire, 12f, 12f, true); light.shadowNearPlane = .65f; break;
                case Kind.BrazierBlue: Set(light, GhostFire, 6f, 11f, true); light.shadowNearPlane = .65f; break;
                // DungeonLayoutLighting.Validate keeps sconces at range <= 8.1 m.
                case Kind.Torch: Set(light, Fire, 6f, 8f, true); break;
                case Kind.WallTorch: Set(light, Fire, 5f, 7f, false); break;
                case Kind.Candle: Set(light, Candle, 1.8f, 4f, false); break;
                case Kind.ShelfCandle: Set(light, Candle, 1.1f, 3.2f, false); break;
                case Kind.Votive: Set(light, Candle, 1.1f, 2.8f, false); break;
                case Kind.CacheLamp: Set(light, Fire, 3.5f, 6.5f, false); break;
                case Kind.SocketBrazier: Set(light, Fire, 6f, 9f, true); light.shadowNearPlane = .45f; break;
                case Kind.SocketCandles: Set(light, Candle, 2f, 4.5f, false); break;
            }
        }

        static void Set(Light light, Color color, float intensity, float range, bool shadows)
        {
            light.color = color;
            light.intensity = intensity;
            light.range = range;
            if (shadows) WallOccluded(light); else light.shadows = LightShadows.None;
            var flicker = light.GetComponent<TorchFlicker>();
            if (flicker != null)
            {
                var serialized = new SerializedObject(flicker);
                serialized.FindProperty("baseIntensity").floatValue = intensity;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        // Same small custom shadow tier the layout sconces already use.
        public static void WallOccluded(Light light)
        {
            light.shadows = LightShadows.Soft;
            var urp = light.GetComponent<UniversalAdditionalLightData>() ?? light.gameObject.AddComponent<UniversalAdditionalLightData>();
            var urpSettings = new SerializedObject(urp);
            urpSettings.FindProperty("m_AdditionalLightsShadowResolutionTier").intValue = UniversalAdditionalLightData.AdditionalLightsShadowResolutionTierCustom;
            urpSettings.ApplyModifiedPropertiesWithoutUndo();
            var lightSettings = new SerializedObject(light);
            lightSettings.FindProperty("m_Shadows.m_Resolution").intValue = ShadowTexels;
            lightSettings.ApplyModifiedPropertiesWithoutUndo();
            urp.usePipelineSettings = false;
            light.shadowCustomResolution = ShadowTexels;
            light.shadowBias = .025f; light.shadowNormalBias = .12f; light.shadowNearPlane = .08f;
        }

        public static Kind? Classify(Light light)
        {
            if (light.type != LightType.Point) return null;
            Transform t = light.transform;
            string parent = t.parent != null ? t.parent.name : "";
            if (t.name == "Brazier_Light") return light.color.b > light.color.r ? Kind.BrazierBlue : Kind.Brazier;
            if (t.name == "Flame" && parent.StartsWith("Bay_Torch_")) return Kind.Torch;
            if (t.name == "Flame" && parent == "Wall_Torch") return Kind.WallTorch;
            if (t.name == "Authored_Candlelight") return parent == "Shelf_Candlestick" ? Kind.ShelfCandle : Kind.Candle;
            if (t.name == "Candlelight") return Kind.Votive;
            if (t.name == "Cache_Lamp") return Kind.CacheLamp;
            if (t.name == SocketBrazierName) return Kind.SocketBrazier;
            if (t.name == SocketCandlesName) return Kind.SocketCandles;
            return null;
        }

        public static int ApplyToRoom(GameObject room)
        {
            int count = 0;
            foreach (var light in room.GetComponentsInChildren<Light>(true))
            {
                var kind = Classify(light);
                if (kind == null) continue;
                Configure(light, kind.Value);
                count++;
            }
            return count;
        }

        [MenuItem("Emberdelve/Visual/Apply Dungeon Light Palette")]
        public static void ApplySaved()
        {
            if (!Application.dataPath.Replace('\\', '/').EndsWith("/DakaDaka_clean_handoff/Assets", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Author only in clean_handoff");
            int total = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Emberdelve/Prefabs/DungeonModules" }).OrderBy(AssetDatabase.GUIDToAssetPath))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var root = PrefabUtility.LoadPrefabContents(path);
                try { total += ApplyToRoom(root); PrefabUtility.SaveAsPrefabAsset(root, path); }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[LightPalette] configured lights=" + total);
        }
    }
}
