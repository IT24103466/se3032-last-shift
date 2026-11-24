using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LastShift
{
    /// <summary>Attachment ownership, lightweight garment cloth and non-rigged facial expressions.</summary>
    public sealed class NpcPresentation : IDisposable
    {
        public enum Expression { Observing, Pursuing, Injured, Dead }
        sealed class Eye
        {
            public Transform t, brow, lid;
            public Vector3 scale, center, extent;
            public Quaternion rotation;
            public Renderer renderer;
            public bool wasEnabled;
        }
        readonly List<Eye> eyes = new List<Eye>();
        readonly List<GarmentMotion> garments = new List<GarmentMotion>();
        readonly List<GameObject> details = new List<GameObject>();
        readonly Transform head;
        readonly MeshFilter faceFilter;
        readonly Mesh originalFace, face;
        readonly Vector3[] neutral, expressionVertices;
        readonly Transform neckBody;
        readonly Vector3[] neckRest;
        readonly float[] neckWeight;
        Material faceDetail;
        Mesh detailMesh;
        float injuryUntil, openness = 1f, browAngle;
        public Expression CurrentExpression { get; private set; }
        public int GarmentCount => garments.Count;
        public int SimulatedGarments => garments.FindAll(g => g.Simulating).Count;
        public bool EyesClosed => CurrentExpression == Expression.Dead;

        public NpcPresentation(Transform owner, Transform model)
        {
            var all = model.GetComponentsInChildren<Transform>(true);
            Transform Find(string name) => Array.Find(all, t => t.name == name);
            head = Find("Head");
            var body = Find("Hips") ?? Find("Torso") ?? Find("Body");
            var legs = Array.FindAll(all, t => t.name.StartsWith("Leg_"));
            // Loose visible pieces must belong to an anatomical part before physics detaches it.
            foreach (var t in all)
            {
                if (t == model || t.parent != model || t == head || t == body || t.name == "UpperBodyPose" ||
                    t.name == "Torso" || t.name == "Body" || t.name == "Hips" ||
                    t.name.StartsWith("Arm_") || t.name.StartsWith("Leg_") || t.name == "Trunk" || t.name == "Tail") continue;
                string n = t.name.ToLowerInvariant();
                bool onHead = n.Contains("tusk") || n.Contains("horn") || n.Contains("ear") || n.Contains("hat") ||
                    n.Contains("cap") || n.Contains("eye") || n.Contains("hair") || n.Contains("head");
                var parent = onHead && head != null ? head : body;
                if (parent != null && t.GetComponentInChildren<Renderer>() != null) t.SetParent(parent, true);
            }
            foreach (var t in all)
                if (t.name.IndexOf("sarong", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.name.IndexOf("skirt", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (body != null && t != body && !body.IsChildOf(t)) t.SetParent(body, true);
                    if (t.GetComponent<MeshFilter>() != null) garments.Add(new GarmentMotion(t, owner, legs));
                }
            if (head == null) return;
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
            if (shader != null)
            {
                faceDetail = new Material(shader) { name = "NPC facial detail" };
                faceDetail.SetColor("_Color", new Color(0.055f, 0.035f, 0.025f));
                if (faceDetail.HasProperty("_BaseColor")) faceDetail.SetColor("_BaseColor", new Color(0.055f, 0.035f, 0.025f));
                detailMesh = new Mesh { name = "Eyelid and brow" };
                detailMesh.vertices = new[] { new Vector3(-0.5f,-0.5f,0), new Vector3(0.5f,-0.5f,0), new Vector3(0.5f,0.5f,0), new Vector3(-0.5f,0.5f,0) };
                detailMesh.triangles = new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 }; detailMesh.RecalculateNormals(); detailMesh.RecalculateBounds();
            }
            foreach (var t in head.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.IndexOf("eye", StringComparison.OrdinalIgnoreCase) < 0 || t.GetComponent<Renderer>() == null) continue;
                var r = t.GetComponent<Renderer>();
                var eye = new Eye { t = t, scale = t.localScale, rotation = t.localRotation, renderer = r, wasEnabled = r.enabled,
                    center = head.InverseTransformPoint(r.bounds.center), extent = new Vector3(0.012f, 0.011f, 0.012f) };
                Vector3 e = r.localBounds.extents;
                eye.extent = new Vector3(Mathf.Max(0.006f, Mathf.Abs(t.localScale.x * e.x)), Mathf.Max(0.005f, Mathf.Abs(t.localScale.y * e.y)), Mathf.Max(0.005f, Mathf.Abs(t.localScale.z * e.z)));
                eye.brow = Detail("Expression brow", eye.center + new Vector3(0, eye.extent.y * 1.7f, eye.extent.z + 0.004f), eye.extent.x * 2.4f, eye.extent.y * 0.35f);
                eye.lid = Detail("Closed eyelid", eye.center + Vector3.forward * (eye.extent.z + 0.005f), eye.extent.x * 2.3f, eye.extent.y * 0.24f);
                if (eye.lid != null) eye.lid.gameObject.SetActive(false);
                eyes.Add(eye);
            }
            faceFilter = head.GetComponent<MeshFilter>();
            if (faceFilter != null && faceFilter.sharedMesh != null && faceFilter.sharedMesh.isReadable && eyes.Count > 0)
            {
                originalFace = faceFilter.sharedMesh;
                face = Object.Instantiate(originalFace); face.name = originalFace.name + " expression"; face.MarkDynamic(); faceFilter.sharedMesh = face;
                neutral = face.vertices; expressionVertices = new Vector3[neutral.Length];
                // Two-bone skinning of the existing neck, without an Animator or a full rig.
                // The lower ring stays in the shirt while the upper neck follows the head.
                neckBody = Find("Torso");
                if (neckBody != null && Find("Arm_L") != null)
                {
                    neckRest = new Vector3[neutral.Length]; neckWeight = new float[neutral.Length];
                    var bounds = originalFace.bounds;
                    float seamHeight = Mathf.Min(0.12f, bounds.size.y * 0.34f);
                    for (int i = 0; i < neutral.Length; i++)
                    {
                        neckRest[i] = neckBody.InverseTransformPoint(head.TransformPoint(neutral[i]));
                        neckWeight[i] = 1f - Mathf.SmoothStep(0f, 1f, (neutral[i].y - bounds.min.y) / seamHeight);
                    }
                }
            }
        }

        Transform Detail(string name, Vector3 center, float width, float height)
        {
            if (faceDetail == null) return null;
            var go = new GameObject(name); details.Add(go);
            go.transform.SetParent(head, false); go.transform.localPosition = center; go.transform.localScale = new Vector3(width, height, 1f);
            go.AddComponent<MeshFilter>().sharedMesh = detailMesh;
            var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = faceDetail;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go.transform;
        }

        public void Injured() { injuryUntil = Time.time + 0.7f; }
        public void Tick(float dt, bool pursuing, bool dead)
        {
            CurrentExpression = dead ? Expression.Dead : Time.time < injuryUntil ? Expression.Injured : pursuing ? Expression.Pursuing : Expression.Observing;
            float targetOpen = dead ? 0.04f : CurrentExpression == Expression.Injured ? 0.3f : pursuing ? 0.7f : 1f;
            openness = Mathf.Lerp(openness, targetOpen, 1f - Mathf.Exp(-12f * dt));
            browAngle = Mathf.Lerp(browAngle, dead ? 0f : CurrentExpression == Expression.Injured ? -22f : pursuing ? 20f : -4f, 1f - Mathf.Exp(-10f * dt));
            foreach (var eye in eyes)
            {
                if (eye.t == null) continue;
                eye.t.localScale = new Vector3(eye.scale.x, eye.scale.y * openness, eye.scale.z);
                eye.renderer.enabled = eye.wasEnabled && !dead; // includes emissive eye spheres
                if (eye.brow != null) eye.brow.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sign(eye.center.x) * browAngle);
                if (eye.lid != null) eye.lid.gameObject.SetActive(dead);
            }
            if (face != null)
            {
                for (int i = 0; i < neutral.Length; i++)
                {
                    Vector3 v = neutral[i];
                    foreach (var eye in eyes)
                    {
                        Vector3 d = v - eye.center;
                        // Low-poly animal heads have sparse vertices around each eye;
                        // include the surrounding lid surface rather than only the eye sphere.
                        float nx = Mathf.Abs(d.x) / (eye.extent.x * 3.2f), ny = Mathf.Abs(d.y) / (eye.extent.y * 2.5f);
                        if (nx < 1f && ny < 1f && Mathf.Abs(d.z) < eye.extent.z * 2.5f)
                            v.y = eye.center.y + d.y * Mathf.Lerp(1f, openness, Mathf.Clamp01((1f - nx) * 3f));
                    }
                    if (neckWeight != null && neckBody != null && neckWeight[i] > 0f)
                        v = Vector3.Lerp(v, head.InverseTransformPoint(neckBody.TransformPoint(neckRest[i])), neckWeight[i]);
                    expressionVertices[i] = v;
                }
                face.vertices = expressionVertices; face.RecalculateNormals(); face.RecalculateBounds();
            }
            foreach (var garment in garments) garment.Tick(dt, dead);
        }
        public void Reset()
        {
            CurrentExpression = Expression.Observing;
            injuryUntil = 0f; openness = 1f; browAngle = 0f;
            foreach (var eye in eyes) if (eye.t != null) { eye.t.localScale = eye.scale; eye.renderer.enabled = eye.wasEnabled; if (eye.lid != null) eye.lid.gameObject.SetActive(false); }
            if (face != null) { face.vertices = neutral; face.RecalculateNormals(); face.RecalculateBounds(); }
            foreach (var garment in garments) garment.Reset();
        }
        public void Dispose()
        {
            foreach (var garment in garments) garment.Dispose();
            foreach (var go in details) Destroy(go);
            if (faceFilter != null && originalFace != null) faceFilter.sharedMesh = originalFace;
            Destroy(face); Destroy(faceDetail); Destroy(detailMesh);
        }
        static void Destroy(Object value) { if (value == null) return; if (Application.isPlaying) Object.Destroy(value); else Object.DestroyImmediate(value); }
    }
}
