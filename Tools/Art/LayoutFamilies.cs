using Emberdelve.EditorTools;

// Narrow, explicit primary-editor authoring entry points; no scene or Play changes.
public static class LayoutFamilies
{
    public static void Apply() => DungeonLayoutFamilies.RebuildSaved();
    public static void PitJoints() => DungeonPitJointBacking.RepairSaved();
    public static void Arches() => ArchitecturalJoinRepair.RepairSaved();
    public static void Lobby() => LobbyMeshyBraziers.BakePrefab();
    public static void Braziers() => typeof(DungeonLayoutFamilies).Assembly
        .GetType("Emberdelve.EditorTools.DownloadedCryptArchitecture", true)
        .GetMethod("RepairSavedBraziers").Invoke(null, null);
    public static void Validate()
    {
        ArchitecturalJoinRepair.ValidateSaved();
        StairSurfaceSeparation.ValidateSaved();
        WeatheredTraversalSurfaces.ValidateSaved();
    }
}
