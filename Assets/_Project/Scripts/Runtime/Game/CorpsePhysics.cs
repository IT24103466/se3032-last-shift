using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LastShift
{
    /// <summary>
    /// A runtime ragdoll for the separate rigid mesh pieces used by the NPCs. Physics proxies
    /// use world-sized shapes, so scaled imported models do not inherit scaled rigidbodies.
    /// </summary>
    public sealed class CorpsePhysics : IDisposable
    {
        sealed class Part
        {
            public Transform visual, parent;
            public Vector3 position, scale;
            public Quaternion rotation;
            public Rigidbody body;
            public Collider collider;
        }

        readonly List<Part> parts = new List<Part>();
        readonly Dictionary<Transform, Part> byVisual = new Dictionary<Transform, Part>();
        readonly Dictionary<Collider, bool> originalColliders = new Dictionary<Collider, bool>();
        GameObject physicsRoot;
        PhysicsMaterial material;
        public bool Active => physicsRoot != null;
        public int BodyCount => parts.Count;
        public bool Sleeping
        {
            get
            {
                if (!Active) return false;
                foreach (var part in parts) if (part.body != null && !part.body.IsSleeping()) return false;
                return true;
            }
        }

        public bool Begin(Transform owner, Transform model, Vector3 push, Vector3 velocity)
        {
            if (Active) return true;
            var all = model.GetComponentsInChildren<Transform>(true);
            Transform Find(string name) => Array.Find(all, t => t.name == name);
            var hips = Find("Hips");
            var chest = Find("Torso") ?? Find("Body");
            if (hips == null && chest == null) return false;

            physicsRoot = new GameObject("Corpse physics");
            SceneManager.MoveGameObjectToScene(physicsRoot, owner.gameObject.scene);
            physicsRoot.transform.position = owner.position;
            physicsRoot.transform.SetParent(owner, true);
            material = new PhysicsMaterial("Corpse contact")
            {
                dynamicFriction = 0.55f, staticFriction = 0.65f, bounciness = 0f,
                frictionCombine = PhysicsMaterialCombine.Average,
                bounceCombine = PhysicsMaterialCombine.Minimum
            };
            foreach (var c in owner.GetComponentsInChildren<Collider>(true)) originalColliders[c] = c.enabled;

            // Build every proxy before reparenting any mesh, so one part cannot capture
            // a neighbour's mesh bounds or change a later joint's intended pivot.
            foreach (var visual in all)
            {
                bool limb = visual.name.StartsWith("Arm_") || visual.name.StartsWith("Leg_");
                if (visual != hips && visual != chest && visual.name != "Head" && !limb &&
                    visual.name != "Tail" && visual.name != "Trunk") continue;
                bool nestedDuplicate = false;
                for (var p = visual.parent; p != null && p != model; p = p.parent)
                    if (p.name == visual.name) { nestedDuplicate = true; break; }
                if (nestedDuplicate) continue;
                if (byVisual.ContainsKey(visual) || !TryBounds(visual, out var bounds)) continue;
                var proxy = new GameObject(visual.name + " physics");
                proxy.layer = owner.gameObject.layer;
                proxy.transform.SetParent(physicsRoot.transform, false);
                proxy.transform.SetPositionAndRotation(visual.position, visual.rotation);
                var body = proxy.AddComponent<Rigidbody>();
                body.isKinematic = true;
                body.mass = visual == hips ? 14f : visual == chest ? 22f : visual.name == "Head" ? 5f : limb ? 4f : 1f;
                body.linearDamping = 0.25f;
                body.angularDamping = 1.8f;
                body.interpolation = RigidbodyInterpolation.Interpolate;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                body.solverIterations = 12;
                body.solverVelocityIterations = 6;
                body.maxDepenetrationVelocity = 2f;
                Collider shape;
                if (limb || visual.name == "Tail" || visual.name == "Trunk")
                {
                    var capsule = proxy.AddComponent<CapsuleCollider>();
                    Vector3 size = bounds.size;
                    int axis = size.y >= size.x && size.y >= size.z ? 1 : size.x >= size.z ? 0 : 2;
                    float length = size[axis];
                    float thickness = Mathf.Min(size[(axis + 1) % 3], size[(axis + 2) % 3]);
                    capsule.direction = axis;
                    capsule.center = bounds.center;
                    capsule.radius = Mathf.Max(0.025f, thickness * 0.45f);
                    capsule.height = Mathf.Max(length * 0.96f, capsule.radius * 2f);
                    shape = capsule;
                }
                else
                {
                    var box = proxy.AddComponent<BoxCollider>();
                    box.center = bounds.center;
                    box.size = Vector3.Max(bounds.size * 0.92f, Vector3.one * 0.05f);
                    shape = box;
                }
                shape.sharedMaterial = material;
                shape.contactOffset = 0.008f;
                body.centerOfMass = bounds.center;
                var part = new Part { visual = visual, parent = visual.parent, position = visual.localPosition,
                    rotation = visual.localRotation, scale = visual.localScale, body = body, collider = shape };
                parts.Add(part); byVisual.Add(visual, part);
            }
            var pelvis = hips != null && byVisual.TryGetValue(hips, out var hipPart) ? hipPart : null;
            var thorax = chest != null && byVisual.TryGetValue(chest, out var chestPart) ? chestPart : null;
            var core = pelvis ?? thorax;
            if (core == null || parts.Count < 3) { Dispose(); return false; }
            foreach (var part in parts)
            {
                if (part == core) continue;
                bool upper = part.visual.name.StartsWith("Arm_") || part.visual.name == "Head" || part.visual.name == "Trunk";
                var parent = part == thorax ? core : upper && thorax != null ? thorax : core;
                if (part == parent) continue;
                Vector3 anchor = part.visual.position;
                if (part == thorax)
                    anchor = pelvis != null ? Vector3.Lerp(pelvis.body.worldCenterOfMass, thorax.body.worldCenterOfMass, 0.5f) : anchor;
                if (part.visual.name == "Trunk")
                {
                    var head = Find("Head");
                    if (head != null && byVisual.TryGetValue(head, out var neck)) parent = neck;
                }
                var joint = part.body.gameObject.AddComponent<CharacterJoint>();
                joint.connectedBody = parent.body;
                joint.autoConfigureConnectedAnchor = false;
                joint.anchor = part.body.transform.InverseTransformPoint(anchor);
                joint.connectedAnchor = parent.body.transform.InverseTransformPoint(anchor);
                joint.axis = Vector3.right; joint.swingAxis = Vector3.forward;
                bool arm = part.visual.name.StartsWith("Arm_");
                bool leg = part.visual.name.StartsWith("Leg_");
                float swing = arm ? 85f : leg ? 65f : 30f;
                joint.lowTwistLimit = new SoftJointLimit { limit = arm ? -75f : -35f };
                joint.highTwistLimit = new SoftJointLimit { limit = arm ? 75f : 35f };
                joint.swing1Limit = new SoftJointLimit { limit = swing };
                joint.swing2Limit = new SoftJointLimit { limit = arm ? 65f : leg ? 40f : 25f };
                joint.enableProjection = true;
                joint.projectionDistance = 0.04f;
                joint.projectionAngle = 15f;
                joint.enablePreprocessing = false;
                joint.enableCollision = false;
                Physics.IgnoreCollision(part.collider, parent.collider);
            }
            foreach (var pair in originalColliders) if (pair.Key != null) pair.Key.enabled = false;
            foreach (var part in parts)
            {
                part.visual.SetParent(part.body.transform, true);
                part.body.isKinematic = false;
                part.body.linearVelocity = Vector3.ClampMagnitude(velocity, 5f) + Vector3.ClampMagnitude(push, 1.5f);
            }
            // A bounded angular impulse breaks the upright balance; limbs then settle under
            // gravity and contact forces instead of being frozen in their last keyed pose.
            core.body.AddTorque(Vector3.Cross(Vector3.up, push) * 1.5f, ForceMode.VelocityChange);
            return true;
        }

        static bool TryBounds(Transform part, out Bounds bounds)
        {
            bounds = new Bounds(); bool found = false;
            foreach (var renderer in part.GetComponentsInChildren<Renderer>(true))
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                bool prop = false;
                for (var p = renderer.transform; p != null && p != part; p = p.parent)
                    if (p.name == "Gun" || p.name == "Lantern" || p.name == "Flashlight" ||
                        (p.name != part.name && (p.name == "Trunk" || p.name == "Tail" ||
                         p.name == "Head" || p.name.StartsWith("Arm_") || p.name.StartsWith("Leg_"))))
                    { prop = true; break; }
                if (prop) continue;
                Bounds local = renderer.localBounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = local.center + Vector3.Scale(local.extents,
                        new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    // Proxy has the visual's rotation/position, but deliberately has unit scale.
                    Vector3 point = Quaternion.Inverse(part.rotation) * (renderer.transform.TransformPoint(corner) - part.position);
                    if (!found) { bounds = new Bounds(point, Vector3.zero); found = true; } else bounds.Encapsulate(point);
                }
            }
            return found && bounds.size.sqrMagnitude > 0.0001f;
        }

        public void Dispose()
        {
            if (physicsRoot != null) physicsRoot.SetActive(false);
            foreach (var part in parts)
            {
                if (part.visual == null || part.parent == null) continue;
                part.visual.SetParent(part.parent, false);
                part.visual.SetLocalPositionAndRotation(part.position, part.rotation);
                part.visual.localScale = part.scale;
            }
            foreach (var pair in originalColliders) if (pair.Key != null) pair.Key.enabled = pair.Value;
            Destroy(physicsRoot); Destroy(material);
            physicsRoot = null; material = null;
            parts.Clear(); byVisual.Clear(); originalColliders.Clear();
        }

        static void Destroy(Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) Object.Destroy(value); else Object.DestroyImmediate(value);
        }
    }
}
