using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// First playable zero-g controller for the modular AC6 test rig. Translation is inertial,
    /// attitude is yaw/pitch only, and frame and weapon animations keep their independent layers.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AC6PlayerMechController : MonoBehaviour
    {
        private const string IdleActionId = "a000_000000";
        private const string ForwardBoostActionId = "a000_120200";
        private const string LeftQuickBoostActionId = "a000_028002";
        private const string RightQuickBoostActionId = "a000_028003";
        private const string RaiseWeaponActionId = "a200_030020";
        private const string HoldWeaponActionId = "a200_030022";
        private const string FireWeaponActionId = "a200_030030";
        private const float ModelYawOffset = 180f;

        private static readonly string[] UpperBodyBoneNames =
        {
            "Spine1", "Neck", "Offset_ClavicleLink", "Spine1_EX", "Head",
            "L_ClavicleLink", "R_ClavicleLink", "L_ClavicleLink_EX", "Offset_L_Clavicle",
            "Offset_R_Clavicle", "R_ClavicleLink_EX", "L_Clavicle", "R_Clavicle",
            "L_ArmJoint", "R_ArmJoint", "L_ClavicleEX_A", "L_ClavicleEX_B",
            "Root_L_UpperArm", "R_ClavicleEX_A", "R_ClavicleEX_B", "Root_R_UpperArm",
            "L_UpperArm", "R_UpperArm", "L_ForeArm", "L_UpArmEX_A", "L_UpArmEX_B",
            "L_UpArmTwist", "R_ForeArm", "R_UpArmEX_A", "R_UpArmEX_B", "R_UpArmTwist",
            "L_Elbow", "L_ForeArmTwist", "L_HandTwist", "L_Wrist", "R_Elbow",
            "R_ForeArmTwist", "R_HandTwist", "R_Wrist", "Root_L_Hand", "Root_R_Hand",
            "L_Hand", "R_Hand", "L_Finger0", "L_Finger1", "L_Finger2", "L_Finger3",
            "L_Finger4", "L_Weapon_00", "R_Finger0", "R_Finger1", "R_Finger2",
            "R_Finger3", "R_Finger4", "R_Weapon_00", "L_Finger01", "L_Finger11",
            "L_Finger21", "L_Finger31", "L_Finger41", "R_Finger01", "R_Finger11",
            "R_Finger21", "R_Finger31", "R_Finger41", "L_Finger02", "L_Finger12",
            "L_Finger22", "L_Finger32", "L_Finger42", "R_Finger02", "R_Finger12",
            "R_Finger22", "R_Finger32", "R_Finger42"
        };

        [Header("Scene bindings")]
        [SerializeField] private GameObject mech;
        [SerializeField] private Camera playerCamera;
        [SerializeField] private MechActionRegistry actionRegistry;

        [Header("Flight")]
        [SerializeField, Min(0.1f)] private float translationAcceleration = 18f;
        [SerializeField, Min(1f)] private float normalSpeedLimit = 80f;
        [SerializeField, Min(1f)] private float assaultBoostAcceleration = 48f;
        [SerializeField, Min(1f)] private float assaultBoostSpeedLimit = 170f;
        [SerializeField, Min(1f)] private float quickBoostVelocityChange = 34f;
        [SerializeField, Min(0.05f)] private float quickBoostCooldown = 0.38f;
        [SerializeField, Min(0.1f)] private float turnResponse = 12f;

        [Header("View")]
        [SerializeField, Min(0.001f)] private float mouseSensitivity = 0.09f;
        [SerializeField, Min(1f)] private float cameraDistance = 17f;
        [SerializeField] private float cameraHeight = 5.2f;
        [SerializeField, Min(0.1f)] private float cameraFollowResponse = 14f;

        [Header("Input timing")]
        [SerializeField, Min(0.1f)] private float assaultBoostDoubleTapWindow = 0.28f;
        [SerializeField, Min(0.05f)] private float weaponRaiseSeconds = 0.12f;
        [SerializeField, Min(0.05f)] private float weaponFireSeconds = 0.22f;

        private readonly List<MechEquipmentAnimationPlayer> boosterPlayers = new();
        private readonly List<ParticleSystem> boosterExhausts = new();
        private MechRuntimeState runtimeState;
        private MechBodyActionPlayer bodyActionPlayer;
        private MechEquipmentAnimationPlayer leftWeaponPlayer;
        private MechEquipmentAnimationPlayer rightWeaponPlayer;
        private Rigidbody mechBody;
        private Vector2 moveInput;
        private bool ascendInput;
        private bool assaultBoostActive;
        private bool quickBoostRequested;
        private float yaw;
        private float pitch;
        private float lastSpacePressTime = float.NegativeInfinity;
        private float nextQuickBoostTime;
        private float locomotionLockUntil;
        private string requestedLocomotionAction = string.Empty;
        private string activeBoosterAction = string.Empty;
        private bool wasAiming;
        private float aimPhaseEndsAt;
        private float firePhaseEndsAt;
        private bool holdActionPlayed;

        public bool AssaultBoostActive => assaultBoostActive;
        public Vector3 Velocity => mechBody == null ? Vector3.zero : mechBody.linearVelocity;

        public void Configure(GameObject mechRoot, Camera sceneCamera, MechActionRegistry registry)
        {
            mech = mechRoot;
            playerCamera = sceneCamera;
            actionRegistry = registry;
        }

        private void Start()
        {
            if (mech == null)
            {
                mech = gameObject;
            }

            ConfigurePhysics();
            BindAnimationSystems();
            BindEquipment();
            BuildBoosterExhausts();

            var visualForward = -(mech.transform.rotation * Vector3.forward);
            yaw = Mathf.Atan2(visualForward.x, visualForward.z) * Mathf.Rad2Deg;
            pitch = -Mathf.Asin(
                Mathf.Clamp(visualForward.y, -1f, 1f)) * Mathf.Rad2Deg;
            LockCursor();
            Debug.Log(
                $"[AC6Player] Ready. bodyGraph={(bodyActionPlayer != null && bodyActionPlayer.IsGraphValid)} " +
                $"leftWeapon={(leftWeaponPlayer != null)} rightWeapon={(rightWeaponPlayer != null)} " +
                $"boosters={boosterPlayers.Count} exhausts={boosterExhausts.Count}.",
                mech);
        }

        private void Update()
        {
            var keyboard = Keyboard.current;
            var mouse = Mouse.current;
            if (keyboard == null || mouse == null)
            {
                return;
            }

            HandleCursor(keyboard, mouse);
            ReadView(mouse);
            ReadMovement(keyboard);
            HandleWeapons(mouse);
            UpdateLocomotionAnimation();
            UpdateBoosterEffects();
        }

        private void FixedUpdate()
        {
            if (mechBody == null)
            {
                return;
            }

            var viewRotation = Quaternion.Euler(pitch, yaw, 0f);
            var targetRotation = viewRotation * Quaternion.Euler(0f, ModelYawOffset, 0f);
            var rotationT = 1f - Mathf.Exp(-turnResponse * Time.fixedDeltaTime);
            mechBody.MoveRotation(Quaternion.Slerp(mechBody.rotation, targetRotation, rotationT));
            mechBody.angularVelocity = Vector3.zero;

            var localThrust = new Vector3(moveInput.x, 0f, moveInput.y);
            var thrust = viewRotation * localThrust;
            if (ascendInput && !assaultBoostActive)
            {
                thrust += Vector3.up;
            }

            if (thrust.sqrMagnitude > 1f)
            {
                thrust.Normalize();
            }

            if (assaultBoostActive)
            {
                mechBody.AddForce(viewRotation * Vector3.forward * assaultBoostAcceleration,
                    ForceMode.Acceleration);
            }
            else if (thrust.sqrMagnitude > 0.0001f)
            {
                mechBody.AddForce(thrust * translationAcceleration, ForceMode.Acceleration);
            }

            if (quickBoostRequested)
            {
                var quickDirection = thrust.sqrMagnitude > 0.0001f
                    ? thrust.normalized
                    : viewRotation * Vector3.forward;
                mechBody.AddForce(quickDirection * quickBoostVelocityChange, ForceMode.VelocityChange);
                quickBoostRequested = false;
            }

            var speedLimit = assaultBoostActive ? assaultBoostSpeedLimit : normalSpeedLimit;
            if (mechBody.linearVelocity.sqrMagnitude > speedLimit * speedLimit)
            {
                mechBody.linearVelocity = mechBody.linearVelocity.normalized * speedLimit;
            }
        }

        private void LateUpdate()
        {
            if (playerCamera == null || mech == null)
            {
                return;
            }

            var viewRotation = Quaternion.Euler(pitch, yaw, 0f);
            var focus = mech.transform.position + Vector3.up * cameraHeight;
            var desiredPosition = focus - viewRotation * Vector3.forward * cameraDistance;
            var followT = 1f - Mathf.Exp(-cameraFollowResponse * Time.deltaTime);
            playerCamera.transform.position = Vector3.Lerp(
                playerCamera.transform.position,
                desiredPosition,
                followT);
            playerCamera.transform.rotation = viewRotation;
        }

        private void ConfigurePhysics()
        {
            mechBody = mech.GetComponent<Rigidbody>();
            if (mechBody == null)
            {
                mechBody = mech.AddComponent<Rigidbody>();
            }

            mechBody.useGravity = false;
            mechBody.isKinematic = false;
            mechBody.linearDamping = 0f;
            mechBody.angularDamping = 8f;
            mechBody.interpolation = RigidbodyInterpolation.Interpolate;
            mechBody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            mechBody.constraints = RigidbodyConstraints.FreezeRotation;
        }

        private void BindAnimationSystems()
        {
            runtimeState = mech.GetComponent<MechRuntimeState>();
            if (runtimeState == null || actionRegistry == null)
            {
                Debug.LogError("[AC6Player] Runtime state or action registry is missing.", mech);
                return;
            }

            var legacyPlayer = mech.GetComponent<MechSynchronizedAnimationPlayer>();
            if (legacyPlayer != null)
            {
                legacyPlayer.StopPlayback();
                legacyPlayer.enabled = false;
            }

            bodyActionPlayer = mech.GetComponent<MechBodyActionPlayer>();
            if (bodyActionPlayer == null)
            {
                bodyActionPlayer = mech.AddComponent<MechBodyActionPlayer>();
            }

            bodyActionPlayer.Configure(
                actionRegistry,
                runtimeState,
                UpperBodyBoneNames,
                IdleActionId,
                1f);
            requestedLocomotionAction = IdleActionId;
        }

        private void BindEquipment()
        {
            boosterPlayers.Clear();
            if (runtimeState == null)
            {
                return;
            }

            for (var i = 0; i < runtimeState.AssembledParts.Count; i++)
            {
                var record = runtimeState.AssembledParts[i];
                if (record?.instance == null)
                {
                    continue;
                }

                var player = record.instance.GetComponent<MechEquipmentAnimationPlayer>();
                switch (record.slot)
                {
                    case MechPartSlot.LeftHandWeapon:
                        leftWeaponPlayer = player;
                        break;
                    case MechPartSlot.RightHandWeapon:
                        rightWeaponPlayer = player;
                        break;
                    case MechPartSlot.Booster:
                        boosterPlayers.AddRange(record.instance
                            .GetComponentsInChildren<MechEquipmentAnimationPlayer>(true));
                        break;
                }
            }

            boosterPlayers.RemoveAll(candidate => candidate == null);
            for (var i = boosterPlayers.Count - 1; i >= 0; i--)
            {
                if (boosterPlayers.IndexOf(boosterPlayers[i]) != i)
                {
                    boosterPlayers.RemoveAt(i);
                }
            }
        }

        private void HandleCursor(Keyboard keyboard, Mouse mouse)
        {
            if (keyboard.escapeKey.wasPressedThisFrame)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else if (mouse.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked)
            {
                LockCursor();
            }
        }

        private void ReadView(Mouse mouse)
        {
            if (Cursor.lockState != CursorLockMode.Locked)
            {
                return;
            }

            var delta = mouse.delta.ReadValue();
            yaw += delta.x * mouseSensitivity;
            pitch = Mathf.DeltaAngle(
                0f,
                pitch - delta.y * mouseSensitivity);
        }

        private void ReadMovement(Keyboard keyboard)
        {
            moveInput = new Vector2(
                ReadAxis(keyboard.aKey, keyboard.dKey),
                ReadAxis(keyboard.sKey, keyboard.wKey));
            moveInput = Vector2.ClampMagnitude(moveInput, 1f);
            ascendInput = keyboard.spaceKey.isPressed;

            if (keyboard.spaceKey.wasPressedThisFrame)
            {
                var now = Time.unscaledTime;
                if (now - lastSpacePressTime <= assaultBoostDoubleTapWindow)
                {
                    assaultBoostActive = !assaultBoostActive;
                    lastSpacePressTime = float.NegativeInfinity;
                    if (assaultBoostActive)
                    {
                        PlayLocomotion(ForwardBoostActionId, true);
                    }
                }
                else
                {
                    lastSpacePressTime = now;
                }
            }

            if (keyboard.sKey.wasPressedThisFrame)
            {
                assaultBoostActive = false;
            }

            if (keyboard.leftShiftKey.wasPressedThisFrame && Time.time >= nextQuickBoostTime)
            {
                nextQuickBoostTime = Time.time + quickBoostCooldown;
                quickBoostRequested = true;
                assaultBoostActive = false;
                locomotionLockUntil = Time.time + 0.32f;
                var actionId = moveInput.x < -0.1f
                    ? LeftQuickBoostActionId
                    : moveInput.x > 0.1f
                        ? RightQuickBoostActionId
                        : ForwardBoostActionId;
                PlayLocomotion(actionId, true);
                SpawnBurstAtBoosters(1.7f);
            }
        }

        private void HandleWeapons(Mouse mouse)
        {
            var aiming = mouse.leftButton.isPressed || mouse.rightButton.isPressed;
            if (aiming && !wasAiming)
            {
                bodyActionPlayer?.PlayUpperBody(RaiseWeaponActionId);
                aimPhaseEndsAt = Time.time + weaponRaiseSeconds;
                holdActionPlayed = false;
            }

            if (mouse.leftButton.wasPressedThisFrame)
            {
                FireWeapon(leftWeaponPlayer, true);
            }
            if (mouse.rightButton.wasPressedThisFrame)
            {
                FireWeapon(rightWeaponPlayer, false);
            }

            if (aiming && Time.time >= firePhaseEndsAt && Time.time >= aimPhaseEndsAt && !holdActionPlayed)
            {
                holdActionPlayed = bodyActionPlayer != null &&
                                   bodyActionPlayer.PlayUpperBody(HoldWeaponActionId);
            }
            else if (!aiming && wasAiming)
            {
                bodyActionPlayer?.ClearUpperBody(0.1f);
                holdActionPlayed = false;
            }

            wasAiming = aiming;
        }

        private void FireWeapon(MechEquipmentAnimationPlayer weaponPlayer, bool leftHand)
        {
            bodyActionPlayer?.PlayUpperBody(FireWeaponActionId);
            firePhaseEndsAt = Time.time + weaponFireSeconds;
            holdActionPlayed = false;

            if (weaponPlayer == null)
            {
                Debug.LogWarning($"[AC6Player] {(leftHand ? "Left" : "Right")} weapon has no animation player.", mech);
                return;
            }

            var actionId = FirstAvailableAction(
                weaponPlayer,
                leftHand
                    ? new[] { "a000_040010", "a000_800210", "a000_800090", "a000_800000" }
                    : new[] { "a000_800210", "a000_800090", "a000_800000", "a000_040010" });
            if (!string.IsNullOrEmpty(actionId))
            {
                weaponPlayer.PlayAction(actionId);
            }

            var anchor = FindMuzzle(weaponPlayer.transform);
            SpawnWeaponBurst(anchor, leftHand ? new Color(0.2f, 0.75f, 1f) : new Color(1f, 0.45f, 0.08f));
            if (actionId == null || actionId.IndexOf("040010", StringComparison.OrdinalIgnoreCase) < 0)
            {
                SpawnProjectile(anchor, leftHand);
            }

            Debug.Log($"[AC6Player] {(leftHand ? "Left" : "Right")} weapon fired action {actionId}.", mech);
        }

        private void UpdateLocomotionAnimation()
        {
            if (bodyActionPlayer == null || Time.time < locomotionLockUntil)
            {
                return;
            }

            var thrusting = moveInput.sqrMagnitude > 0.001f || ascendInput;
            var desired = assaultBoostActive || thrusting ? ForwardBoostActionId : IdleActionId;
            PlayLocomotion(desired, false);
        }

        private void PlayLocomotion(string actionId, bool restart)
        {
            if (bodyActionPlayer == null || string.IsNullOrWhiteSpace(actionId))
            {
                return;
            }

            if (!restart && string.Equals(requestedLocomotionAction, actionId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (bodyActionPlayer.PlayFullBody(actionId))
            {
                requestedLocomotionAction = actionId;
            }
        }

        private void UpdateBoosterEffects()
        {
            var active = assaultBoostActive || quickBoostRequested || moveInput.sqrMagnitude > 0.001f || ascendInput;
            var intensity = assaultBoostActive ? 2.2f : Time.time < locomotionLockUntil ? 1.8f : 0.8f;
            var equipmentAction = active
                ? intensity > 1f ? "a000_000002" : "a000_000001"
                : "a000_000003";
            if (!string.Equals(activeBoosterAction, equipmentAction, StringComparison.OrdinalIgnoreCase))
            {
                for (var i = 0; i < boosterPlayers.Count; i++)
                {
                    if (boosterPlayers[i].HasAction(equipmentAction))
                    {
                        boosterPlayers[i].PlayAction(equipmentAction);
                    }
                }
                activeBoosterAction = equipmentAction;
            }

            for (var i = 0; i < boosterExhausts.Count; i++)
            {
                var particles = boosterExhausts[i];
                var emission = particles.emission;
                emission.rateOverTime = 70f * intensity;
                var main = particles.main;
                main.startSpeed = 10f * intensity;
                if (active && !particles.isPlaying)
                {
                    particles.Play();
                }
                else if (!active && particles.isPlaying)
                {
                    particles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }
            }
        }

        private void BuildBoosterExhausts()
        {
            boosterExhausts.Clear();
            for (var i = 0; i < boosterPlayers.Count; i++)
            {
                var anchors = boosterPlayers[i].GetComponentsInChildren<Transform>(true)
                    .Where(candidate => candidate.name.EndsWith("_dmy_31", StringComparison.OrdinalIgnoreCase));
                foreach (var anchor in anchors)
                {
                    var root = new GameObject("Player_Booster_Exhaust");
                    root.transform.SetParent(anchor, false);
                    root.transform.localPosition = Vector3.zero;
                    root.transform.localRotation = Quaternion.identity;
                    var particles = root.AddComponent<ParticleSystem>();
                    particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    var main = particles.main;
                    main.duration = 1f;
                    main.loop = true;
                    main.playOnAwake = false;
                    main.startLifetime = 0.22f;
                    main.startSpeed = 10f;
                    main.startSize = 0.24f;
                    main.startColor = new Color(0.22f, 0.62f, 1f, 0.95f);
                    main.simulationSpace = ParticleSystemSimulationSpace.World;
                    var emission = particles.emission;
                    emission.rateOverTime = 70f;
                    var shape = particles.shape;
                    shape.shapeType = ParticleSystemShapeType.Cone;
                    shape.angle = 6f;
                    shape.radius = 0.05f;
                    root.GetComponent<ParticleSystemRenderer>().sharedMaterial =
                        CreateRuntimeMaterial("Player Booster Exhaust", new Color(0.18f, 0.58f, 1f), true);
                    boosterExhausts.Add(particles);
                }
            }
        }

        private void SpawnBurstAtBoosters(float size)
        {
            for (var i = 0; i < boosterExhausts.Count; i++)
            {
                SpawnWeaponBurst(boosterExhausts[i].transform.parent, new Color(0.25f, 0.7f, 1f), size);
            }
        }

        private static Transform FindMuzzle(Transform root)
        {
            return root.GetComponentsInChildren<Transform>(true).FirstOrDefault(candidate =>
                       candidate.name.EndsWith("_dmy_300", StringComparison.OrdinalIgnoreCase) ||
                       candidate.name.EndsWith("_dmy_301", StringComparison.OrdinalIgnoreCase) ||
                       candidate.name.EndsWith("_dmy_100", StringComparison.OrdinalIgnoreCase))
                   ?? root;
        }

        private static void SpawnWeaponBurst(Transform anchor, Color color, float size = 0.65f)
        {
            if (anchor == null)
            {
                return;
            }

            var root = new GameObject("Player_Weapon_Burst");
            root.transform.SetParent(anchor, false);
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            var particles = root.AddComponent<ParticleSystem>();
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = particles.main;
            main.duration = 0.25f;
            main.loop = false;
            main.startLifetime = 0.22f;
            main.startSpeed = 7f;
            main.startSize = size;
            main.startColor = color;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var emission = particles.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 18) });
            var shape = particles.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 12f;
            shape.radius = 0.03f;
            root.GetComponent<ParticleSystemRenderer>().sharedMaterial =
                CreateRuntimeMaterial("Player Weapon Burst", color, true);
            particles.Play();
            Destroy(root, 1f);
        }

        private void SpawnProjectile(Transform anchor, bool leftHand)
        {
            if (anchor == null)
            {
                return;
            }

            var projectile = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            projectile.name = leftHand ? "Player_Left_Projectile" : "Player_Right_Projectile";
            projectile.transform.position = anchor.position;
            projectile.transform.localScale = Vector3.one * 0.18f;
            var collider = projectile.GetComponent<Collider>();
            if (collider != null)
            {
                collider.isTrigger = true;
            }
            var color = leftHand ? new Color(0.15f, 0.65f, 1f) : new Color(1f, 0.34f, 0.06f);
            projectile.GetComponent<Renderer>().sharedMaterial =
                CreateRuntimeMaterial("Player Projectile", color, true);
            var projectileBody = projectile.AddComponent<Rigidbody>();
            projectileBody.useGravity = false;
            projectileBody.linearDamping = 0f;
            projectileBody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            projectileBody.linearVelocity = (playerCamera != null ? playerCamera.transform.forward : anchor.forward) * 180f;
            Destroy(projectile, 3f);
        }

        private static string FirstAvailableAction(
            MechEquipmentAnimationPlayer player,
            IEnumerable<string> candidates)
        {
            return candidates.FirstOrDefault(player.HasAction);
        }

        private static Material CreateRuntimeMaterial(string materialName, Color color, bool emissive)
        {
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit") ??
                         Shader.Find("Universal Render Pipeline/Unlit") ??
                         Shader.Find("Unlit/Color");
            var material = new Material(shader) { name = materialName };
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
            }
            else if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", color);
            }
            if (emissive && material.HasProperty("_EmissionColor"))
            {
                material.SetColor("_EmissionColor", color * 2f);
                material.EnableKeyword("_EMISSION");
            }
            return material;
        }

        private static float ReadAxis(KeyControl negative, KeyControl positive)
        {
            return (positive.isPressed ? 1f : 0f) - (negative.isPressed ? 1f : 0f);
        }

        private static float NormalizeAngle(float degrees)
        {
            return Mathf.Repeat(degrees + 180f, 360f) - 180f;
        }

        private static void LockCursor()
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }
}
