# Modular Mech System

This folder contains the AC-style modular loadout skeleton.

## Core Flow

1. Create `MechPartDefinition` assets for heads, cores, arms, legs, boosters, internals, sensors, FCS, and weapons.
2. Create a `MechLoadoutDefinition` asset that assigns parts to slots.
3. Add `MechAssembler` and `MechRuntimeState` to a mech root GameObject.
4. Assign the loadout to `MechAssembler`.
5. Use `Assemble Mech` from the component context menu or `Tools/ACunicombat/Mech/Assemble Selected Mech`.

## Skinned Modular Parts

- Set a frame part's `Visual Rig Mode` to `Synchronized Skinned`.
- Keep the original GLB skeleton and skin weights in that part's model prefab.
- Assign the bind-relative `Skinned Animation Clip` exported for that exact part.
- `MechAssembler` creates one Animator per skinned part and registers all of them with `MechSynchronizedAnimationPlayer`.
- The player evaluates all part-specific clips from one manual Playables clock. After evaluation, explicit interface ownership rules are applied in dependency order: the selected legs drive the core's 4 root-interface bones, then the aligned core drives 10 head ancestors and 20 arm ancestors. Part-exclusive bones and skin weights remain untouched.
- Do not let each part animate its inherited body chain independently: a 2010 head otherwise carries 2010 core offsets when mounted on a 1010 core and visibly separates.
- Use a mount point's `Source Transform Name Suffix` to bind equipment to an imported animated dummy node. `Match All Source Transforms` registers duplicate dummy references such as the three `_dmy_800` booster sockets.
- Enable `Instantiate Visual At All Mounts` for logical parts such as a booster nozzle that need several visual instances while contributing gameplay stats only once.
- Mounted weapons and boosters keep their own imported skeleton and native clips. Put their action list on `MechPartDefinition.EquipmentAnimations`; `MechAssembler` creates a `MechEquipmentAnimationPlayer` for every mounted visual instance.
- Call `MechEquipmentAnimationPlayer.PlayAction(actionId)` for firing, reloading, deployment, or booster mechanism actions. Equipment playback is independent from the shared frame-part locomotion clock.
- Native equipment GLBs preserve both FLVER vertex spaces: dynamic meshes use inverse-bind skinning, while `Mesh.Dynamic == 0` rigid pieces use an identity-IBM reference-space skin. Do not collapse those skins or weapon scale tracks can explode the mesh.
- Use `Tools/ACunicombat/AC6/Build And Validate Skinned Modular Samples` for the local 1010/2010/3010 validation case.
- The generated validation loadouts use only native FLVER/HKX/DDS-derived GLBs: three animated booster visuals plus a left-hand weapon and both back weapons. Blender-baked equipment and its generated Unity materials are legacy-only and must not be referenced by current loadouts.

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
- Animation state selection should change the synchronized player's set of part-specific clips while preserving one shared playback time.
- Weapon state machines should select actions on each equipment player without changing the synchronized frame-part player.
