using UnityEditor;
using UnityEngine;
using Emberdelve.Data;
using Emberdelve.Items;

// Independent manager reproduction. No scene/shared asset mutations; ephemeral objects only.
public static class ManagerInventoryGeometryProbe
{
    public static void Run()
    {
        GameObject owner = new GameObject("ManagerInventoryGeometryProbe");
        ItemData blade = ScriptableObject.CreateInstance<ItemData>();
        ItemData boots = ScriptableObject.CreateInstance<ItemData>();
        int failures = 0;
        ItemCatalog testCatalogForCleanup = null;
        try
        {
            blade.itemId = "probe_blade";
            blade.displayName = "Probe Blade";
            blade.gridFootprint = new Vector2Int(1, 3);
            blade.equipSlot = EquipmentSlot.Weapon;
            boots.itemId = "probe_boots";
            boots.displayName = "Probe Boots";
            boots.gridFootprint = new Vector2Int(2, 2);
            boots.equipSlot = EquipmentSlot.Legs;
            Inventory bag = owner.AddComponent<Inventory>();
            // R3: the bag mints gear only for catalog entries, so the probe plates are listed in an explicit TEST catalog.
            ItemCatalog testCatalog = ItemCatalog.CreateForTest(new[] { blade, boots }, null);
            testCatalogForCleanup = testCatalog;
            bag.SetCatalog(testCatalog);
            bag.ConfigureGrid(10, 4);
            bag.Clear();
            bool setup = bag.TryAddAt(blade, 1, new Vector2Int(0, 0), out _) &&
                         bag.TryAddAt(boots, 1, new Vector2Int(1, 0), out _);
            if (!setup) { failures++; Debug.LogError("[Emberdelve][Smoke:geometry] FAIL non-overlapping initial footprints could not be placed"); }
            bool swapped = bag.TrySwap(0, 1, out string reason);
            if (swapped) { failures++; Debug.LogError("[Emberdelve][Smoke:geometry] FAIL swap accepted overlapping destinations: blade1x3 at(1,0), boots2x2 at(0,0)"); }
            bool unchanged = bag.Slots.Count == 2 && bag.Slots[0].Origin == new Vector2Int(0, 0) &&
                             bag.Slots[1].Origin == new Vector2Int(1, 0) && bag.TotalUnits == 2 && bag.UsedCells == 7;
            if (!unchanged) { failures++; Debug.LogError("[Emberdelve][Smoke:geometry] FAIL refused-overlap swap must preserve exact original origins, units and7 occupied cells"); }
            Debug.Log($"[Emberdelve][Smoke:geometry] {(failures == 0 ? "PASS" : "FAIL")} checks=3 failures={failures} swapped={swapped} reason={reason}");
        }
        finally
        {
            Object.DestroyImmediate(owner);
            if (testCatalogForCleanup != null) Object.DestroyImmediate(testCatalogForCleanup);
            Object.DestroyImmediate(blade);
            Object.DestroyImmediate(boots);
        }
        EditorApplication.Exit(failures == 0 ? 0 : 1);
    }
}
