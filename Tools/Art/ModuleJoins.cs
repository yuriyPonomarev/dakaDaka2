using Emberdelve.EditorTools;
public static class ModuleJoins {
 public static void Apply() { ArchitecturalJoinRepair.RepairSaved(); StairSurfaceSeparation.RepairSaved(); typeof(ArchitecturalJoinRepair).Assembly.GetType("Emberdelve.EditorTools.DungeonDoorAuthoring", true).GetMethod("RepairSavedVisuals").Invoke(null, null); }
 public static void Doors() => typeof(ArchitecturalJoinRepair).Assembly.GetType("Emberdelve.EditorTools.DungeonDoorAuthoring", true).GetMethod("RepairSavedVisuals").Invoke(null, null);
 public static void Validate() { ArchitecturalJoinRepair.ValidateSaved(); StairSurfaceSeparation.ValidateSaved(); }
}
