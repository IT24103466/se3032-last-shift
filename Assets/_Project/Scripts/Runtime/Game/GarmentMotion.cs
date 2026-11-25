using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LastShift
{
    /// <summary>Small spring cloth: welded vertices, pinned waistband, edge constraints and leg/ground contact.</summary>
    public sealed class GarmentMotion : IDisposable
    {
        struct Edge { public int a, b; public float length; }
        sealed class LegContact
        {
            public Transform t; public Bounds bounds; public Matrix4x4 previous, inverse, world;
            public Vector3[] barrierNormal, barrierPoint;
        }
        readonly Transform garment, owner;
        readonly MeshFilter filter;
        readonly Mesh source, mesh;
        readonly Vector3[] rest, positions, previous, output;
        readonly int[] nodeForVertex;
        readonly float[] free;
        readonly List<Edge> edges = new List<Edge>();
        readonly Transform[] legs;
        readonly List<LegContact> contacts = new List<LegContact>();
        readonly int[] clothTriangles;
        readonly float height;
        readonly AudioSource sound;
        readonly AudioClip rustle;
        Vector3 lastOrigin;
        float accumulator, nextRustle;
        public bool Simulating => mesh != null;
        public float MaximumOffset { get; private set; }

        public GarmentMotion(Transform garment, Transform owner, Transform[] legs)
        {
            this.garment = garment; this.owner = owner; this.legs = legs;
            filter = garment.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null || !filter.sharedMesh.isReadable) return;
            source = filter.sharedMesh;
            mesh = Object.Instantiate(source); mesh.name = source.name + " cloth"; mesh.MarkDynamic(); filter.sharedMesh = mesh;
            Refine(mesh);
            output = mesh.vertices; nodeForVertex = new int[output.Length];
            var welded = new Dictionary<Vector3Int, int>(); var nodes = new List<Vector3>();
            for (int i = 0; i < output.Length; i++)
            {
                var key = Vector3Int.RoundToInt(output[i] * 10000f);
                if (!welded.TryGetValue(key, out int node)) { node = nodes.Count; nodes.Add(output[i]); welded.Add(key, node); }
                nodeForVertex[i] = node;
            }
            rest = nodes.ToArray(); positions = new Vector3[rest.Length]; previous = new Vector3[rest.Length]; free = new float[rest.Length];
            var bounds = mesh.bounds;
            height = Mathf.Max(0.05f, garment.TransformVector(Vector3.up * bounds.size.y).magnitude);
            for (int i = 0; i < rest.Length; i++)
            {
                positions[i] = previous[i] = garment.TransformPoint(rest[i]);
                free[i] = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((bounds.max.y - rest[i].y) / Mathf.Max(0.01f, bounds.size.y) - 0.18f) / 0.82f);
            }
            int[] triangles = mesh.triangles; var uniqueEdges = new HashSet<long>();
            clothTriangles = new int[triangles.Length];
            for (int i = 0; i < triangles.Length; i++) clothTriangles[i] = nodeForVertex[triangles[i]];
            foreach (var leg in legs)
            {
                Bounds fitted;
                var joints = owner.GetComponent<CreatureAnimator>()?.Articulation;
                if (joints != null && joints.TryBounds(leg, out var segmentBounds)) fitted = segmentBounds;
                else
                {
                    var mf = leg.GetComponentInChildren<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    Bounds local = mf.sharedMesh.bounds; fitted = new Bounds();
                    for (int i = 0; i < 8; i++)
                    {
                        var p = local.center + Vector3.Scale(local.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                        p = leg.InverseTransformPoint(mf.transform.TransformPoint(p));
                        if (i == 0) fitted = new Bounds(p, Vector3.zero); else fitted.Encapsulate(p);
                    }
                }
                fitted.Expand(0.025f);
                contacts.Add(new LegContact { t = leg, bounds = fitted, previous = leg.worldToLocalMatrix,
                    barrierNormal = new Vector3[rest.Length], barrierPoint = new Vector3[rest.Length] });
            }
            void AddEdge(int a, int b)
            {
                a = nodeForVertex[a]; b = nodeForVertex[b]; if (a == b) return;
                int low = Mathf.Min(a, b), high = Mathf.Max(a, b); long key = ((long)low << 32) | (uint)high;
                if (uniqueEdges.Add(key)) edges.Add(new Edge { a = a, b = b, length = Vector3.Distance(positions[a], positions[b]) });
            }
            for (int i = 0; i < triangles.Length; i += 3) { AddEdge(triangles[i], triangles[i + 1]); AddEdge(triangles[i + 1], triangles[i + 2]); AddEdge(triangles[i + 2], triangles[i]); }
            lastOrigin = garment.position;
            sound = garment.gameObject.AddComponent<AudioSource>();
            sound.spatialBlend = 1f; sound.volume = 0.055f; sound.minDistance = 0.5f; sound.maxDistance = 7f; sound.playOnAwake = false;
            var random = new System.Random(41068); var samples = new float[2400]; float smooth = 0f;
            for (int i = 0; i < samples.Length; i++) { smooth = Mathf.Lerp(smooth, (float)random.NextDouble() * 2f - 1f, 0.18f); samples[i] = smooth * Mathf.Sin(i / (float)samples.Length * Mathf.PI); }
            rustle = AudioClip.Create("Sarong rustle", samples.Length, 1, 22050, false); rustle.SetData(samples, 0);
        }

        public void Tick(float dt, bool dead)
        {
            if (!Simulating || garment == null || dt <= 0f) return;
            accumulator += Mathf.Min(dt, 0.1f);
            if (accumulator < 1f / 30f) return;
            float step = Mathf.Min(accumulator, 1f / 20f); accumulator = 0f;
            var origin = garment.position;
            float movement = Vector3.Distance(origin, lastOrigin) / step;
            bool teleport = Vector3.Distance(origin, lastOrigin) > height * 3f;
            lastOrigin = origin;
            for (int i = 0; i < rest.Length; i++)
            {
                Vector3 target = garment.TransformPoint(rest[i]);
                if (free[i] <= 0.001f || teleport) { positions[i] = previous[i] = target; continue; }
                Vector3 inertia = (positions[i] - previous[i]) * 0.88f;
                previous[i] = positions[i];
                Vector3 breeze = garment.right * (Mathf.Sin(Time.time * 2.5f + rest[i].y * 9f) * 0.35f);
                positions[i] += inertia + ((target - positions[i]) * 65f + Physics.gravity * 0.18f + breeze) * (step * step);
            }
            PrepareSweeps(teleport);
            for (int iteration = 0; iteration < 6; iteration++)
            {
                foreach (var edge in edges)
                {
                    Vector3 d = positions[edge.b] - positions[edge.a]; float length = d.magnitude;
                    float weight = free[edge.a] + free[edge.b];
                    if (length < 0.00001f || weight <= 0f) continue;
                    Vector3 fix = d * ((length - edge.length) / length * 0.65f);
                    positions[edge.a] += fix * (free[edge.a] / weight); positions[edge.b] -= fix * (free[edge.b] / weight);
                }
                for (int i = 0; i < rest.Length; i++)
                {
                    Vector3 target = garment.TransformPoint(rest[i]);
                    if (free[i] <= 0.001f) { positions[i] = target; continue; }
                    // Contact takes priority over this soft tether; otherwise a bent leg
                    // is forced back through the cloth by the following solver iteration.
                    positions[i] = target + Vector3.ClampMagnitude(positions[i] - target, height * (dead ? 1.1f : 0.7f) * Mathf.Sqrt(free[i]));
                }
                SolveContacts();
            }
            MaximumOffset = 0f;
            for (int i = 0; i < rest.Length; i++)
            {
                if (free[i] > 0f)
                {
                    var ray = new Ray(positions[i] + Vector3.up * height * 0.25f, Vector3.down);
                    if (Physics.Raycast(ray, out var hit, height * 0.35f, ~(1 << 2), QueryTriggerInteraction.Ignore) && !hit.transform.IsChildOf(owner))
                        positions[i].y = Mathf.Max(positions[i].y, hit.point.y + 0.008f);
                }
                MaximumOffset = Mathf.Max(MaximumOffset, Vector3.Distance(positions[i], garment.TransformPoint(rest[i])));
            }
            // Ground contact and spring tethers must not undo leg separation.
            SolveContacts();
            for (int i = 0; i < rest.Length; i++) previous[i] = Vector3.Lerp(previous[i], positions[i], 0.35f);
            for (int i = 0; i < output.Length; i++) output[i] = garment.InverseTransformPoint(positions[nodeForVertex[i]]);
            mesh.vertices = output; mesh.RecalculateNormals(); mesh.RecalculateBounds();
            if (!dead && movement > 0.6f && Time.time >= nextRustle && sound != null)
            { nextRustle = Time.time + 0.55f; sound.PlayOneShot(rustle); }
        }

        void PrepareSweeps(bool teleport)
        {
            foreach (var c in contacts)
            {
                c.inverse = c.t.worldToLocalMatrix; c.world = c.t.localToWorldMatrix;
                for (int i = 0; i < positions.Length; i++)
                {
                    c.barrierNormal[i] = Vector3.zero;
                    if (teleport || free[i] <= 0.001f) continue;
                    Vector3 from = c.previous.MultiplyPoint3x4(previous[i]), to = c.inverse.MultiplyPoint3x4(positions[i]);
                    Vector3 direction = to - from, normal = Vector3.zero;
                    float enter = 0f, exit = 1f;
                    for (int axis = 0; axis < 3; axis++)
                    {
                        if (Mathf.Abs(direction[axis]) < 0.000001f)
                        { if (from[axis] < c.bounds.min[axis] || from[axis] > c.bounds.max[axis]) { exit = -1f; break; } continue; }
                        float a = (c.bounds.min[axis] - from[axis]) / direction[axis];
                        float b = (c.bounds.max[axis] - from[axis]) / direction[axis];
                        float near = Mathf.Min(a, b), far = Mathf.Max(a, b);
                        if (near > enter) { enter = near; normal = Vector3.zero; normal[axis] = direction[axis] > 0f ? -1f : 1f; }
                        exit = Mathf.Min(exit, far);
                    }
                    if (enter <= exit && enter > 0f && enter < 1f)
                    { c.barrierNormal[i] = normal; c.barrierPoint[i] = from + direction * enter; }
                }
                c.previous = c.inverse;
            }
        }

        void SolveContacts()
        {
            foreach (var contact in contacts)
            {
                if (contact.t == null) continue;
                Vector3 Correction(Vector3 point)
                {
                    Vector3 p = contact.inverse.MultiplyPoint3x4(point);
                    if (!contact.bounds.Contains(p)) return Vector3.zero;
                    float nearest = float.PositiveInfinity; Vector3 fix = Vector3.zero;
                    for (int axis = 0; axis < 3; axis++)
                    {
                        float low = p[axis] - contact.bounds.min[axis], high = contact.bounds.max[axis] - p[axis];
                        if (low < nearest) { nearest = low; fix = Vector3.zero; fix[axis] = -low - 0.001f; }
                        if (high < nearest) { nearest = high; fix = Vector3.zero; fix[axis] = high + 0.001f; }
                    }
                    return contact.world.MultiplyVector(fix);
                }
                for (int i = 0; i < positions.Length; i++)
                    if (free[i] > 0.001f)
                    {
                        var normal = contact.barrierNormal[i];
                        if (normal != Vector3.zero)
                        {
                            var local = contact.inverse.MultiplyPoint3x4(positions[i]);
                            float depth = Vector3.Dot(local - contact.barrierPoint[i], normal);
                            if (depth < 0.001f) positions[i] += contact.world.MultiplyVector(normal * (0.001f - depth));
                        }
                        positions[i] += Correction(positions[i]);
                    }
                // A long triangle can straddle a leg even when all its corners are outside.
                // Sample its centre and edges and distribute the contact to movable corners.
                for (int i = 0; i < clothTriangles.Length; i += 3)
                {
                    int x = clothTriangles[i], y = clothTriangles[i + 1], z = clothTriangles[i + 2];
                    void Sample(float wx, float wy, float wz)
                    {
                        float denom = wx * wx * free[x] + wy * wy * free[y] + wz * wz * free[z];
                        if (denom < 0.00001f) return;
                        var fix = Correction(positions[x] * wx + positions[y] * wy + positions[z] * wz);
                        positions[x] += fix * (wx * free[x] / denom);
                        positions[y] += fix * (wy * free[y] / denom);
                        positions[z] += fix * (wz * free[z] / denom);
                    }
                    Sample(1f / 3f, 1f / 3f, 1f / 3f); Sample(0.5f, 0.5f, 0); Sample(0, 0.5f, 0.5f); Sample(0.5f, 0, 0.5f);
                }
            }
        }

        static void Refine(Mesh mesh)
        {
            // Refine only coarse meshes, with a small budget for multiple live NPCs.
            for (int pass = 0; pass < 2 && mesh.vertexCount < 512; pass++)
            {
                var vertices = new List<Vector3>(mesh.vertices); var uv = new List<Vector2>(mesh.uv);
                if (uv.Count != vertices.Count) { uv.Clear(); for (int i = 0; i < vertices.Count; i++) uv.Add(Vector2.zero); }
                var triangles = mesh.triangles; var refined = new List<int>(); var midpoints = new Dictionary<long, int>();
                int Mid(int a, int b)
                {
                    long key = ((long)Mathf.Min(a, b) << 32) | (uint)Mathf.Max(a, b);
                    if (!midpoints.TryGetValue(key, out int index))
                    { index = vertices.Count; vertices.Add((vertices[a] + vertices[b]) * 0.5f); uv.Add((uv[a] + uv[b]) * 0.5f); midpoints.Add(key, index); }
                    return index;
                }
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2], ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                    refined.AddRange(new[] { a, ab, ca, ab, b, bc, ca, bc, c, ab, bc, ca });
                }
                mesh.SetVertices(vertices); mesh.SetUVs(0, uv); mesh.SetTriangles(refined, 0);
            }
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
        }

        public void Reset()
        {
            if (!Simulating) return;
            for (int i = 0; i < rest.Length; i++) positions[i] = previous[i] = garment.TransformPoint(rest[i]);
            accumulator = 0f; lastOrigin = garment.position;
            foreach (var c in contacts) c.previous = c.t.worldToLocalMatrix;
            for (int i = 0; i < output.Length; i++) output[i] = rest[nodeForVertex[i]];
            mesh.vertices = output; mesh.RecalculateNormals(); mesh.RecalculateBounds();
        }

        public void Dispose()
        {
            if (filter != null && source != null) filter.sharedMesh = source;
            Destroy(mesh); Destroy(sound); Destroy(rustle);
        }
        static void Destroy(Object value) { if (value == null) return; if (Application.isPlaying) Object.Destroy(value); else Object.DestroyImmediate(value); }
    }
}
