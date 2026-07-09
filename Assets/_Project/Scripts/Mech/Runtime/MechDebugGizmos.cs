using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace ACunicombat.Mechs
{
    [DisallowMultipleComponent]
    public sealed class MechDebugGizmos : MonoBehaviour
    {
        [SerializeField] private bool drawWhenNotSelected = true;
        [SerializeField] private bool drawAssembledParts = true;
        [SerializeField] private bool drawMountPoints = true;
        [SerializeField] private bool drawLabels = true;
        [SerializeField] private bool autoScaleGizmos = true;
        [SerializeField] private float mountSphereRadius = 0.35f;
        [SerializeField] private float mountForwardLineLength = 1.5f;
        [SerializeField] private Color partColor = new Color(0.1f, 0.7f, 1f, 0.7f);
        [SerializeField] private Color mountColor = new Color(1f, 0.8f, 0.1f, 0.85f);

        public void ResetToVisibleDefaults()
        {
            drawWhenNotSelected = true;
            drawAssembledParts = true;
            drawMountPoints = true;
            drawLabels = true;
            autoScaleGizmos = true;
            mountSphereRadius = 0.35f;
            mountForwardLineLength = 1.5f;
        }

        private void OnDrawGizmos()
        {
            if (drawWhenNotSelected)
            {
                Draw();
            }
        }

        private void OnDrawGizmosSelected()
        {
            Draw();
        }

        private void Draw()
        {
            var state = GetComponent<MechRuntimeState>();
            if (state == null)
            {
                return;
            }

            if (drawAssembledParts)
            {
                DrawParts(state);
            }

            if (drawMountPoints)
            {
                DrawMounts(state, CalculateGizmoScale(state));
            }
        }

        private void DrawParts(MechRuntimeState state)
        {
            Gizmos.color = partColor;
            var parts = state.AssembledParts;
            for (var i = 0; i < parts.Count; i++)
            {
                var record = parts[i];
                if (record == null || record.instance == null)
                {
                    continue;
                }

                var partTransform = record.instance.transform;
                if (TryGetWorldBounds(record.instance, out var bounds))
                {
                    Gizmos.DrawWireCube(bounds.center, bounds.size);
                }
                else
                {
                    Gizmos.DrawWireCube(partTransform.position, Vector3.one * 0.75f);
                }

#if UNITY_EDITOR
                if (drawLabels)
                {
                    Handles.color = partColor;
                    Handles.Label(partTransform.position + Vector3.up * 0.25f, record.slot.ToString());
                }
#endif
            }
        }

        private void DrawMounts(MechRuntimeState state, float gizmoScale)
        {
            Gizmos.color = mountColor;
            var mounts = state.MountPoints;
            for (var i = 0; i < mounts.Count; i++)
            {
                var mount = mounts[i];
                if (mount == null)
                {
                    continue;
                }

                var mountTransform = mount.transform;
                var radius = mountSphereRadius * gizmoScale;
                var lineLength = mountForwardLineLength * gizmoScale;
                Gizmos.DrawSphere(mountTransform.position, radius);
                Gizmos.DrawLine(mountTransform.position, mountTransform.position + mountTransform.forward * lineLength);

#if UNITY_EDITOR
                if (drawLabels)
                {
                    Handles.color = mountColor;
                    Handles.Label(mountTransform.position + Vector3.up * 0.12f, mount.TargetSlot.ToString());
                }
#endif
            }
        }

        private float CalculateGizmoScale(MechRuntimeState state)
        {
            if (!autoScaleGizmos)
            {
                return 1f;
            }

            if (!TryGetAssemblyBounds(state, out var bounds))
            {
                return 1f;
            }

            var largestAxis = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            return Mathf.Clamp(largestAxis * 0.015f, 1f, 5f);
        }

        private static bool TryGetAssemblyBounds(MechRuntimeState state, out Bounds bounds)
        {
            bounds = default;
            var hasBounds = false;
            var parts = state.AssembledParts;
            for (var i = 0; i < parts.Count; i++)
            {
                var record = parts[i];
                if (record == null || record.instance == null || !TryGetWorldBounds(record.instance, out var partBounds))
                {
                    continue;
                }

                if (hasBounds)
                {
                    bounds.Encapsulate(partBounds);
                }
                else
                {
                    bounds = partBounds;
                    hasBounds = true;
                }
            }

            return hasBounds;
        }

        private static bool TryGetWorldBounds(GameObject root, out Bounds bounds)
        {
            bounds = default;
            var hasBounds = false;
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            for (var i = 0; i < renderers.Length; i++)
            {
                var rendererBounds = renderers[i].bounds;
                if (rendererBounds.size == Vector3.zero)
                {
                    continue;
                }

                if (hasBounds)
                {
                    bounds.Encapsulate(rendererBounds);
                }
                else
                {
                    bounds = rendererBounds;
                    hasBounds = true;
                }
            }

            return hasBounds;
        }
    }
}
