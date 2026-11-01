using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEngine;
public sealed class MechSubtitlePresenterTests
{
    [Test] public void FactionColorsAreDistinctAndEmptySubtitleClearsState()
    {
        Assert.AreNotEqual(MechSubtitlePresenter.FactionColor(SubtitleFaction.Friendly),MechSubtitlePresenter.FactionColor(SubtitleFaction.Hostile));
        Assert.AreNotEqual(MechSubtitlePresenter.FactionColor(SubtitleFaction.Friendly),MechSubtitlePresenter.FactionColor(SubtitleFaction.Player));
        MechSubtitlePresenter.Show("Wing","test",SubtitleFaction.Friendly,2);
        MechSubtitlePresenter.Show("","",SubtitleFaction.Neutral);
        var field=typeof(MechSubtitlePresenter).GetField("role",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
        Assert.AreEqual("",field.GetValue(null));
    }
}
