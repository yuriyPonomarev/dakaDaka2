using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Emberdelve.AI;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Player;
using Emberdelve.Stats;
using Emberdelve.UI;

// Play-mode-only visual fixture. Never saves or modifies shared definitions.
public static class ReviewD4a
{
    public static object Prepare()
    {
        if (!Application.isPlaying) throw new InvalidOperationException("Enter Play mode first");
        var rig = UnityEngine.Object.FindFirstObjectByType<PlayerRig>();
        if (rig == null || rig.Inventory == null) throw new InvalidOperationException("No live dungeon rig");
        foreach (var enemy in UnityEngine.Object.FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None)) enemy.enabled = false;
        rig.Equipment.Clear();
        rig.Inventory.Clear();
        if (!rig.Inventory.Catalog.TryResolveItem("footpad_boots", out ItemData boots, out string reason) ||
            !rig.Inventory.TryAdd(boots, 1, out reason) || !rig.Equipment.TryEquipItem(boots, out reason))
            throw new InvalidOperationException(reason);
        var screen = UnityEngine.Object.FindFirstObjectByType<InventoryScreenController>();
        screen.Open();
        foreach (var button in UnityEngine.Object.FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (button.name == "TabCharacter") button.onClick.Invoke();
        Canvas.ForceUpdateCanvases();
        return Measure();
    }

    public static object Measure()
    {
        var rig = UnityEngine.Object.FindFirstObjectByType<PlayerRig>();
        var texts = new List<object>();
        foreach (var label in UnityEngine.Object.FindObjectsByType<Text>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            if (label.text.Contains("DEX") || label.text.Contains("Speed") || label.text.Contains("speed"))
                texts.Add(new { name = label.name, text = label.text });
        return new { frame = Time.frameCount, dex = rig.AttributeStats.GetAttribute(SixAttribute.Dexterity),
            modifier = rig.AttributeStats.GetModifier(SixAttribute.Dexterity), factor = rig.AttributeStats.MoveSpeedFactor,
            attributeWalk = rig.AttributeStats.MoveSpeed, walk = rig.EffectiveStats.WalkSpeed,
            physicalWalk = rig.Movement.ResolveGroundSpeed(false, false), hp = rig.CurrentHealth,
            mana = rig.Mana.CurrentMana, labels = texts };
    }
}