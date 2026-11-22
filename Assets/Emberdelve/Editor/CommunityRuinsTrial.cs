using System;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Emberdelve.EditorTools
{
    internal static class CommunityRuinsTrial
    {
        private const string Art = "Assets/Emberdelve/Art/Models/AI/Meshy/CommunityRuins";
        private const string Entry = "Assets/Emberdelve/Prefabs/DungeonModules/Crypt_Entry.prefab";
        private const string GroupName = "Community_Ruins_Trial";
        private static readonly Vector3 TombArch = new Vector3(-7f, 0f, 7f);
        private const float TombArchYaw = -20f;
        private static readonly Vector3 TombBrazier = new Vector3(-4.8f, 0f, 4.8f);
        private const float PedestalTop = .9f;

        [MenuItem("Emberdelve/Visual/Apply Meshy Courtyard Trial")]
        public static void BakeEntry()
        {
            var entry = PrefabUtility.LoadPrefabContents(Entry);
            try
            {
                if (!Apply(entry) || PrefabUtility.SaveAsPrefabAsset(entry, Entry) == null)
                    throw new InvalidOperationException("Could not save the Meshy trial in Crypt_Entry.");
            }
            finally { PrefabUtility.UnloadPrefabContents(entry); }
            AssetDatabase.SaveAssets();
        }

        internal static bool Apply(GameObject room)
        {
            var panel = LoadModel("WallPanel");
            var ruin = LoadModel("RuinedArches");
            var arch = LoadModel("OvergrownArch");
            if (panel == null || ruin == null || arch == null) return false;

            var old = room.transform.Find(GroupName);
            if (old != null) Object.DestroyImmediate(old.gameObject);
            var group = new GameObject(GroupName).transform;
            group.SetParent(room.transform, false);
            // The former west wall is now the gallery. Use clear perimeter bays instead.
            Place(group, panel, new Vector3(-7f, .015f, -17.2f), -2f, Vector3.one * .85f);
            Place(group, ruin, new Vector3(6.5f, 0f, 16.3f), 182f, Vector3.one);
            Place(group, arch, TombArch, TombArchYaw, new Vector3(1.90f, 1.41f, .86f));

            // The new arch fits the same tomb bay. Keep its established collision, lighting and furniture.
            Vector3 pierOffset = Quaternion.Euler(0f, TombArchYaw, 0f) * (Vector3.right * 2.1f);
            foreach (Transform child in room.transform)
            {
                Vector3 position = child.localPosition;
                position.y = 0f;
                bool cap = child.name == "Stone_Arch" && At(position, TombArch);
                bool pier = (child.name == "Column_Base" || child.name == "Column_Shaft" || child.name == "Column_Capital")
                    && (At(position, TombArch - pierOffset) || At(position, TombArch + pierOffset));
                if ((cap || pier) && child.TryGetComponent<MeshRenderer>(out var renderer)) renderer.enabled = false;
            }
            var brazier = LoadModel("StoneBrazier");
            if (brazier != null) FitBrazier(room.transform, group, brazier);
            Debug.Log("[CommunityRuins] Placed the trial models in the entry courtyard.");
            return true;
        }

        private static void FitBrazier(Transform room, Transform group, GameObject model)
        {
            const float bowlHeight = .50f;
            const float bowlWidth = .98f;
            // Fit the existing west brazier so the light keeps its room-relative identity.
            ResizeBrazierPart(room, "Column_Base", .11f, new Vector3(1.10f, .22f, 1.10f));
            ResizeBrazierPart(room, "Column_Shaft", .49f, new Vector3(.70f, .28f, .70f));
            ResizeBrazierPart(room, "Column_Capital", PedestalTop - .075f, new Vector3(.96f, .15f, .96f));
            var bowl = ResizeBrazierPart(room, "Brazier_Bowl", PedestalTop + bowlHeight * .5f,
                new Vector3(bowlWidth, bowlHeight * .5f, .86f));
            bowl.GetComponent<MeshRenderer>().enabled = false;

            var light = FindBrazierPart(room, "Brazier_Light");
            light.localPosition = TombBrazier + Vector3.up * (PedestalTop + bowlHeight);
            light.GetComponent<Light>().intensity = 7.5f;
            Place(group, model, TombBrazier + Vector3.up * PedestalTop, 25f, Vector3.one);

            const string pedestalPath = "Assets/Emberdelve/Art/Generated/WeatheredBrazierPedestal/WeatheredPedestal.prefab";
            var pedestal = AssetDatabase.LoadAssetAtPath<GameObject>(pedestalPath);
            if (pedestal == null) return;
            Place(group, pedestal, TombBrazier, 0f, Vector3.one);
            foreach (string partName in new[] { "Column_Base", "Column_Shaft", "Column_Capital" })
                FindBrazierPart(room, partName).GetComponent<MeshRenderer>().enabled = false;
        }

        private static Transform ResizeBrazierPart(Transform room, string name, float height, Vector3 scale)
        {
            var part = FindBrazierPart(room, name);
            part.localPosition = TombBrazier + Vector3.up * height;
            part.localScale = scale;
            return part;
        }

        private static Transform FindBrazierPart(Transform room, string name)
        {
            foreach (Transform child in room)
            {
                Vector3 position = child.localPosition;
                position.y = 0f;
                if (child.name == name && At(position, TombBrazier)) return child;
            }
            throw new InvalidOperationException("Missing west brazier part: " + name);
        }

        private static bool At(Vector3 point, Vector3 expected) => (point - expected).sqrMagnitude < .0025f;

        private static GameObject LoadModel(string name) =>
            AssetDatabase.LoadAssetAtPath<GameObject>($"{Art}/{name}/{name}.prefab");

        private static void Place(Transform parent, GameObject prefab, Vector3 position, float yaw, Vector3 scale)
        {
            var piece = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            piece.transform.localPosition = position;
            piece.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            piece.transform.localScale = scale;
        }
    }
}
