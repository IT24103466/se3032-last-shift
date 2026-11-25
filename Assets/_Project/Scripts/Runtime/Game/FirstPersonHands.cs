using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
namespace LastShift
{
    [DefaultExecutionOrder(200)]
    public sealed class FirstPersonHands : MonoBehaviour
    {
        public FirstPersonArt art;
        readonly List<GripSurface> surfaces = new List<GripSurface>();
        PlayerGun gun; PlayerController player;
        float obstruction;
        public int HandCount => surfaces.Count;
        public int ContactCount { get { int count = 0; foreach (var s in surfaces) count += s.ContactCount; return count; } }
        void Start()
        {
            player = GetComponent<PlayerController>(); gun = GetComponent<PlayerGun>();
            if (art == null) art = Resources.Load<FirstPersonArt>("FirstPersonArt");
            if (art == null || player == null || player.eyes == null) return;
            var eyes = player.eyes;
            foreach (string kind in new[] { "Pistol", "Shotgun" })
            {
                var frame = eyes.Find(kind); var prefab = kind == "Pistol" ? art.pistol : art.shotgun;
                if (frame == null || prefab == null) continue;
                foreach (var r in frame.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                if (kind == "Shotgun" && art.shotgunReceiver != null && art.shotgunPump != null && frame.Find("Pump") != null)
                {
                    Visual(frame,art.shotgunReceiver,art.shotgunMaterial,"HeldCustomWeapon");
                    Visual(frame.Find("Pump"),art.shotgunPump,art.shotgunMaterial,"Moving custom fore-end");
                }
                else
                {
                    var model = Instantiate(prefab, frame, false); model.name = "HeldCustomWeapon";
                    model.transform.localPosition = Vector3.zero; model.transform.localRotation = Quaternion.Euler(art.weaponEuler);
                    foreach (var c in model.GetComponentsInChildren<Collider>(true)) { c.enabled = false; Destroy(c); }
                    foreach (var r in model.GetComponentsInChildren<Renderer>(true)) { r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = true; }
                }
                bool pistol = kind == "Pistol";
                var gripPos = pistol ? new Vector3(0,-0.04f,-0.01f) : new Vector3(0,-0.025f,-0.02f);
                var gripAngle = new Vector3(pistol ? -14f : -20f,0,0);
                var grip = Box(frame, "Grip contact", gripPos, pistol ? new Vector3(0.028f,0.085f,0.043f) : new Vector3(0.035f,0.06f,0.055f), gripAngle);
                Hand(frame.Find("Hand") ?? frame, frame, art.gripRight, gripPos, gripAngle, pistol ? 1f : 0.92f, new Collider[] { grip }, true);
                if (!pistol)
                {
                    // Keep the imported fore-end and its support hand on the same moving pump.
                    var pump = frame.Find("Pump");
                    if (pump != null)
                    {
                        var contact = new GameObject("Fore-end contact"); contact.layer = 2; contact.transform.SetParent(pump, false);
                        var capsule = contact.AddComponent<CapsuleCollider>(); capsule.isTrigger = true; capsule.direction = 2;
                        var bounds = art.shotgunPump != null ? art.shotgunPump.bounds : new Bounds(Vector3.zero,new Vector3(0.068f,0.068f,0.15f));
                        capsule.center = bounds.center; capsule.radius = Mathf.Max(bounds.extents.x,bounds.extents.y); capsule.height = bounds.size.z;
                        Hand(pump.Find("Hand") ?? pump, pump, art.supportLeft, bounds.center, Vector3.zero, 1f, new Collider[] { capsule }, false);
                    }
                }
            }
            var club = eyes.Find("Viewmodel");
            if (club != null)
            {
                foreach (var r in club.GetComponentsInChildren<MeshRenderer>(true))
                    if (r.name == "Fist" || r.name == "Sleeve" || r.name == "CustomArt_Hand") r.enabled = false;
                var contact = Box(club, "Club contact", new Vector3(0,0.04f,0.063f), new Vector3(0.04f,0.12f,0.04f), new Vector3(-15,0,0));
                Hand(club, club, art.gripRight, new Vector3(0,0.04f,0.063f),new Vector3(-15,0,0),1f,new Collider[] { contact },false);
            }
            var sling = eyes.Find("Slingshot"); var slingHand = sling != null ? sling.Find("Hand") : null;
            if (slingHand != null)
            {
                foreach (var r in slingHand.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                var contact = Box(sling,"Slingshot contact",new Vector3(0,-0.07f,0),new Vector3(0.03f,0.1f,0.035f),Vector3.zero);
                Hand(slingHand,sling,art.slingLeft,new Vector3(0,-0.07f,0),Vector3.zero,1f,new Collider[] {contact},false);
            }
            foreach (var surface in surfaces) surface.Update(0f);
        }
        static BoxCollider Box(Transform frame,string name,Vector3 position,Vector3 size,Vector3 angles)
        {
            var go = new GameObject(name); go.layer = 2; go.transform.SetParent(frame,false); go.transform.localPosition = position; go.transform.localEulerAngles = angles;
            var box = go.AddComponent<BoxCollider>(); box.isTrigger = true; box.size = size; return box;
        }
        static void Visual(Transform parent,Mesh mesh,Material material,string name)
        {
            var go = new GameObject(name,typeof(MeshFilter),typeof(MeshRenderer)); go.transform.SetParent(parent,false);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.GetComponent<MeshRenderer>(); r.sharedMaterial = material; r.shadowCastingMode = ShadowCastingMode.Off;
        }
        void Hand(Transform parent,Transform frame,Mesh mesh,Vector3 position,Vector3 angles,float scale,Collider[] contacts,bool trigger)
        {
            if (mesh == null) return;
            var go = new GameObject("Contact fitted hand",typeof(MeshFilter),typeof(MeshRenderer)); go.layer = 2;
            go.transform.SetParent(frame,false); go.transform.localPosition = position; go.transform.localEulerAngles = angles; go.transform.localScale = Vector3.one * scale;
            go.transform.SetParent(parent,true);
            var filter = go.GetComponent<MeshFilter>(); filter.sharedMesh = mesh;
            var renderer = go.GetComponent<MeshRenderer>(); renderer.sharedMaterial = art.handMaterial; renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = true;
            surfaces.Add(new GripSurface(filter,contacts,trigger));
        }
        void LateUpdate()
        {
            if (gun == null || player == null || player.eyes == null || player.Health.Dead) return;
            foreach (var surface in surfaces) surface.Update(gun.GripPress);
            if (!gun.OnGun || gun.Current.model == null) return;
            float nearest = 0.85f;
            foreach (var hit in Physics.SphereCastAll(player.eyes.position,0.06f,player.eyes.forward,0.85f,~(1<<2),QueryTriggerInteraction.Ignore))
                if (!hit.transform.IsChildOf(transform)) nearest = Mathf.Min(nearest,hit.distance);
            obstruction = Mathf.MoveTowards(obstruction,Mathf.Clamp01((0.85f-nearest)/0.6f),Time.deltaTime * 7f);
            var weapon = gun.Current.model;
            weapon.localPosition += new Vector3(0,-0.07f * obstruction,-0.19f * obstruction);
            weapon.localRotation *= Quaternion.Euler(28f * obstruction,-6f * obstruction,0);
        }
        void OnDestroy() { foreach (var surface in surfaces) surface.Dispose(); }
    }
}
