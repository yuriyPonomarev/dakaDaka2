using UnityEditor;
using UnityEngine;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// V1: import settings for the ambientCG texture sets (and the procedural flame sprite). Runs in the import
    /// pipeline, so the settings are right on the very first import, including a fresh batchmode checkout.
    /// Naming contract: *_BaseColor (sRGB colour), *_Normal (OpenGL normal map), *_MaskMS (R metallic, A smoothness,
    /// linear), *_AO (linear).
    /// </summary>
    public sealed class EmberdelveTextureImport : AssetPostprocessor
    {
        public const string TextureRoot = "Assets/Emberdelve/Art/ThirdParty/ambientCG/";
        public const string FlameTexturePath = "Assets/Emberdelve/Art/Textures/Flame_Soft.png";

        public override uint GetVersion()
        {
            return 1;
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

            if (importer.filterMode != FilterMode.Trilinear)
            {
                importer.filterMode = FilterMode.Trilinear;
                changed = true;
            }

            if (importer.anisoLevel != 8)
            {
                importer.anisoLevel = 8;
                changed = true;
            }

            if (!importer.mipmapEnabled)
            {
                importer.mipmapEnabled = true;
                changed = true;
            }

            if (importer.maxTextureSize != 2048)
            {
                importer.maxTextureSize = 2048;
                changed = true;
            }

            if (importer.isReadable)
            {
                importer.isReadable = false;
                changed = true;
            }

            return changed;
        }
    }
}
