# Modular Mech System

This folder contains the AC-style modular loadout skeleton.

## Core Flow

1. Create `MechPartDefinition` assets for heads, cores, arms, legs, boosters, internals, sensors, FCS, and weapons.
2. Create a `MechLoadoutDefinition` asset that assigns parts to slots.
3. Add `MechAssembler` and `MechRuntimeState` to a mech root GameObject.
4. Assign the loadout to `MechAssembler`.
5. Use `Assemble Mech` from the component context menu or `Tools/ACunicombat/Mech/Assemble Selected Mech`.

## Editor Menus

- `Tools/ACunicombat/Mech/Create Starter Sample Assets`
  Creates placeholder ScriptableObject data for a complete AC-style loadout. It does not copy external resources.
- `Tools/ACunicombat/Mech/Create Sample Mech Rig`
  Creates a scene rig with `Rigidbody`, `MechAssembler`, and `MechRuntimeState`, then assembles the sample placeholder loadout.
- `Tools/ACunicombat/Mech/Assemble Selected Mech`
  Rebuilds the selected GameObject if it has a `MechAssembler`.

## Debugging

- Select a GameObject with `MechAssembler` to see the custom inspector.
- Click `Assemble Now` to rebuild the mech from its current loadout.
- Click `Validate` to print loadout errors and warnings to the Console.
- Use the `Validation`, `Aggregated Stats`, and `Runtime State` foldouts to inspect the current loadout.
- Click `Add Debug Gizmos` to add `MechDebugGizmos`; with the mech selected, the Scene view draws assembled parts and mount points.
- If the debug inspector does not appear after pulling these files in, use `Assets/Refresh` or reopen the Unity project so Unity imports `MechAssemblerEditor.cs` and `MechDebugGizmos.cs`.

## Extension Points

- Flight reads `MechRuntimeState.AggregatedStats` for mass, thrust, reverse thrust, lateral thrust, and attitude torque.
- Sensors read radar range, refresh, precision, signatures, and track capacity.
- Fire control reads FCS quality, guidance channels, and track capacity.
- Garage UI edits a `MechLoadoutDefinition`, then calls `MechAssembler.Assemble()`.
