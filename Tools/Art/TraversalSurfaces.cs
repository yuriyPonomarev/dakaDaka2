using Emberdelve.EditorTools;

// Small Unity CLI script-run bridge after the Editor assembly has recompiled.
// These commands load isolated prefab contents; they never enter/exit Play or open an active scene.
public static class TraversalSurfaces
{
    public static void Export() => WeatheredTraversalSurfaces.ExportDimensions();
    public static void Import() => WeatheredTraversalSurfaces.ImportAndApply();
    public static void Validate() => WeatheredTraversalSurfaces.ValidateSaved();
}
