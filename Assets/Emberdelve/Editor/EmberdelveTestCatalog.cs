using UnityEditor;
using UnityEngine;
using Emberdelve.Data;
using Emberdelve.Items;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// R3 test-only support for gates that need ad-hoc ItemData (ScriptableObject.CreateInstance in a fixture).
    /// The production rule is untouched: the bag mints gear only for definitions its catalog owns. A fixture that
    /// wants a throwaway definition builds an in-memory TEST catalog (ItemCatalog.CreateForTest: the production
    /// items and affixes plus whatever the fixture registers) and hands that catalog to the bag. The production
    /// catalog asset is never modified (RegisterForTest refuses on it).
    /// </summary>
    internal static class EmberdelveTestCatalog
    {
        /// <summary>A fresh in-memory test catalog holding every production item and affix.</summary>
        public static ItemCatalog FromProduction()
        {
            ItemCatalog real = AssetDatabase.LoadAssetAtPath<ItemCatalog>(EmberdelveAssetLibrary.ItemCatalogPath);
            return ItemCatalog.CreateForTest(real != null ? real.Items : null, real != null ? real.Affixes : null);
        }

        /// <summary>
        /// Registers <paramref name="item"/> in the (lazily created) test catalog and makes the bag mint against
        /// that catalog. A definition that is already a production item is a harmless no-op registration.
        /// Returns false with the refusal reason when the catalog rejects the definition (for example a second
        /// object sharing an id), so a fixture mistake is reported instead of silently skipped.
        /// </summary>
        public static bool Adopt(ref ItemCatalog test, Inventory bag, ItemData item, out string reason)
        {
            if (test == null) test = FromProduction();
            if (!test.RegisterForTest(item, out reason)) return false;
            if (bag != null && bag.Catalog != test) bag.SetCatalog(test);
            return true;
        }

        public static void Release(ref ItemCatalog test)
        {
            if (test != null) Object.DestroyImmediate(test);
            test = null;
        }
    }
}
