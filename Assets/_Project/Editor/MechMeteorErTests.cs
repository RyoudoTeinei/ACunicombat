using ACunicombat.Mechs;
using NUnit.Framework;
public class MechMeteorErTests
{
    [TestCase("DPL-15","火石 15")] [TestCase("DPL-17","火石 17")]
    [TestCase("LRA-4230","SIM-240 LR")] [TestCase("AAM-4301C","天空之手")]
    [TestCase("MTR-4340","流星")] [TestCase("MTR-4340-ER","流星-ER")]
    public void StableIdentifiersExposeRequestedNames(string id,string name)=>Assert.AreEqual(name,MechMissileAmmunitionCatalog.Create(id).missileDisplayName);
    [TestCase(30)] [TestCase(60)]
    public void ExtendedMeteorReaches340Km(int hz)=>MechLongRangeMissileTests.AssertFlightEnvelope(MechMissileAmmunitionCatalog.Create("MTR-4340-ER"),"流星-ER",hz,1f);
    [Test] public void MeteorVariantsKeepOriginalAndCompatibleOldSelection()
    {
        var d=MechMissileLauncherCatalog.Find("WP_L_4340");var p=MechMissileLauncherCatalog.Compose(d,"MTR-4340");
        Assert.AreEqual(300000,p.maximumRangeMeters);Assert.AreEqual(2,MechMissileVariantCatalog.GetVariantCount("WP_L_4340",p));
        Assert.AreEqual("流星",MechMissileVariantCatalog.ResolveVariant("WP_L_4340",p,"standard").DisplayName);
        var er=MechMissileVariantCatalog.ResolveVariant("WP_L_4340",p,"MTR-4340-ER").Performance;
        Assert.AreEqual(340000,er.maximumRangeMeters);Assert.IsTrue(er.supportsPassiveRfHoming);
        Assert.AreEqual(p.magazineCapacity,er.magazineCapacity);Assert.Greater(er.missileFlightProfile.SecondStagePropellantMassKg,p.missileFlightProfile.SecondStagePropellantMassKg);
    }
}
