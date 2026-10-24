using System;
using System.Collections.Generic;
using UnityEngine;
namespace ACunicombat.Mechs
{
 [Serializable] public sealed class MechScenarioUnit
 {
  public string key,name;public bool player;public int team;
  public Vector3 position;public Quaternion rotation=Quaternion.identity;
  public MechLoadoutDefinition loadout;
 }
 [CreateAssetMenu(menuName="ACunicombat/Scenario",fileName="BattlefieldScenario")]
 public sealed class MechScenarioDefinition:ScriptableObject
 {
  public string scenarioId="current-battlefield",displayName="当前战场";
  public string scenePath="Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Scenes/AC6PlayerControl.unity";
  public List<MechScenarioUnit> units=new();
 }
}
