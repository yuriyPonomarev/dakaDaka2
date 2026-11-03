using UnityEditor;
using UnityEngine;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// V1: import settings for the ambientCG texture sets (and the procedural flame sprite). Runs in the import
    /// pipeline, so the settings are right on the very first import, including a fresh batchmode checkout.
    /// Naming contract: *_BaseColor (sRGB colour), *_Normal (OpenGL normal map), *_MaskMS (R metallic, A smoothness,
    /// linear), *_AO (linear).
    ///
    /// V2 (retro): every texture of the ambientCG sets and the KayKit atlases is Point filtered and capped at 512 px
    /// (ambientCG, mipmaps kept so distant stone does not shimmer; normal maps are Point filtered too, which makes the
    /// bump relief blocky like the colour, and the material's bump scale is lowered to compensate) or 256 px (KayKit
    /// atlases: no mipmaps, uncompressed, clamped). The flame sprite is intentionally untouched (it must stay soft).
    /// </summary>
    public sealed class EmberdelveTextureImport : AssetPostprocessor
    {
        public const string TextureRoot = "Assets/Emberdelve/Art/ThirdParty/ambientCG/";
        public const string FlameTexturePath = "Assets/Emberdelve/Art/Textures/Flame_Soft.png";
        public const string KayKitRoot = "Assets/Emberdelve/Art/ThirdParty/KayKit/";

        /// <summary>V2: largest edge of the ambientCG maps (the shipped 1024-2048 px sets are reduced on import).</summary>
        public const int RetroStoneMaxSize = 512;

        /// <summary>V2: largest edge of the KayKit palette atlases.</summary>
        public const int RetroKayKitMaxSize = 256;

        public override uint GetVersion()
        {
            return 2;
        }

        private void OnPreprocessTexture()
        {
            TextureImporter importer = assetImporter as TextureImporter;
            if (importer == null) return;
            Apply(importer, assetPath);
        }

        /// <summary>Applies the settings for the path. Returns true if anything had to change.</summary>
        public static bool Apply(TextureImporter importer, string path)
        {
            if (importer == null || string.IsNullOrEmpty(path)) return false;

            bool changed = false;

            if (path == FlameTexturePath)
            {
                if (importer.textureType != TextureImporterType.Default) { importer.textureType = TextureImporterType.Default; changed = true; }
                if (importer.wrapMode != TextureWrapMode.Clamp) { importer.wrapMode = TextureWrapMode.Clamp; changed = true; }
                if (!importer.mipmapEnabled) { importer.mipmapEnabled = true; changed = true; }
                if (!importer.sRGBTexture) { importer.sRGBTexture = true; changed = true; }
                if (importer.alphaSource != TextureImporterAlphaSource.FromInput) { importer.alphaSource = TextureImporterAlphaSource.FromInput; changed = true; }
                return changed;
            }

            if (path.StartsWith(KayKitRoot)) return ApplyKayKit(importer);
            if (!path.StartsWith(TextureRoot)) return false;

            string file = System.IO.Path.GetFileNameWithoutExtension(path);
            bool isNormal = file.EndsWith("_Normal");
            bool isLinearData = file.EndsWith("_MaskMS") || file.EndsWith("_AO");
            bool isMask = file.EndsWith("_MaskMS");

            if (isNormal)
            {
                if (importer.textureType != TextureImporterType.NormalMap)
                {
                    importer.textureType = TextureImporterType.NormalMap;
                    changed = true;
                }
            }
            else
            {
                if (importer.textureType != TextureImporterType.Default)
                {
                    importer.textureType = TextureImporterType.Default;
                    changed = true;
                }

                bool wantSrgb = !isLinearData;
                if (importer.sRGBTexture != wantSrgb)
                {
                    importer.sRGBTexture = wantSrgb;
                    changed = true;
                }
            }

            if (isMask)
            {
                // Smoothness lives in the alpha channel: keep it as data, never as transparency.
                if (importer.alphaSource != TextureImporterAlphaSource.FromInput)
                {
                    importer.alphaSource = TextureImporterAlphaSource.FromInput;
                    changed = true;
                }

                if (importer.alphaIsTransparency)
                {
                    importer.alphaIsTransparency = false;
                    changed = true;
                }
            }

            if (importer.wrapMode != TextureWrapMode.Repeat)
            {
                importer.wrapMode = TextureWrapMode.Repeat;
                changed = true;
            }

            // V2: Point filtering (was Trilinear + aniso 8 in V1).
            if (importer.filterMode != FilterMode.Point)
            {
                importer.filterMode = FilterMode.Point;
                changed = true;
            }

            if (importer.anisoLevel != 1)
            {
                importer.anisoLevel = 1;
                changed = true;
            }

            if (!importer.mipmapEnabled)
            {
                importer.mipmapEnabled = true;
                changed = true;
            }

            if (importer.maxTextureSize != RetroStoneMaxSize)
            {
                importer.maxTextureSize = RetroStoneMaxSize;
                changed = true;
            }

            if (importer.isReadable)
            {
                importer.isReadable = false;
                changed = true;
            }

            return changed;
        }

        /// <summary>V2: the KayKit palette atlases (1024 px as shipped): Point, 256 px, no mipmaps, uncompressed, clamped.</summary>
        private static bool ApplyKayKit(TextureImporter importer)
        {
            bool changed = false;

            if (importer.textureType != TextureImporterType.Default) { importer.textureType = TextureImporterType.Default; changed = true; }
            if (!importer.sRGBTexture) { importer.sRGBTexture = true; changed = true; }
            if (importer.filterMode != FilterMode.Point) { importer.filterMode = FilterMode.Point; changed = true; }
            if (importer.wrapMode != TextureWrapMode.Clamp) { importer.wrapMode = TextureWrapMode.Clamp; changed = true; }
            if (importer.anisoLevel != 1) { importer.anisoLevel = 1; changed = true; }
            if (importer.mipmapEnabled) { importer.mipmapEnabled = false; changed = true; }
            if (importer.maxTextureSize != RetroKayKitMaxSize) { importer.maxTextureSize = RetroKayKitMaxSize; changed = true; }
            if (importer.textureCompression != TextureImporterCompression.Uncompressed) { importer.textureCompression = TextureImporterCompression.Uncompressed; changed = true; }
            if (importer.alphaIsTransparency) { importer.alphaIsTransparency = false; changed = true; }
            if (importer.isReadable) { importer.isReadable = false; changed = true; }

            return changed;
        }
    }
}
