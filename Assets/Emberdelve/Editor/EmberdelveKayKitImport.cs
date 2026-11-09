using UnityEditor;
using UnityEngine;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// V2: model import settings for the KayKit packs under Art/ThirdParty/KayKit. Runs in the import pipeline (right on the
    /// very first import) and is also called by <see cref="EmberdelveVendorImport"/> after the import, because the animation
    /// take list is only known once the FBX has been imported once.
    ///
    ///   characters (Skeletons/Characters): Generic rig, avatar from the model, animations imported, bones kept (the weapon
    ///     is attached to a hand bone, so GameObject optimisation is off), looping flag on Idle / Walking / Running (and every
    ///     other idle take); every other take plays once.
    ///   everything else (weapons, dungeon props): no rig, no animation.
    ///   all: the FBX materials ("skeleton", "Glow", "texture") are remapped to the project materials that use the
    ///     Point-filtered KayKit atlases; no cameras, lights, colliders or blend shapes.
    /// </summary>
    public sealed class EmberdelveKayKitImport : AssetPostprocessor
    {
        public const string Root = "Assets/Emberdelve/Art/ThirdParty/KayKit/";
        public const string CharactersRoot = Root + "Skeletons/Characters/";

        public override uint GetVersion()
        {
            return 1;
        }

        private void OnPreprocessModel()
        {
            ModelImporter importer = assetImporter as ModelImporter;
            if (importer == null) return;
            Apply(importer, assetPath);
        }

        /// <summary>True for a take that should loop (idles, walks, runs).</summary>
        public static bool ShouldLoop(string clipName)
        {
            if (string.IsNullOrEmpty(clipName)) return false;
            string lower = clipName.ToLowerInvariant();
            return lower.Contains("idle") || lower.StartsWith("walking") || lower.StartsWith("running");
        }

        /// <summary>Applies the settings for the path. Returns true if anything had to change.</summary>
        public static bool Apply(ModelImporter importer, string path)
        {
            if (importer == null || string.IsNullOrEmpty(path) || !path.StartsWith(Root)) return false;

            bool changed = false;
            bool character = path.StartsWith(CharactersRoot);

            if (importer.importCameras) { importer.importCameras = false; changed = true; }
            if (importer.importLights) { importer.importLights = false; changed = true; }
            if (importer.importBlendShapes) { importer.importBlendShapes = false; changed = true; }
            if (importer.addCollider) { importer.addCollider = false; changed = true; }
            if (importer.isReadable) { importer.isReadable = false; changed = true; }
            if (importer.generateSecondaryUV) { importer.generateSecondaryUV = false; changed = true; }
            if (importer.meshCompression != ModelImporterMeshCompression.Off) { importer.meshCompression = ModelImporterMeshCompression.Off; changed = true; }

            // Player models retain exported animation takes; named clips are authored by the class builder.
            if (path.StartsWith(Root + "Adventurers/"))
            {
                if (importer.animationType != ModelImporterAnimationType.Legacy) { importer.animationType = ModelImporterAnimationType.Legacy; changed = true; }
                if (!importer.importAnimation) { importer.importAnimation = true; changed = true; }
                if (importer.optimizeGameObjects) { importer.optimizeGameObjects = false; changed = true; }
                return changed;
            }

            if (character)
            {
                if (importer.animationType != ModelImporterAnimationType.Generic) { importer.animationType = ModelImporterAnimationType.Generic; changed = true; }
                if (!importer.importAnimation) { importer.importAnimation = true; changed = true; }
                if (importer.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel) { importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel; changed = true; }
                if (importer.optimizeGameObjects) { importer.optimizeGameObjects = false; changed = true; }
                changed |= ApplyClipLoops(importer);
            }
            else
            {
                if (importer.animationType != ModelImporterAnimationType.None) { importer.animationType = ModelImporterAnimationType.None; changed = true; }
                if (importer.importAnimation) { importer.importAnimation = false; changed = true; }
            }

            changed |= ApplyMaterialRemaps(importer);
            return changed;
        }

        /// <summary>
        /// Loop flags. Uses the explicit clip list when there is one, otherwise the default list (one clip per take; empty
        /// until the FBX has been imported once, in which case nothing is done here and the vendor import calls this again).
        /// </summary>
        private static bool ApplyClipLoops(ModelImporter importer)
        {
            ModelImporterClipAnimation[] clips = importer.clipAnimations;
            bool fromDefault = false;
            if (clips == null || clips.Length == 0)
            {
                clips = importer.defaultClipAnimations;
                fromDefault = true;
            }

            if (clips == null || clips.Length == 0) return false;

            bool changed = fromDefault;
            for (int i = 0; i < clips.Length; i++)
            {
                bool loop = ShouldLoop(clips[i].name);
                if (clips[i].loopTime != loop) { clips[i].loopTime = loop; changed = true; }
                if (clips[i].loopPose != loop) { clips[i].loopPose = loop; changed = true; }
            }

            if (changed) importer.clipAnimations = clips;
            return changed;
        }

        private static bool ApplyMaterialRemaps(ModelImporter importer)
        {
            bool changed = false;
            changed |= Remap(importer, "skeleton", EmberdelveVendorImport.SkeletonMaterialPath);
            changed |= Remap(importer, "Glow", EmberdelveVendorImport.GlowMaterialPath);
            changed |= Remap(importer, "texture", EmberdelveVendorImport.DungeonMaterialPath);
            return changed;
        }

        private static bool Remap(ModelImporter importer, string sourceName, string materialPath)
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null) return false; // created later by the vendor import, which then re-applies the remaps

            AssetImporter.SourceAssetIdentifier id = new AssetImporter.SourceAssetIdentifier(typeof(Material), sourceName);
            var map = importer.GetExternalObjectMap();
            if (map.TryGetValue(id, out Object current) && current == material) return false;

            importer.AddRemap(id, material);
            return true;
        }
    }
}
