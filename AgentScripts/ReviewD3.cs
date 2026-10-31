using System;
using System.Collections.Generic;
using UnityEngine;
using Emberdelve.AI;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Player;
using Emberdelve.UI;

// Development-only, in-memory fixture. Run only in Editor Play mode; never saves scenes or assets.
public static class ReviewD3
{
    public static object Prepare()
    {
        if (!Application.isPlaying) throw new InvalidOperationException("Enter Play mode first");
        var rig = UnityEngine.Object.FindFirstObjectByType<PlayerRig>();
        if (rig == null || rig.Inventory == null || rig.Equipment == null) throw new InvalidOperationException("No live dungeon rig");
        foreach (var enemy in UnityEngine.Object.FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None)) enemy.enabled = false;
        if (rig.Loadout.ActiveSlot != 0) rig.Loadout.ForceSwitchToSlot(0);
        rig.Equipment.Clear();
        rig.Inventory.Clear();
        rig.Inventory.ConfigureGrid(10, 6);
        var ids = new[] { "boiled_cuirass", "footpad_boots", "ashen_cloak", "graspwrap_gloves", "emberbead_necklace", "cinder_band_ring", "cinder_band_ring", "ember_fang_blade", "ember_fang_blade", "emberbulwark_shield", "emberbulwark_shield" };
        var slots = new[] { EquipmentSlot.Chest, EquipmentSlot.Feet, EquipmentSlot.Back, EquipmentSlot.Hands, EquipmentSlot.Necklace, EquipmentSlot.RingLeft, EquipmentSlot.RingRight, EquipmentSlot.Weapon, EquipmentSlot.AltWeapon, EquipmentSlot.OffHand, EquipmentSlot.AltOffHand };
        for (int i = 0; i < ids.Length; i++)
        {
            if (!rig.Inventory.Catalog.TryResolveItem(ids[i], out ItemData item, out string reason)) throw new InvalidOperationException(reason);
            if (!rig.Inventory.TryAdd(item, 1, out reason)) throw new InvalidOperationException(reason);
            if (!rig.Equipment.TryEquipFromInventory(rig.Inventory.IndexOfItem(item), slots[i], out reason)) throw new InvalidOperationException(reason);
        }
        foreach (var id in new[] { "blood_draught", "arrow_bundle" })
        {
            if (rig.Inventory.Catalog.TryResolveItem(id, out ItemData item, out string reason)) rig.Inventory.TryAdd(item, 2);
        }
        var screen = UnityEngine.Object.FindFirstObjectByType<InventoryScreenController>();
        if (screen == null) throw new InvalidOperationException("No inventory screen");
        screen.Open();
        Canvas.ForceUpdateCanvases();
        return Measure();
    }

    public static object Measure()
    {
        var result = new List<object>();
        foreach (var text in UnityEngine.Object.FindObjectsByType<UnityEngine.UI.Text>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!text.name.StartsWith("EquipLabel_") || !text.gameObject.activeInHierarchy) continue;
            result.Add(new { name = text.name, text = text.text, rectHeight = text.rectTransform.rect.height, preferredHeight = text.preferredHeight, fontSize = text.fontSize, clipped = text.preferredHeight > text.rectTransform.rect.height + 0.5f });
        }
        return new { frame = Time.frameCount, width = Screen.width, height = Screen.height, labels = result };
    }
}
