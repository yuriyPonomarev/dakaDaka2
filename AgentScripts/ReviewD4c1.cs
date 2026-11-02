using System;
using System.Collections.Generic;
using Emberdelve.AI;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Player;
using Emberdelve.UI;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

// Graphical, Play-mode-only full-window UI probe. Local verifier executes it; saves no assets/settings.
public static class ReviewD4c1
{
    public static object Prepare()
    {
        RequireGraphics();
        PlayerRig rig = UnityEngine.Object.FindFirstObjectByType<PlayerRig>();
        if (rig == null || rig.Inventory == null || rig.Equipment == null) throw new InvalidOperationException("No live dungeon rig");
        foreach (EnemyAgent enemy in UnityEngine.Object.FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None)) enemy.enabled = false;
        if (rig.Loadout.ActiveSlot != 0) rig.Loadout.ForceSwitchToSlot(0);
        rig.Equipment.Clear(); rig.Inventory.Clear(); rig.Inventory.ConfigureGrid(10, 6);
        // Same authored full outfit as the accepted D3 rendered review, including both weapon sets.
        string[] ids = { "boiled_cuirass", "footpad_boots", "ashen_cloak", "graspwrap_gloves", "emberbead_necklace",
            "cinder_band_ring", "cinder_band_ring", "ember_fang_blade", "ember_fang_blade", "emberbulwark_shield", "emberbulwark_shield" };
        EquipmentSlot[] slots = { EquipmentSlot.Chest, EquipmentSlot.Feet, EquipmentSlot.Back, EquipmentSlot.Hands,
            EquipmentSlot.Necklace, EquipmentSlot.RingLeft, EquipmentSlot.RingRight, EquipmentSlot.Weapon,
            EquipmentSlot.AltWeapon, EquipmentSlot.OffHand, EquipmentSlot.AltOffHand };
        for (int i = 0; i < ids.Length; i++)
        {
            if (!rig.Inventory.Catalog.TryResolveItem(ids[i], out ItemData item, out string reason) ||
                !rig.Inventory.TryAdd(item, 1, out reason) ||
                !rig.Equipment.TryEquipFromInventory(rig.Inventory.IndexOfItem(item), slots[i], out reason))
                throw new InvalidOperationException(reason);
        }
        InventoryScreenController screen = UnityEngine.Object.FindFirstObjectByType<InventoryScreenController>();
        if (screen == null) throw new InvalidOperationException("No actual inventory controller");
        screen.Open();
        RectTransform root = Root(); bool found = false;
        foreach (Button button in root.GetComponentsInChildren<Button>(true))
            if (button.name == "TabCharacter") { button.onClick.Invoke(); found = true; break; }
        if (!found) throw new InvalidOperationException("Actual Character tab button missing");
        screen.Rebuild(); Canvas.ForceUpdateCanvases(); return new { prepared = true, frame = Time.frameCount, gameTime = Time.time };
    }
    public static object SmallBag() => SetBagSize(4);
    public static object TallBag() => SetBagSize(6);
    private static object SetBagSize(int height)
    {
        RequireGraphics();
        PlayerRig rig = UnityEngine.Object.FindFirstObjectByType<PlayerRig>();
        if (rig == null || rig.IsDead || rig.Inventory == null || rig.Inventory.TotalUnits != 0)
            throw new InvalidOperationException("A living player with an empty carried bag is required");
        rig.Inventory.ConfigureGrid(10, height);
        if (rig.Inventory.GridWidth != 10 || rig.Inventory.GridHeight != height)
            throw new InvalidOperationException("Bag resize was refused");
        return new { width = rig.Inventory.GridWidth, height = rig.Inventory.GridHeight, frame = Time.frameCount };
    }
    public static object Measure()
    {
        RequireGraphics();
        InventoryScreenController screen = UnityEngine.Object.FindFirstObjectByType<InventoryScreenController>();
        PlayerRig rig = UnityEngine.Object.FindFirstObjectByType<PlayerRig>();
        if (screen == null || !screen.IsOpen || rig == null) throw new InvalidOperationException("Actual inventory must be open");
        Canvas.ForceUpdateCanvases();
        RectTransform root = Root();
        RectTransform panel = root.Find("SatchelPanel") as RectTransform;
        RectTransform pane = root.Find("StatsGroup/StatsPane") as RectTransform;
        Transform rows = root.Find("StatsGroup/CharacterRows");
        if (panel == null || pane == null || rows == null || !rows.gameObject.activeInHierarchy)
            throw new InvalidOperationException("Actual character pane is not visible");
        Rect viewport = new Rect(0, 0, Screen.width, Screen.height);
        Rect panelBounds = Bounds(panel), paneBounds = Bounds(pane);
        var failures = new List<string>(); var measurements = new List<object>();
        int labels = 0, values = 0, equipment = 0; string opening = null;
        foreach (Text text in root.GetComponentsInChildren<Text>(true))
        {
            bool statLabel = text.name.StartsWith("StatLabel", StringComparison.Ordinal);
            bool statValue = text.name.StartsWith("StatValue", StringComparison.Ordinal);
            bool equip = text.name.StartsWith("EquipLabel_", StringComparison.Ordinal);
            if (!(statLabel || statValue || equip) || !text.gameObject.activeInHierarchy) continue;
            if (statLabel) labels++; if (statValue) values++; if (equip) equipment++;
            if (text.name == "StatValue13") opening = text.text;
            float width = text.preferredWidth, height = text.preferredHeight;
            Rect rect = text.rectTransform.rect, bounds = Bounds(text.rectTransform);
            bool realMetrics = text.font != null && width > 0f && height > 0f;
            bool fits = realMetrics && width <= rect.width + .5f && height <= rect.height + .5f;
            bool onScreen = Contains(viewport, bounds, 2f), inPanel = Contains(panelBounds, bounds, 2f);
            bool inPane = equip || Contains(paneBounds, bounds, 2f);
            if (!realMetrics) failures.Add(text.name + ": zero/unavailable font metrics");
            if (!fits) failures.Add(text.name + ": text exceeds its rectangle");
            if (!onScreen || !inPanel || !inPane) failures.Add(text.name + ": outside window/panel/pane");
            measurements.Add(new { name = text.name, text = text.text, width = rect.width, height = rect.height,
                preferredWidth = width, preferredHeight = height, realMetrics, fits, onScreen, inPanel, inPane,
                screenX = bounds.x, screenY = bounds.y, screenWidth = bounds.width, screenHeight = bounds.height });
        }
        if (labels != 14 || values != 14) failures.Add("Expected14 real stat labels and14 values");
        if (equipment != 13) failures.Add("Expected13 real equipment labels");
        if (rows.Find("StatLabel13")?.GetComponent<Text>().text != "Chest opening") failures.Add("Final row label missing");
        string expected = rig.EffectiveStats.InteractionSpeedFactor.ToString("0.##") + "x";
        if (opening != expected) failures.Add("Actual opening value disagrees with effective capture");
        // The satchel background must cover the header and both tabs even for a tall bag.
        foreach (string child in new[] { "Header", "TabBag", "TabCharacter" })
        {
            RectTransform element = root.Find(child) as RectTransform;
            if (element == null || !element.gameObject.activeInHierarchy || !Contains(panelBounds, Bounds(element), 2f))
                failures.Add(child + ": outside satchel background");
        }
        Image panelImage = panel.GetComponent<Image>();
        if (panelImage == null || !panelImage.enabled || panelImage.color.a < 0.999f)
            failures.Add("Satchel background must be opaque");
        return new { pass = failures.Count == 0, failures, frame = Time.frameCount, gameTime = Time.time,
            width = Screen.width, height = Screen.height, graphics = SystemInfo.graphicsDeviceType.ToString(), panelWidth = panel.rect.width, panelHeight = panel.rect.height,
            statLabels = labels, statValues = values, equipmentLabels = equipment, opening, expected, measurements };
    }
    private static void RequireGraphics()
    {
        if (!Application.isPlaying) throw new InvalidOperationException("Enter Play mode first");
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || Application.isBatchMode)
            throw new InvalidOperationException("Full-window font/layout proof requires an owned graphical Editor");
    }
    private static RectTransform Root()
    {
        RectTransform result = null;
        foreach (RectTransform rect in UnityEngine.Object.FindObjectsByType<RectTransform>(FindObjectsSortMode.None))
            if (rect.name == "InventoryRoot" && rect.gameObject.activeInHierarchy)
            {
                if (result != null) throw new InvalidOperationException("Multiple active inventory roots");
                result = rect;
            }
        if (result == null) throw new InvalidOperationException("Actual inventory root missing");
        return result;
    }
    private static Rect Bounds(RectTransform rect)
    {
        Vector3[] corners = new Vector3[4]; rect.GetWorldCorners(corners);
        Canvas canvas = rect.GetComponentInParent<Canvas>();
        Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        Vector2 first = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
        float minX = first.x, minY = first.y, maxX = first.x, maxY = first.y;
        for (int i = 1; i < corners.Length; i++)
        {
            Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, corners[i]);
            minX = Mathf.Min(minX, point.x); minY = Mathf.Min(minY, point.y);
            maxX = Mathf.Max(maxX, point.x); maxY = Mathf.Max(maxY, point.y);
        }
        return Rect.MinMaxRect(minX, minY, maxX, maxY);
    }
    private static bool Contains(Rect outer, Rect inner, float slack) =>
        inner.xMin >= outer.xMin - slack && inner.yMin >= outer.yMin - slack &&
        inner.xMax <= outer.xMax + slack && inner.yMax <= outer.yMax + slack;
}
