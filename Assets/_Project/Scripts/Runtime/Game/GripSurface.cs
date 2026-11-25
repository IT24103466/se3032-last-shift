using System;
using UnityEngine;
using Object = UnityEngine.Object;
namespace LastShift
{
    /// <summary>Local skin contact against weapon primitives; no unstable rigidbody fighting the camera.</summary>
    public sealed class GripSurface : IDisposable
    {
        readonly MeshFilter filter; readonly Mesh source, mesh; readonly Vector3[] rest, posed;
        readonly Collider[] contacts; readonly bool triggerFinger;
        readonly Matrix4x4[] previous;
        float lastPress = -1f;
        public int ContactCount { get; private set; }
        public GripSurface(MeshFilter filter, Collider[] contacts, bool triggerFinger)
        {
            this.filter = filter; this.contacts = contacts; this.triggerFinger = triggerFinger;
            source = filter.sharedMesh;
            if (source == null || !source.isReadable) return;
            mesh = Object.Instantiate(source); mesh.name = source.name + " contact pose"; mesh.MarkDynamic();
            rest = source.vertices; posed = new Vector3[rest.Length]; previous = new Matrix4x4[contacts.Length]; filter.sharedMesh = mesh;
        }
        public void Update(float press)
        {
            if (mesh == null || !filter.gameObject.activeInHierarchy) return;
            var hand = filter.transform; bool dirty = Mathf.Abs(press - lastPress) > 0.001f;
            for (int c = 0; c < contacts.Length; c++)
            {
                var relative = hand.worldToLocalMatrix * contacts[c].transform.localToWorldMatrix;
                if (relative != previous[c]) dirty = true;
                previous[c] = relative;
            }
            if (!dirty) return;
            lastPress = press; ContactCount = 0;
            for (int i = 0; i < rest.Length; i++)
            {
                Vector3 v = rest[i];
                // The upper/front finger follows the trigger by a few millimetres.
                if (triggerFinger)
                {
                    float weight = Mathf.SmoothStep(0, 1, (v.y + 0.005f) / 0.035f) * Mathf.SmoothStep(0, 1, (v.z + 0.025f) / 0.04f);
                    v.z -= 0.003f * press * weight;
                }
                Vector3 world = hand.TransformPoint(v);
                for (int iteration = 0; iteration < 2; iteration++)
                    foreach (var contact in contacts)
                    {
                        var t = contact.transform; Vector3 p = t.InverseTransformPoint(world), correction = Vector3.zero;
                        const float skin = 0.0012f;
                        // Close small gaps at the fingers, while leaving the palm/forearm free.
                        bool finger = v.z > -0.04f && Mathf.Abs(v.x) < 0.065f && Mathf.Abs(v.y) < 0.065f;
                        if (contact is BoxCollider box)
                        {
                            Vector3 d = p - box.center, half = box.size * 0.5f + Vector3.one * skin;
                            if (Mathf.Abs(d.x) < half.x && Mathf.Abs(d.y) < half.y && Mathf.Abs(d.z) < half.z)
                            {
                                int axis = 0; float depth = half.x - Mathf.Abs(d.x);
                                for (int a = 1; a < 3; a++) if (half[a] - Mathf.Abs(d[a]) < depth) { axis = a; depth = half[a] - Mathf.Abs(d[a]); }
                                correction[axis] = (d[axis] >= 0f ? 1f : -1f) * depth;
                            }
                            else if (finger)
                            {
                                Vector3 nearest = new Vector3(Mathf.Clamp(d.x,-half.x,half.x),Mathf.Clamp(d.y,-half.y,half.y),Mathf.Clamp(d.z,-half.z,half.z));
                                var gap = nearest-d;
                                if(gap.magnitude < 0.01f) correction = gap * 0.85f;
                            }
                        }
                        else if (contact is CapsuleCollider capsule)
                        {
                            Vector3 closest = capsule.center;
                            float half = Mathf.Max(0, capsule.height * 0.5f - capsule.radius);
                            closest[capsule.direction] += Mathf.Clamp(p[capsule.direction] - closest[capsule.direction], -half, half);
                            Vector3 away = p - closest; float distance = away.magnitude;
                            if (distance < capsule.radius + skin)
                                correction = (distance > 0.00001f ? away / distance : Vector3.up) * (capsule.radius + skin - distance);
                            else if(finger && distance < capsule.radius + skin + 0.01f)
                                correction = away.normalized * (capsule.radius + skin - distance) * 0.85f;
                        }
                        if (correction.sqrMagnitude > 0.000000001f) { world += t.TransformVector(correction); ContactCount++; }
                    }
                posed[i] = hand.InverseTransformPoint(world);
            }
            mesh.vertices = posed; mesh.RecalculateNormals(); mesh.RecalculateBounds();
        }
        public void Dispose()
        {
            if (filter != null) filter.sharedMesh = source;
            if (mesh != null) { if (Application.isPlaying) Object.Destroy(mesh); else Object.DestroyImmediate(mesh); }
        }
    }
}
