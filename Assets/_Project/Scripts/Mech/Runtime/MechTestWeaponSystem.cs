using System;
using System.Collections.Generic;
using System.Linq;
using ACunicombat.Sensing.Contracts;
using UnityEngine;

namespace ACunicombat.Mechs
{
    [DefaultExecutionOrder(200)]
    [DisallowMultipleComponent]
    public sealed class MechTestWeaponSystem : MonoBehaviour
    {
        [SerializeField] private MechPlayerInputAdapter input;
        [SerializeField] private MechRuntimeState runtimeState;
        [SerializeField] private Camera aimingCamera;
        private MechThirdPersonCamera aimController;
        [SerializeField] private CombatTestTarget target;
        [SerializeField] private MechRadarController radarController;
        [SerializeField] private MechEffectPrefabLibrary nativeEffectLibrary;
        [Header("Aiming")]
        [SerializeField] private Vector2 aimViewportPoint = new Vector2(0.5f, 0.56f);
        [SerializeField] private float gunAutoAimDegrees = 4f;
        [SerializeField] private float missileLockDegrees = 24f;
        [Header("Test weapon envelopes")]
        [SerializeField, Min(1f)] private float missileMaximumFlightTimeSeconds = 300f;
        [SerializeField, Min(1000f)] private float missileMaximumRangeMeters = 50000f;

        private readonly Dictionary<MechPartSlot, Transform[]> muzzlesBySlot =
            new Dictionary<MechPartSlot, Transform[]>();
        private int leftHandMuzzleIndex;
        private int rightHandMuzzleIndex;
        private int leftBackMuzzleIndex;
        private int rightBackMuzzleIndex;
        private GUIStyle hudLabelStyle;

        private static Material rifleMaterial;
        private static Material cannonMaterial;
        private static Material missileMaterial;
        private static Material trailMaterial;

        public MechRadarController RadarController => radarController;

        public void Configure(
            MechPlayerInputAdapter newInput,
            MechRuntimeState newRuntimeState,
            Camera newAimingCamera,
            CombatTestTarget newTarget,
            MechEffectPrefabLibrary newNativeEffectLibrary = null)
        {
            input = newInput;
            runtimeState = newRuntimeState;
            aimingCamera = newAimingCamera;
            aimController = aimingCamera != null
                ? aimingCamera.GetComponent<MechThirdPersonCamera>()
                : null;
            target = newTarget;
            nativeEffectLibrary = newNativeEffectLibrary;
            radarController = GetComponent<MechRadarController>();
            if (radarController == null)
            {
                radarController = gameObject.AddComponent<MechRadarController>();
            }
            radarController.Configure(aimingCamera, target);
            ResolveMuzzles();
        }

        public void ConfigureAimViewport(Vector2 viewportPoint)
        {
            aimViewportPoint = new Vector2(
                Mathf.Clamp(viewportPoint.x, 0.05f, 0.95f),
                Mathf.Clamp(viewportPoint.y, 0.05f, 0.95f));
        }

        private void Awake()
        {
            if (input == null) input = GetComponent<MechPlayerInputAdapter>();
            if (runtimeState == null) runtimeState = GetComponent<MechRuntimeState>();
            if (aimingCamera == null) aimingCamera = Camera.main;
            if (aimController == null && aimingCamera != null)
            {
                aimController = aimingCamera.GetComponent<MechThirdPersonCamera>();
            }
            EnsureRadarController();
        }

        private void Start()
        {
            EnsureRadarController();
            ResolveMuzzles();
        }

        private void EnsureRadarController()
        {
            if (radarController == null)
            {
                radarController = GetComponent<MechRadarController>();
            }

            if (radarController == null && target != null)
            {
                radarController = gameObject.AddComponent<MechRadarController>();
            }

            if (radarController != null && target != null)
            {
                radarController.Configure(aimingCamera, target);
            }
        }

        private void Update()
        {
            if (input == null)
            {
                return;
            }

            var frame = input.Current;
            if (frame.PrimaryFireEdge)
            {
                FireHandWeapon(MechPartSlot.LeftHandWeapon, TestWeaponKind.Cannon, ref leftHandMuzzleIndex);
            }
            if (frame.SecondaryFireEdge)
            {
                FireHandWeapon(MechPartSlot.RightHandWeapon, TestWeaponKind.Rifle, ref rightHandMuzzleIndex);
            }
            if (frame.LeftShoulderFireEdge)
            {
                FireMissileSalvo(MechPartSlot.LeftBackWeapon, ref leftBackMuzzleIndex);
            }
            if (frame.RightShoulderFireEdge)
            {
                FireMissileSalvo(MechPartSlot.RightBackWeapon, ref rightBackMuzzleIndex);
            }
        }

        private void ResolveMuzzles()
        {
            muzzlesBySlot.Clear();
            if (runtimeState == null)
            {
                return;
            }

            for (var i = 0; i < runtimeState.AssembledParts.Count; i++)
            {
                var record = runtimeState.AssembledParts[i];
                if (record?.instance == null || !MechSlotRules.IsWeaponSlot(record.slot))
                {
                    continue;
                }

                var candidates = record.instance.GetComponentsInChildren<Transform>(true)
                    .Where(IsMuzzleTransform)
                    .OrderBy(candidate => candidate.name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                muzzlesBySlot[record.slot] = candidates.Length > 0
                    ? candidates
                    : new[] { record.instance.transform };
            }
        }

        private void FireHandWeapon(MechPartSlot slot, TestWeaponKind kind, ref int muzzleIndex)
        {
            var muzzle = GetNextMuzzle(slot, ref muzzleIndex);
            if (muzzle == null)
            {
                Debug.LogWarning($"[MechTestWeaponSystem] No muzzle for {slot}.", this);
                return;
            }

            var projectileSpeed = kind == TestWeaponKind.Rifle ? 520f : 175f;
            var locked = TryGetAimPoint(
                kind == TestWeaponKind.Rifle ? gunAutoAimDegrees : gunAutoAimDegrees + 2f,
                projectileSpeed,
                out var aimPoint,
                out _);
            var direction = (aimPoint - muzzle.position).normalized;
            if (!locked)
            {
                direction = GetAimRay().direction;
            }

            if (kind == TestWeaponKind.Rifle)
            {
                SpawnProjectile(muzzle.position, direction, kind, 520f, 110f, 0.08f, 3f,
                    launchFfxId: 6011100, projectileFfxId: 6013101, impactFfxId: 6011101,
                    launchAnchor: muzzle);
            }
            else
            {
                SpawnProjectile(muzzle.position, direction, kind, 175f, 420f, 0.2f, 6f,
                    launchFfxId: 6031100, projectileFfxId: 6031101,
                    launchAnchor: muzzle);
            }
        }

        private void FireMissileSalvo(MechPartSlot slot, ref int muzzleIndex)
        {
            if (!muzzlesBySlot.TryGetValue(slot, out var muzzles) || muzzles.Length == 0)
            {
                ResolveMuzzles();
            }
            if (!muzzlesBySlot.TryGetValue(slot, out muzzles) || muzzles.Length == 0)
            {
                Debug.LogWarning($"[MechTestWeaponSystem] No missile muzzle for {slot}.", this);
                return;
            }

            if (radarController == null ||
                !radarController.TryAuthorizeMissileLaunch(
                    MissileGuidanceMode.ActiveRadar,
                    missileLockDegrees,
                    230f,
                    out var track,
                    out _,
                    out var guidanceToken))
            {
                Debug.LogWarning(
                    $"[MechTestWeaponSystem] {slot} launch inhibited: no authorized radar track, " +
                    "weapon-envelope solution, seeker handoff, or free guidance channel.",
                    this);
                return;
            }

            var trackId = track.TrackId;
            Debug.Log(
                $"[MechTestWeaponSystem] Missile launch authorized: slot={slot}, track={trackId}, " +
                $"channel={guidanceToken}.",
                this);
            var salvoCount = Mathf.Min(4, muzzles.Length);
            for (var i = 0; i < salvoCount; i++)
            {
                if (i > 0 && !radarController.RetainGuidanceChannel(guidanceToken))
                {
                    Debug.LogWarning(
                        $"[MechTestWeaponSystem] {slot} salvo truncated: guidance channel was released.",
                        this);
                    break;
                }

                var muzzle = muzzles[(muzzleIndex + i) % muzzles.Length];
                var launchDirection = muzzle.forward;
                if (Vector3.Dot(launchDirection, GetAimRay().direction) < 0f)
                {
                    launchDirection = GetAimRay().direction;
                }

                SpawnProjectile(
                    muzzle.position,
                    launchDirection.normalized,
                    TestWeaponKind.Missile,
                    55f,
                    300f,
                    0.22f,
                    missileMaximumFlightTimeSeconds,
                    trackId,
                    i * 0.06f,
                    85f,
                    230f,
                    150f,
                    launchFfxId: slot == MechPartSlot.LeftBackWeapon ? 6082100 : 6083100,
                    projectileFfxId: slot == MechPartSlot.LeftBackWeapon ? 6082101 : 6083101,
                    launchAnchor: muzzle,
                    maximumTravelDistance: missileMaximumRangeMeters,
                    guidanceChannelToken: guidanceToken);
            }
            muzzleIndex = (muzzleIndex + salvoCount) % muzzles.Length;
        }

        private Transform GetNextMuzzle(MechPartSlot slot, ref int index)
        {
            if (!muzzlesBySlot.TryGetValue(slot, out var muzzles) || muzzles.Length == 0)
            {
                ResolveMuzzles();
            }
            if (!muzzlesBySlot.TryGetValue(slot, out muzzles) || muzzles.Length == 0)
            {
                return null;
            }

            var muzzle = muzzles[index % muzzles.Length];
            index = (index + 1) % muzzles.Length;
            return muzzle;
        }

        private void SpawnProjectile(
            Vector3 position,
            Vector3 direction,
            TestWeaponKind kind,
            float speed,
            float damage,
            float radius,
            float lifetime,
            TrackId homingTrack = default,
            float launchDelay = 0f,
            float acceleration = 0f,
            float maximumSpeed = 0f,
            float turnRateDegrees = 0f,
            int launchFfxId = 0,
            int projectileFfxId = 0,
            int impactFfxId = 0,
            Transform launchAnchor = null,
            float maximumTravelDistance = 0f,
            int guidanceChannelToken = 0)
        {
            EnsureMaterials();
            SpawnNativeOneShot(launchFfxId, launchAnchor, position, direction);
            var projectile = GameObject.CreatePrimitive(
                kind == TestWeaponKind.Missile ? PrimitiveType.Cube : PrimitiveType.Sphere);
            projectile.name = $"Test_{kind}_Projectile";
            projectile.transform.SetPositionAndRotation(position, Quaternion.LookRotation(direction, Vector3.up));
            projectile.transform.localScale = kind switch
            {
                TestWeaponKind.Rifle => Vector3.one * 0.12f,
                TestWeaponKind.Cannon => Vector3.one * 0.32f,
                _ => new Vector3(0.18f, 0.18f, 0.8f),
            };

            var collider = projectile.GetComponent<Collider>();
            if (collider != null) Destroy(collider);
            var placeholderRenderer = projectile.GetComponent<Renderer>();
            placeholderRenderer.sharedMaterial = GetProjectileMaterial(kind);

            var trail = projectile.AddComponent<TrailRenderer>();
            trail.time = kind == TestWeaponKind.Missile ? 0.8f : 0.16f;
            trail.startWidth = kind == TestWeaponKind.Missile ? 0.18f : 0.08f;
            trail.endWidth = 0f;
            trail.sharedMaterial = trailMaterial;
            trail.startColor = GetWeaponColor(kind);
            trail.endColor = new Color(GetWeaponColor(kind).r, GetWeaponColor(kind).g, GetWeaponColor(kind).b, 0f);

            if (TryInstantiateNativeEffect(projectileFfxId, projectile.transform, out var nativeProjectileEffect))
            {
                placeholderRenderer.enabled = false;
                trail.enabled = false;
                nativeProjectileEffect.name = $"Native_FFX_{projectileFfxId}";
            }

            projectile.AddComponent<MechTestProjectile>().Configure(
                transform,
                radarController,
                homingTrack,
                kind,
                direction * speed,
                damage,
                radius,
                lifetime,
                launchDelay,
                acceleration,
                maximumSpeed,
                turnRateDegrees,
                nativeEffectLibrary,
                impactFfxId,
                maximumTravelDistance,
                guidanceChannelToken);
        }

        private bool TryGetAimPoint(
            float coneDegrees,
            float projectileSpeed,
            out Vector3 aimPoint,
            out TrackId trackId)
        {
            var ray = GetAimRay();
            if (radarController != null &&
                radarController.TryGetFireSolution(coneDegrees, projectileSpeed, out var track, out var solution))
            {
                aimPoint = new Vector3(
                    (float)solution.InterceptPoint.x,
                    (float)solution.InterceptPoint.y,
                    (float)solution.InterceptPoint.z);
                trackId = track.TrackId;
                return true;
            }

            aimPoint = ray.origin + ray.direction * 100000f;
            trackId = default;
            return false;
        }

        private Ray GetAimRay()
        {
            if (aimingCamera == null) aimingCamera = Camera.main;
            var viewportPoint = GetAimViewportPoint();
            return aimingCamera != null
                ? aimingCamera.ViewportPointToRay(new Vector3(viewportPoint.x, viewportPoint.y, 0f))
                : new Ray(transform.position + Vector3.up * 2f, transform.forward);
        }

        private Vector2 GetAimViewportPoint()
        {
            if (aimController == null && aimingCamera != null)
            {
                aimController = aimingCamera.GetComponent<MechThirdPersonCamera>();
            }

            var viewportPoint = aimController != null
                ? aimController.AimViewportPoint
                : aimViewportPoint;
            return new Vector2(
                Mathf.Clamp(viewportPoint.x, 0.05f, 0.95f),
                Mathf.Clamp(viewportPoint.y, 0.05f, 0.95f));
        }

        private Vector2 GetReticleGuiPosition()
        {
            var viewportPoint = GetAimViewportPoint();
            if (aimingCamera == null)
            {
                return new Vector2(
                    Screen.width * viewportPoint.x,
                    Screen.height * (1f - viewportPoint.y));
            }

            var screenPoint = aimingCamera.ViewportToScreenPoint(
                new Vector3(viewportPoint.x, viewportPoint.y, 0f));
            return new Vector2(screenPoint.x, Screen.height - screenPoint.y);
        }

        private void OnGUI()
        {
            if (GetComponent<MechRadarHudPresenter>() != null)
            {
                return;
            }

            if (aimingCamera == null || radarController == null)
            {
                return;
            }

            hudLabelStyle ??= new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 13,
                fontStyle = FontStyle.Bold,
            };

            var center = GetReticleGuiPosition();
            DrawRect(new Rect(center.x - 11f, center.y - 1f, 22f, 2f), new Color(0.25f, 0.95f, 0.75f));
            DrawRect(new Rect(center.x - 1f, center.y - 11f, 2f, 22f), new Color(0.25f, 0.95f, 0.75f));

            if (!radarController.TryGetBestTrack(out var track))
            {
                return;
            }

            var trackedPosition = radarController.PredictTrackPosition(track);
            var screen = aimingCamera.WorldToScreenPoint(trackedPosition);
            if (screen.z <= 0f)
            {
                return;
            }

            var targetCenter = new Vector2(screen.x, Screen.height - screen.y);
            var color = TryGetAimPoint(missileLockDegrees, 230f, out _, out _)
                ? new Color(1f, 0.35f, 0.12f)
                : new Color(0.25f, 0.9f, 0.95f);
            const float bracket = 34f;
            DrawCornerBrackets(targetCenter, bracket, color);
            var range = Vector3.Distance(transform.position, trackedPosition);
            hudLabelStyle.normal.textColor = color;
            GUI.Label(new Rect(targetCenter.x - 90f, targetCenter.y - 65f, 180f, 22f),
                $"{track.TrackId}  {range:0} m  {track.Lifecycle}  {track.Confidence:P0}", hudLabelStyle);
        }

        public static void SpawnImpact(
            Vector3 position,
            Vector3 normal,
            TestWeaponKind kind,
            MechEffectPrefabLibrary effectLibrary = null,
            int impactFfxId = 0)
        {
            if (effectLibrary != null && impactFfxId > 0 &&
                effectLibrary.TryGet(impactFfxId, out var effectPrefab))
            {
                var effect = Instantiate(effectPrefab, position,
                    Quaternion.LookRotation(normal.sqrMagnitude > 0.001f ? normal : Vector3.up));
                PlayAllParticles(effect);
                Destroy(effect, 3f);
                return;
            }

            EnsureMaterials();
            var impact = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            impact.name = $"Test_{kind}_Impact";
            impact.transform.position = position + normal * 0.05f;
            impact.transform.localScale = Vector3.one * (kind == TestWeaponKind.Missile ? 1.3f : 0.65f);
            var collider = impact.GetComponent<Collider>();
            if (collider != null) Destroy(collider);
            impact.GetComponent<Renderer>().sharedMaterial = GetProjectileMaterial(kind);
            Destroy(impact, 0.12f);
        }

        private void SpawnNativeOneShot(
            int ffxId,
            Transform anchor,
            Vector3 position,
            Vector3 direction)
        {
            if (nativeEffectLibrary == null || ffxId <= 0 ||
                !nativeEffectLibrary.TryGet(ffxId, out var prefab))
            {
                return;
            }

            var rotation = Quaternion.LookRotation(direction, Vector3.up);
            var effect = anchor != null
                ? Instantiate(prefab, position, rotation, anchor)
                : Instantiate(prefab, position, rotation);
            effect.name = $"Native_FFX_{ffxId}_OneShot";
            PlayAllParticles(effect);
            Destroy(effect, GetOneShotLifetime(effect));
        }

        private static float GetOneShotLifetime(GameObject effect)
        {
            var lifetime = 0.1f;
            foreach (var particles in effect.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = particles.main;
                // These imported one-shots emit their only burst at t=0. The final
                // visible particle therefore ends after delay + particle lifetime;
                // adding the system duration again kept an already-finished root
                // alive longer than the source FXR specifies.
                lifetime = Mathf.Max(
                    lifetime,
                    main.startDelay.constantMax + main.startLifetime.constantMax);
            }

            foreach (var timedElement in effect.GetComponentsInChildren<MechTimedEffectElement>(true))
            {
                lifetime = Mathf.Max(lifetime, timedElement.LifetimeSeconds);
            }

            return lifetime + 0.1f;
        }

        private bool TryInstantiateNativeEffect(int ffxId, Transform parent, out GameObject effect)
        {
            if (nativeEffectLibrary != null && ffxId > 0 &&
                nativeEffectLibrary.TryGet(ffxId, out var prefab))
            {
                effect = Instantiate(prefab, parent, false);
                effect.transform.localPosition = Vector3.zero;
                effect.transform.localRotation = Quaternion.identity;
                PlayAllParticles(effect);
                return true;
            }

            effect = null;
            return false;
        }

        private static void PlayAllParticles(GameObject root)
        {
            var systems = root.GetComponentsInChildren<ParticleSystem>(true);
            for (var i = 0; i < systems.Length; i++) systems[i].Play(true);
        }

        private static bool IsMuzzleTransform(Transform candidate)
        {
            return candidate.name.EndsWith("_dmy_300", StringComparison.OrdinalIgnoreCase) ||
                   candidate.name.EndsWith("_dmy_301", StringComparison.OrdinalIgnoreCase) ||
                   candidate.name.EndsWith("_dmy_100", StringComparison.OrdinalIgnoreCase);
        }

        private static Color GetWeaponColor(TestWeaponKind kind)
        {
            return kind switch
            {
                TestWeaponKind.Rifle => new Color(0.25f, 0.85f, 1f, 1f),
                TestWeaponKind.Cannon => new Color(1f, 0.52f, 0.08f, 1f),
                _ => new Color(1f, 0.18f, 0.06f, 1f),
            };
        }

        private static Material GetProjectileMaterial(TestWeaponKind kind)
        {
            return kind switch
            {
                TestWeaponKind.Rifle => rifleMaterial,
                TestWeaponKind.Cannon => cannonMaterial,
                _ => missileMaterial,
            };
        }

        private static void EnsureMaterials()
        {
            rifleMaterial ??= CreateRuntimeMaterial("Test Rifle", GetWeaponColor(TestWeaponKind.Rifle));
            cannonMaterial ??= CreateRuntimeMaterial("Test Cannon", GetWeaponColor(TestWeaponKind.Cannon));
            missileMaterial ??= CreateRuntimeMaterial("Test Missile", GetWeaponColor(TestWeaponKind.Missile));
            trailMaterial ??= CreateRuntimeMaterial("Test Projectile Trail", Color.white);
        }

        private static Material CreateRuntimeMaterial(string materialName, Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            var material = new Material(shader) { name = materialName };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            return material;
        }

        private static void DrawCornerBrackets(Vector2 center, float extent, Color color)
        {
            const float length = 12f;
            const float width = 2f;
            DrawRect(new Rect(center.x - extent, center.y - extent, length, width), color);
            DrawRect(new Rect(center.x - extent, center.y - extent, width, length), color);
            DrawRect(new Rect(center.x + extent - length, center.y - extent, length, width), color);
            DrawRect(new Rect(center.x + extent - width, center.y - extent, width, length), color);
            DrawRect(new Rect(center.x - extent, center.y + extent - width, length, width), color);
            DrawRect(new Rect(center.x - extent, center.y + extent - length, width, length), color);
            DrawRect(new Rect(center.x + extent - length, center.y + extent - width, length, width), color);
            DrawRect(new Rect(center.x + extent - width, center.y + extent - length, width, length), color);
        }

        private static void DrawRect(Rect rect, Color color)
        {
            var previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }
    }

}
