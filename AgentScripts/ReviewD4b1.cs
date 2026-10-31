using System;
using UnityEditor;
using UnityEngine;
using Emberdelve.AI;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Player;
using Emberdelve.UI;

// Development-only visual fixture. Uses real class/gear and accepted melee timing; saves no assets.
public static class ReviewD4b1
{
    public static object Prepare()
    {
        if (!Application.isPlaying) throw new InvalidOperationException("Enter Play mode first");
        var rig = UnityEngine.Object.FindFirstObjectByType<PlayerRig>();
        if (rig == null) throw new InvalidOperationException("No live player");
        foreach (var enemy in UnityEngine.Object.FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None)) enemy.enabled = false;
        var data = AssetDatabase.LoadAssetAtPath<CharacterClassData>("Assets/Emberdelve/Data/Class_Archer.asset");
        var config = AssetDatabase.LoadAssetAtPath<GameConfig>("Assets/Emberdelve/Data/EmberdelveGameConfig.asset");
        rig.ApplyClass(data, config);
        rig.Equipment.Clear(); rig.Inventory.Clear();
        if (!rig.Inventory.Catalog.TryResolveItem("footpad_boots", out ItemData boots, out string reason) ||
            !rig.Inventory.TryAdd(boots, 1, out reason) || !rig.Equipment.TryEquipItem(boots, out reason))
            throw new InvalidOperationException(reason);
        rig.Loadout.ForceSwitchToSlot(1);
        UnityEngine.Object.FindFirstObjectByType<InventoryScreenController>()?.Close();
        rig.Look.ApplyAimPose(0f, 0f);
        Time.timeScale = .05f;
        var melee = rig.GetComponent<MeleeCombat>();
        if (!melee.TryBeginSwing()) throw new InvalidOperationException("Actual melee begin refused");
        return Measure();
    }
    public static object Measure()
    {
        var rig = UnityEngine.Object.FindFirstObjectByType<PlayerRig>();
        // script run uses a fresh assembly; recover the id from the live component, not static state.
        var melee = rig != null ? rig.GetComponent<MeleeCombat>() : null;
        int swing = melee != null ? melee.LastSwingId : 0;
        if (!GameServices.TryGet<ICombatAuthority>(out var authority) ||
            !authority.TryGetMeleeSwingStatus(swing, out var status)) throw new InvalidOperationException("No accepted swing");
        var blade = rig.GetComponentInChildren<WeaponVisualRig>();
        return new { swingId = status.SwingId, frame = Time.frameCount, gameTime = Time.time, factor = rig.EffectiveStats.ActionSpeedFactor,
            phase = status.Phase.ToString(), elapsed = status.Elapsed,
            timing = new { windup = status.Timing.Windup, active = status.Timing.Active,
                recovery = status.Timing.Recovery, recoil = status.Timing.WallRecoil, repeat = status.RepeatInterval },
            blade = blade == null ? null : new { swinging = blade.IsSwinging, from = blade.BladeBase, to = blade.BladeTip } };
    }
}