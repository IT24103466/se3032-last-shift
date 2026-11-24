using UnityEngine;
namespace LastShift
{
    public static class EnemyAwareness
    {
        public static Vector3 ClosestPoint(Vector3 from, Vector3 to, Vector3 point)
        {
            var d = to - from;
            return from + d * Mathf.Clamp01(Vector3.Dot(point - from, d) / Mathf.Max(0.0001f, d.sqrMagnitude));
        }
        public static bool Clear(Vector3 from, Vector3 to, Transform observer, Transform source)
        {
            var delta = to - from;
            foreach (var hit in Physics.RaycastAll(from, delta.normalized, delta.magnitude, ~(1 << 2), QueryTriggerInteraction.Ignore))
                if (!hit.transform.IsChildOf(observer) && !hit.transform.IsChildOf(source) && hit.distance < delta.magnitude - 0.08f) return false;
            return true;
        }
        public static bool TorchClue(Light torch, Transform source, Transform observer, Vector3 eye, Vector3 facing, out Vector3 clue)
        {
            clue = Vector3.zero;
            if (torch == null || !torch.enabled || !torch.gameObject.activeInHierarchy) return false;
            Vector3 origin = torch.transform.position, forward = torch.transform.forward;
            Vector3 toEye = eye - origin;
            // Seeing the beam on oneself is a warning even before identifying its owner.
            if (toEye.magnitude <= torch.range && Vector3.Angle(forward, toEye) <= torch.spotAngle * 0.5f && Clear(origin, eye, observer, source))
            { clue = eye; return true; }
            var hits = Physics.RaycastAll(origin, forward, torch.range, ~(1 << 2), QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in hits)
            {
                if (hit.transform.IsChildOf(source)) continue;
                Vector3 toSpot = hit.point - eye;
                if (toSpot.magnitude > 22f || Vector3.Angle(facing, toSpot) > 75f) return false;
                clue = hit.point + hit.normal * 0.12f;
                return Clear(eye, clue, observer, source);
            }
            return false;
        }
    }
}
