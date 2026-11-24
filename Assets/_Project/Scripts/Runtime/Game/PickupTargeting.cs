using System;
using UnityEngine;
using WorldBuilder.Testing;

namespace LastShift
{
    /// <summary>Whole-object pickup volumes and camera queries independent of trigger enter/stay events.</summary>
    public static class PickupTargeting
    {
        public static void Fit(Component pickup)
        {
            Transform root = pickup.transform;
            var existing = root.Find("InteractionVolume");
            var volume = existing != null ? existing.gameObject : new GameObject("InteractionVolume");
            volume.transform.SetParent(root, false); volume.layer = 0;
            var box = volume.GetComponent<BoxCollider>();
            if (box == null) box = volume.AddComponent<BoxCollider>();
            bool found = false; Bounds bounds = new Bounds();
            foreach (var r in root.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy || (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer))) continue;
                var b = r.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 p = root.InverseTransformPoint(b.center + Vector3.Scale(b.extents,
                        new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                    if (!found) { bounds = new Bounds(p, Vector3.zero); found = true; } else bounds.Encapsulate(p);
                }
            }
            box.center = found ? bounds.center : Vector3.zero;
            Vector3 s = root.lossyScale;
            Vector3 padding = new Vector3(0.06f / Mathf.Max(0.001f, Mathf.Abs(s.x)), 0.06f / Mathf.Max(0.001f, Mathf.Abs(s.y)), 0.06f / Mathf.Max(0.001f, Mathf.Abs(s.z)));
            box.size = found ? bounds.size + padding : padding * 3f;
            box.isTrigger = true;
            Physics.SyncTransforms();
        }

        static Component Resolve(Collider collider)
        {
            Component target = collider.GetComponentInParent<TeaBag>();
            if (target == null) target = collider.GetComponentInParent<AmmoBox>();
            if (target == null) target = collider.GetComponentInParent<PebblePile>();
            if (target == null) target = collider.GetComponentInParent<GreyboxInteractable>();
            return target;
        }

        public static Component Find(Transform eyes, Transform player, float range = 2.6f)
        {
            var ray = new Ray(eyes.position, eyes.forward);
            var hits = Physics.RaycastAll(ray, range, ~(1 << 2), QueryTriggerInteraction.Collide);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in hits)
            {
                if (hit.transform.IsChildOf(player)) continue;
                var target = Resolve(hit.collider);
                if (target != null) return target;
                if (!hit.collider.isTrigger) break;
            }
            // Forgiving near misses, while preserving line of sight through the world.
            hits = Physics.SphereCastAll(ray, 0.16f, range, ~(1 << 2), QueryTriggerInteraction.Collide);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in hits)
            {
                if (hit.transform.IsChildOf(player)) continue;
                var target = Resolve(hit.collider);
                Vector3 point = hit.distance > 0f ? hit.point : hit.collider.bounds.center;
                if (target != null && Visible(eyes.position, point, target.transform, player)) return target;
            }
            // Spherecasts do not report shapes already overlapping the cast origin.
            foreach (var c in Physics.OverlapSphere(eyes.position, 0.16f, ~(1 << 2), QueryTriggerInteraction.Collide))
            {
                if (c.transform.IsChildOf(player)) continue;
                var target = Resolve(c);
                if (target != null && Visible(eyes.position, c.bounds.center, target.transform, player)) return target;
            }
            return null;
        }

        static bool Visible(Vector3 origin, Vector3 point, Transform target, Transform player)
        {
            var delta = point - origin;
            foreach (var hit in Physics.RaycastAll(origin, delta.normalized, delta.magnitude, ~(1 << 2), QueryTriggerInteraction.Ignore))
                if (!hit.transform.IsChildOf(player) && !hit.transform.IsChildOf(target)) return false;
            return true;
        }
    }
}
