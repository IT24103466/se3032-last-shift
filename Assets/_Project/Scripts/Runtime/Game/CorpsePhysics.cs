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
            public bool prop;
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
            var articulation = owner.GetComponent<CreatureAnimator>()?.Articulation;
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
                bool segment = articulation != null && articulation.TryBounds(visual, out _);
                bool limb = segment || visual.name.StartsWith("Arm_") || visual.name.StartsWith("Leg_");
                bool prop = visual.name == "Gun" || visual.name == "Lantern" || visual.name == "Flashlight";
                if (visual != hips && visual != chest && visual.name != "Head" && !limb &&
                    visual.name != "Tail" && visual.name != "Trunk" && !prop) continue;
                bool nestedDuplicate = false;
                for (var p = visual.parent; p != null && p != model; p = p.parent)
                    if (p.name == visual.name) { nestedDuplicate = true; break; }
                if (nestedDuplicate) continue;
                if (byVisual.ContainsKey(visual)) continue;
                Bounds bounds;
                if (segment && articulation.TryBounds(visual, out var local))
                { bounds = new Bounds(Vector3.Scale(local.center, visual.lossyScale), Vector3.Scale(local.size, visual.lossyScale)); }
                else if (!TryBounds(visual, out bounds)) continue;
                var proxy = new GameObject(visual.name + " physics");
                proxy.layer = owner.gameObject.layer;
                proxy.transform.SetParent(physicsRoot.transform, false);
                proxy.transform.SetPositionAndRotation(visual.position, visual.rotation);
                var body = proxy.AddComponent<Rigidbody>();
                body.isKinematic = true;
                body.mass = prop ? 1.5f : visual == hips ? 14f : visual == chest ? 22f : visual.name == "Head" ? 5f : limb ? 4f : 1f;
                body.linearDamping = 0.25f;
                body.angularDamping = 1.8f;
                body.interpolation = RigidbodyInterpolation.Interpolate;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                body.solverIterations = 16;
                body.solverVelocityIterations = 8;
                body.maxDepenetrationVelocity = 2f;
                Collider shape;
                if (limb || visual.name == "Tail" || visual.name == "Trunk")
                {
                    var capsule = proxy.AddComponent<CapsuleCollider>();
                    Vector3 size = bounds.size;
                    int axis = size.y >= size.x && size.y >= size.z ? 1 : size.x >= size.z ? 0 : 2;
                    float length = size[axis];
                    float thickness = Mathf.Max(size[(axis + 1) % 3], size[(axis + 2) % 3]);
                    capsule.direction = axis;
                    // Do not put the shoulder/hip socket inside the colliding capsule.
                    // The distal limb still collides with its connected torso, blocking paws
                    // and hands from passing through it without an explosive socket overlap.
                    Vector3 center = bounds.center;
                    float awayFromJoint = Mathf.Sign(center[axis]);
                    if (limb) center[axis] += awayFromJoint * length * 0.2f;
                    capsule.center = center;
                    capsule.radius = Mathf.Max(0.025f, Mathf.Min(thickness * 0.48f, length * 0.22f));
                    capsule.height = Mathf.Max(length * (limb ? 0.6f : 0.96f), capsule.radius * 2f);
                    shape = capsule;
                }
                else
                {
                    var box = proxy.AddComponent<BoxCollider>();
                    box.center = bounds.center;
                    box.size = Vector3.Max(bounds.size * (prop ? 0.95f : 0.98f), Vector3.one * 0.05f);
                    shape = box;
                }
                shape.sharedMaterial = material;
                shape.contactOffset = 0.008f;
                body.centerOfMass = bounds.center;
                var part = new Part { visual = visual, parent = visual.parent, position = visual.localPosition,
                    rotation = visual.localRotation, scale = visual.localScale, body = body, collider = shape, prop = prop };
                parts.Add(part); byVisual.Add(visual, part);
            }
            var pelvis = hips != null && byVisual.TryGetValue(hips, out var hipPart) ? hipPart : null;
            var thorax = chest != null && byVisual.TryGetValue(chest, out var chestPart) ? chestPart : null;
            var core = pelvis ?? thorax;
            if (core == null || parts.Count < 3) { Dispose(); return false; }
            foreach (var part in parts)
            {
                if (part == core || part.prop) continue;
                bool upper = part.visual.name.StartsWith("Arm_") || part.visual.name == "Head" || part.visual.name == "Trunk";
                var parent = part == thorax ? core : upper && thorax != null ? thorax : core;
                var boneParent = articulation?.ParentBone(part.visual);
                bool hinge = boneParent != null && byVisual.TryGetValue(boneParent, out _);
                if (hinge) parent = byVisual[boneParent];
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
                bool knee = part.visual.name.StartsWith("Shin_");
                bool elbow = part.visual.name.StartsWith("Forearm_");
                float swing = hinge ? 5f : arm ? 85f : leg ? 65f : 30f;
                float bend = hinge ? Mathf.DeltaAngle(0f, part.visual.localEulerAngles.x) : 0f;
                joint.lowTwistLimit = new SoftJointLimit { limit = knee ? -bend : elbow ? -135f-bend : hinge ? -25f-bend : arm ? -75f : -35f };
                joint.highTwistLimit = new SoftJointLimit { limit = knee ? 140f-bend : elbow ? -bend : hinge ? 25f-bend : arm ? 75f : 35f };
                joint.swing1Limit = new SoftJointLimit { limit = swing };
                joint.swing2Limit = new SoftJointLimit { limit = hinge ? 5f : arm ? 65f : leg ? 40f : 25f };
                joint.enableProjection = true;
                joint.projectionDistance = 0.04f;
                joint.projectionAngle = 15f;
                joint.enablePreprocessing = false;
                joint.enableCollision = !hinge && (arm || leg);
                Physics.IgnoreCollision(part.collider, parent.collider, hinge || !(arm || leg));
            }
            foreach (var pair in originalColliders) if (pair.Key != null) pair.Key.enabled = false;
            foreach (var part in parts)
            {
                part.visual.SetParent(part.body.transform, true);
                part.body.isKinematic = false;
                part.body.linearVelocity = Vector3.ClampMagnitude(velocity, 5f) + Vector3.ClampMagnitude(push, 1.5f);
                if (part.prop) part.body.angularVelocity = new Vector3(0.5f, 0.8f, -0.4f);
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
                        p.name.IndexOf("sarong", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        p.name.IndexOf("skirt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        p.name.StartsWith("Expression ") || p.name == "Closed eyelid" ||
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
