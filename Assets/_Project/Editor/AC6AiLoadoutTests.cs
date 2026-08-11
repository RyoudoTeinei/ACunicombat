using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEditor;

namespace ACunicombat.Editor.Tests
{
    public sealed class AC6AiLoadoutTests
    {
        private const string CatalogPath =
            "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Resources/AC6FullPartCatalog.asset";
        private const string LegacyRiflePath =
            "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Parts/AC6_NATIVE_WP_R_1610.asset";

        [Test]
        public void CanonicalResolver_ReplacesLegacyUnconfiguredWeapon()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<MechPartCatalog>(
                CatalogPath);
            var legacyPart = AssetDatabase.LoadAssetAtPath<MechPartDefinition>(
                LegacyRiflePath);

            Assert.That(catalog, Is.Not.Null);
            Assert.That(legacyPart, Is.Not.Null);

            var canonicalPart =
                AC6PlayableSceneBuilder.ResolveCanonicalPartForLoadout(
                    legacyPart,
                    catalog);

            Assert.That(canonicalPart, Is.Not.Null);
            Assert.That(canonicalPart, Is.Not.SameAs(legacyPart));
            Assert.That(canonicalPart.PartId, Is.EqualTo(legacyPart.PartId));
            Assert.That(canonicalPart.WeaponPerformance.IsConfigured, Is.True);
        }
    }
}
