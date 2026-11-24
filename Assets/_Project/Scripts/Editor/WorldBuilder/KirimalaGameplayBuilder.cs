using System.Collections.Generic;
using System.Linq;
using LastShift;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace WorldBuilder.EditorTools
{
    /// <summary>
    /// Tools > World Builder > Build Kirimala Gameplay.
    ///
    /// Makes the game playable: builds the characters out of primitives (no imported models
    /// needed) and saves them as prefabs, then places them in Level_01:
    ///   Player (Nimal)       first-person, pistol, shotgun, slingshot, stick, torch -> at Spawn_Player (jungle camp)
    ///   3 tigers, 4 deer, 1 elephant, 1 hunter (rifle, headlamp)             -> jungle
    ///   5 security guards (pistols, patrol routes, torches),
    ///   4 night-shift workers (shotguns, lanterns)                           -> factory premises
    ///   ammo crates and stone piles along the route
    ///   GameManager (objectives, ring, alarm, HUD)                           -> GAMEPLAY
    /// It also wires the alarm/exit triggers and the tea bags, switches the overview camera off,
    /// and puts Level_01 first in the Build Settings. Re-running replaces everything it made.
    /// </summary>
    public static class KirimalaGameplayBuilder
    {
        const string MatFolder = "Assets/_Project/Art/Characters/Materials";
        const string PrefabFolder = "Assets/_Project/Prefabs/Characters";
        const float Ground = 8f, LoftY = 12.5f;
        static readonly Dictionary<string, Material> mats = new Dictionary<string, Material>();

        [MenuItem("Tools/World Builder/Build Kirimala Gameplay (Player + Characters)", priority = 87)]
        public static void Build()
        {
            var scene = SceneManager.GetActiveScene();
            if (GameObject.Find("Spawn_Player") == null || GameObject.Find("Trigger_Alarm_Premises") == null)
            {
                EditorUtility.DisplayDialog("Kirimala Gameplay", "Open Level_01 and run Build Kirimala Expansion first.", "OK");
                return;
            }
            EnsureFolder(MatFolder);
            EnsureFolder(PrefabFolder);
            EnsureFolder("Assets/_Project/Art/Characters/Meshes");
            mats.Clear();
            built.Clear();
            texCache.Clear();
            bank = new MeshBank("Assets/_Project/Art/Characters/Meshes/Kirimala_Characters.asset");

            var prefabs = new Dictionary<string, GameObject>
            {
                ["Player"] = Save(BuildPlayer()),
                ["Tiger"] = Save(BuildTiger()),
                ["Deer"] = Save(BuildDeer()),
                ["Elephant"] = Save(BuildElephant()),
                ["Guard"] = Save(BuildHuman("Guard", EnemyKind.Guard)),
                ["Worker"] = Save(BuildHuman("Worker", EnemyKind.Worker)),
                ["Hunter"] = Save(BuildHuman("Hunter", EnemyKind.Hunter)),
            };
            bank.Save();

            LevelBakeTool.CreateHierarchy();
            Physics.SyncTransforms();
            var gameplay = Fresh(LevelBakeTool.FindRoot(LevelBakeTool.GameplayRoot).transform, "Kirimala_Gameplay");
            var agents = Fresh(LevelBakeTool.FindRoot(LevelBakeTool.AgentsRoot).transform, "Kirimala_Characters");

            // Game manager
            var gmGo = new GameObject("GameManager");
            gmGo.transform.SetParent(gameplay, false);
            var gm = gmGo.AddComponent<GameManager>();
            gm.alarmMusic = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Audio/Background_Music_Action.prefab");

            // Player at the jungle camp, facing up the trail
            var spawn = GameObject.Find("Spawn_Player").transform;
            var player = (GameObject)PrefabUtility.InstantiatePrefab(prefabs["Player"], gameplay);
            player.transform.SetPositionAndRotation(spawn.position + Vector3.up * 0.05f, Quaternion.Euler(0f, 35f, 0f));
            PrefabUtility.RecordPrefabInstancePropertyModifications(player.transform);

            // The overview camera would fight the player's camera
            var main = GameObject.Find("Main Camera");
            if (main != null && main.GetComponent<Camera>() != null) { Undo.RecordObject(main, "Disable overview camera"); main.SetActive(false); }

            // Jungle animals
            int n = 0;
            GameObject Enemy(string key, Transform parent, Vector3 p, Transform route = null, int wp = 0, float wander = 10f)
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefabs[key], parent);
                go.name = $"{key}_{++n:00}";
                go.transform.SetPositionAndRotation(Snap(p), Quaternion.Euler(0f, (n * 73) % 360, 0f));
                var ai = go.GetComponent<EnemyAI>();
                ai.patrolRoute = route; ai.startWaypoint = wp; ai.wanderRadius = wander;
                PrefabUtility.RecordPrefabInstancePropertyModifications(ai);
                PrefabUtility.RecordPrefabInstancePropertyModifications(go.transform);
                return go;
            }
            var jungle = Group(agents, "Jungle");
            foreach (var p in new[] { new Vector2(44f, -52f), new Vector2(63f, -30f), new Vector2(14f, -17f) })
                Enemy("Tiger", jungle, Outdoor(p), wander: 12f);
            foreach (var p in new[] { new Vector2(20f, -28f), new Vector2(23f, -26f), new Vector2(52f, -10f), new Vector2(-6f, -42f) })
                Enemy("Deer", jungle, Outdoor(p), wander: 8f);
            Enemy("Elephant", jungle, Outdoor(new Vector2(44f, -29f)), wander: 7f);
            Enemy("Hunter", jungle, Outdoor(new Vector2(46f, -40f)), wander: 14f);   // poacher working the slopes east of the trail

            // Factory people
            var factory = Group(agents, "Factory");
            Transform R(string name) { var g = GameObject.Find(name); return g != null ? g.transform : null; }
            Enemy("Guard", factory, new Vector3(12.3f, Ground, 49.5f), R("Route_Yard_Compound"), 0);
            Enemy("Guard", factory, new Vector3(64f, Ground, 46f), R("Route_Yard_Compound"), 3);
            Enemy("Guard", factory, new Vector3(58f, Ground, 70.6f), null, 0, 5f);
            Enemy("Guard", factory, new Vector3(18f, Ground, 78f), R("Route_BackLane"), 0);
            Enemy("Guard", factory, new Vector3(15f, Ground, 53.5f), R("Route_Factory"), 0);
            Enemy("Worker", factory, new Vector3(26f, Ground, 57.5f), null, 0, 6f);
            Enemy("Worker", factory, new Vector3(25f, LoftY, 70f), null, 0, 6f);
            Enemy("Worker", factory, new Vector3(41f, Ground, 57f), null, 0, 3f);
            Enemy("Worker", factory, new Vector3(67f, Ground, 58f), null, 0, 6f);

            // Stone piles to refill the slingshot along the route
            var piles = Group(gameplay, "PebblePiles");
            var stone = M("Pebble", new Color(0.42f, 0.4f, 0.37f));
            int pileCount = 0;
            void Pile(Vector3 p)
            {
                var pile = new GameObject("PebblePile").transform;
                pile.SetParent(piles, false);
                pile.position = p;
                var rng = new System.Random(pileCount * 31 + 7);
                for (int i = 0; i < 9; i++)
                {
                    float a = i * 2.4f, r = 0.05f + 0.035f * i;
                    P(pile, "Stone", PrimitiveType.Sphere, new Vector3(Mathf.Cos(a) * r * 0.6f, 0.03f + (i < 3 ? 0.05f : 0f), Mathf.Sin(a) * r * 0.6f),
                      new Vector3(0.09f, 0.06f, 0.08f) * (0.8f + (float)rng.NextDouble() * 0.5f), stone);
                }
                var col = pile.gameObject.AddComponent<BoxCollider>();
                col.center = new Vector3(0f, 0.08f, 0f); col.size = new Vector3(0.45f, 0.18f, 0.45f);
                pile.gameObject.AddComponent<PebblePile>();
                pileCount++;
            }
            foreach (var p in new[] { new Vector2(10.5f, -54.5f), new Vector2(29f, -26f), new Vector2(57.5f, -19.5f), new Vector2(31.5f, -4f), new Vector2(40f, 22.5f), new Vector2(47.5f, 37.5f) })
                Pile(Outdoor(p));
            Pile(new Vector3(24f, Ground, 44.5f));      // yard
            Pile(new Vector3(62f, Ground, 58.5f));      // east compound
            Pile(new Vector3(28f, LoftY, 69f));         // loft
            Pile(new Vector3(42f, Ground, 64f));        // boiler room

            // Ammo crates for the pistol and shotgun, beside some of the stone piles
            var crates = Group(gameplay, "AmmoCrates");
            Material olive = M("AmmoCrate", new Color(0.26f, 0.31f, 0.18f)), stripe = M("AmmoStripe", new Color(0.85f, 0.68f, 0.12f));
            void Crate(Vector3 p, int rounds, int shells)
            {
                var crate = new GameObject("AmmoCrate").transform;
                crate.SetParent(crates, false);
                crate.position = p;
                crate.rotation = Quaternion.Euler(0f, (crates.childCount * 47) % 360, 0f);
                P(crate, "Box", PrimitiveType.Cube, new Vector3(0f, 0.15f, 0f), new Vector3(0.55f, 0.3f, 0.34f), olive);
                P(crate, "Lid", PrimitiveType.Cube, new Vector3(0f, 0.31f, 0f), new Vector3(0.57f, 0.03f, 0.36f), olive);
                P(crate, "Stripe", PrimitiveType.Cube, new Vector3(0f, 0.2f, 0f), new Vector3(0.56f, 0.05f, 0.35f), stripe);
                foreach (float sx in new[] { -1f, 1f })
                    P(crate, "Handle", PrimitiveType.Cube, new Vector3(sx * 0.29f, 0.2f, 0f), new Vector3(0.03f, 0.04f, 0.12f), M("Black", new Color(0.04f, 0.04f, 0.05f)));
                var col = crate.gameObject.AddComponent<BoxCollider>();
                col.center = new Vector3(0f, 0.16f, 0f); col.size = new Vector3(0.58f, 0.33f, 0.37f);
                var a = crate.gameObject.AddComponent<AmmoBox>();
                a.pistolRounds = rounds; a.shells = shells;
            }
            foreach (var p in new[] { new Vector2(58.2f, -19.5f), new Vector2(32.2f, -4f), new Vector2(40.7f, 22.5f), new Vector2(48.2f, 37.5f) })
                Crate(Outdoor(p), 18, 6);
            Crate(new Vector3(24.7f, Ground, 44.5f), 18, 6);      // yard
            Crate(new Vector3(62.7f, Ground, 58.5f), 18, 6);      // east compound
            Crate(new Vector3(28.7f, LoftY, 69f), 12, 4);         // loft
            Crate(new Vector3(42.7f, Ground, 64f), 12, 4);        // boiler room

            // Triggers and tea bags
            AddOnce<AlarmZone>("Trigger_Alarm_Premises");
            AddOnce<ExitZone>("Trigger_Exit_MainGate");
            var objectives = GameObject.Find("Objectives");
            int bags = 0;
            if (objectives != null)
                foreach (Transform b in objectives.transform)
                {
                    var bag = b.GetComponent<TeaBag>();
                    if (bag == null) bag = Undo.AddComponent<TeaBag>(b.gameObject);
                    bag.place = b.name.Substring(b.name.LastIndexOf('_') + 1);
                    bags++;
                }

            // Level_01 is the game: first scene in the build
            var list = EditorBuildSettings.scenes.Where(s => s.path != scene.path).ToList();
            list.Insert(0, new EditorBuildSettingsScene(scene.path, true));
            EditorBuildSettings.scenes = list.ToArray();

            EditorSceneManager.MarkSceneDirty(scene);
            AssetDatabase.SaveAssets();
            Debug.Log($"[World Builder] Gameplay built: player (pistol, shotgun, slingshot), 3 tigers, 4 deer, 1 elephant, 1 hunter, " +
                      $"5 guards, 4 workers, {crates.childCount} ammo crates, {bags} tea bags, GameManager. Press Play to play the game.");
        }

        // ------------------------------------------------------------------ placement

        static Vector3 Outdoor(Vector2 xz)
        {
            if (Physics.Raycast(new Vector3(xz.x, 80f, xz.y), Vector3.down, out var hit, 200f, ~(1 << 2), QueryTriggerInteraction.Ignore))
                return hit.point;
            return new Vector3(xz.x, 0f, xz.y);
        }

        static Vector3 Snap(Vector3 p) => NavMesh.SamplePosition(p, out var hit, 4f, NavMesh.AllAreas) ? hit.position : p;

        static void AddOnce<T>(string name) where T : Component
        {
            var go = GameObject.Find(name);
            if (go != null && go.GetComponent<T>() == null) Undo.AddComponent<T>(go);
        }

        // ------------------------------------------------------------------ characters

        static GameObject BuildPlayer()
        {
            var root = new GameObject("Player") { tag = "Player" };
            var cc = root.AddComponent<CharacterController>();
            cc.height = 1.8f; cc.radius = 0.3f; cc.center = new Vector3(0f, 0.9f, 0f); cc.stepOffset = 0.4f; cc.slopeLimit = 45f;
            var hp = root.AddComponent<Health>();
            hp.max = 150f; hp.regenDelay = 4f; hp.regenPerSecond = 8f;
            var pc = root.AddComponent<PlayerController>();
            root.AddComponent<PlayerCombat>();
            var sling = root.AddComponent<PlayerSlingshot>();
            sling.pebbleMaterial = M("Pebble", new Color(0.42f, 0.4f, 0.37f));
            sling.trailMaterial = M("PebbleTrail", new Color(1f, 0.9f, 0.7f), 1.5f);

            var eyes = new GameObject("Eyes");
            eyes.transform.SetParent(root.transform, false);
            eyes.transform.localPosition = new Vector3(0f, 1.65f, 0f);
            eyes.tag = "MainCamera";
            var cam = eyes.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f; cam.fieldOfView = 70f;
            var data = cam.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            eyes.AddComponent<AudioListener>();

            var torch = new GameObject("Torch").AddComponent<Light>();
            torch.transform.SetParent(eyes.transform, false);
            torch.type = LightType.Spot; torch.spotAngle = 65f; torch.innerSpotAngle = 38f; torch.range = 40f; torch.intensity = 8f;
            torch.color = new Color(1f, 0.95f, 0.85f); torch.shadows = LightShadows.Soft; torch.enabled = false;
            pc.eyes = eyes.transform; pc.torch = torch;

            // First-person stick and hand
            var vm = new GameObject("Viewmodel").transform;
            vm.SetParent(eyes.transform, false);
            vm.localPosition = new Vector3(0.34f, -0.34f, 0.5f);
            vm.localRotation = Quaternion.Euler(30f, -10f, 48f);    // club angled up and across the view, like it's held ready
            S(vm, "Fist", "Player", new Sculpt().Ell(V(0f, 0f, 0f), V(0.045f, 0.05f, 0.058f))
                .Ell(V(-0.03f, 0.02f, 0.03f), V(0.016f, 0.03f, 0.016f), 0.015f)
                .Cone(V(0f, -0.02f, -0.04f), V(0f, -0.09f, -0.2f), 0.036f, 0.04f, 0.03f), M("Skin", new Color(0.42f, 0.27f, 0.17f)), 0.007f, shadows: false);
            S(vm, "Sleeve", "Player", new Sculpt().Cone(V(0f, -0.08f, -0.18f), V(0f, -0.14f, -0.42f), 0.05f, 0.06f), Cloth("NimalShirt", new Color(0.2f, 0.28f, 0.42f)), 0.01f, shadows: false);
            P(vm, "Stick", PrimitiveType.Cylinder, new Vector3(0f, 0.2f, 0.02f), new Vector3(0.04f, 0.23f, 0.04f), M("Wood", new Color(0.36f, 0.24f, 0.14f)), new Vector3(-15f, 0f, 0f), false);

            // The slingshot, held out in the left hand (the band is a LineRenderer the script stretches)
            var slingVm = new GameObject("Slingshot").transform;
            slingVm.SetParent(eyes.transform, false);
            slingVm.localPosition = new Vector3(-0.1f, -0.2f, 0.45f);
            slingVm.localRotation = Quaternion.Euler(-8f, 4f, 0f);
            var wood = M("SlingWood", new Color(0.45f, 0.3f, 0.17f));
            S(slingVm, "Fork", "Slingshot", new Sculpt()
                .Cone(V(0f, -0.13f, 0f), V(0f, -0.01f, 0f), 0.014f, 0.016f, 0.02f)
                .Cone(V(0f, -0.01f, 0f), V(-0.05f, 0.07f, 0f), 0.014f, 0.011f, 0.025f)
                .Cone(V(0f, -0.01f, 0f), V(0.05f, 0.07f, 0f), 0.014f, 0.011f, 0.025f), wood, 0.004f, shadows: false);
            S(slingVm, "Hand", "Slingshot", new Sculpt().Ell(V(0f, -0.08f, -0.005f), V(0.04f, 0.045f, 0.045f))
                .Ell(V(0.03f, -0.06f, 0.02f), V(0.013f, 0.025f, 0.013f), 0.012f)
                .Cone(V(0f, -0.1f, -0.03f), V(-0.02f, -0.2f, -0.22f), 0.033f, 0.038f, 0.03f), M("Skin", new Color(0.42f, 0.27f, 0.17f)), 0.006f, shadows: false);
            Pivot(slingVm, "TipL", new Vector3(-0.05f, 0.075f, 0f));
            Pivot(slingVm, "TipR", new Vector3(0.05f, 0.075f, 0f));
            var pouch = Pivot(slingVm, "Pouch", new Vector3(0f, 0.07f, -0.01f));
            P(pouch, "Leather", PrimitiveType.Cube, Vector3.zero, new Vector3(0.03f, 0.018f, 0.02f), M("Leather", new Color(0.28f, 0.17f, 0.1f)), shadows: false);
            var line = slingVm.gameObject.AddComponent<LineRenderer>();
            line.useWorldSpace = false; line.positionCount = 3; line.widthMultiplier = 0.006f;
            line.sharedMaterial = M("Rubber", new Color(0.2f, 0.12f, 0.08f));
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.SetPositions(new[] { new Vector3(-0.05f, 0.075f, 0f), new Vector3(0f, 0.07f, -0.01f), new Vector3(0.05f, 0.075f, 0f) });

            // Firearms (weapon 1 and 2). Each model's root has no rotation: PlayerGun animates it.
            var gun = root.AddComponent<PlayerGun>();
            gun.tracerMaterial = M("Tracer", new Color(1f, 0.85f, 0.5f), 4f);
            gun.sparkMaterial = M("Spark", new Color(1f, 0.7f, 0.3f), 5f);
            Material metal = GunMetal("GunMetal", new Color(0.11f, 0.11f, 0.12f)), steel = GunMetal("GunSteel", new Color(0.32f, 0.33f, 0.35f)),
                     polymer = M("GunGrip", new Color(0.07f, 0.07f, 0.07f)), walnut = M("GunWood", new Color(0.33f, 0.18f, 0.09f)),
                     skinM = M("Skin", new Color(0.42f, 0.27f, 0.17f)), sleeve = Cloth("NimalShirt", new Color(0.2f, 0.28f, 0.42f));

            void Hand(Transform parent, Vector3 pos, Vector3 euler)
            {
                var h = Pivot(parent, "Hand", pos);
                h.localRotation = Quaternion.Euler(euler);
                S(h, "Fist", "Player", null, skinM, 0.007f, shadows: false);
                S(h, "Sleeve", "Player", null, sleeve, 0.01f, shadows: false);
            }

            // Pistol: slide, frame, grip, trigger guard, sights
            var pistol = Pivot(eyes.transform, "Pistol", new Vector3(0.15f, -0.16f, 0.36f));
            P(pistol, "Slide", PrimitiveType.Cube, new Vector3(0f, 0.035f, 0.07f), new Vector3(0.03f, 0.033f, 0.19f), metal, shadows: false);
            P(pistol, "Frame", PrimitiveType.Cube, new Vector3(0f, 0.007f, 0.055f), new Vector3(0.027f, 0.024f, 0.16f), polymer, shadows: false);
            P(pistol, "Barrel", PrimitiveType.Cylinder, new Vector3(0f, 0.037f, 0.165f), new Vector3(0.012f, 0.006f, 0.012f), steel, new Vector3(90f, 0f, 0f), false);
            P(pistol, "Grip", PrimitiveType.Cube, new Vector3(0f, -0.04f, -0.01f), new Vector3(0.026f, 0.085f, 0.04f), polymer, new Vector3(-14f, 0f, 0f), false);
            P(pistol, "Guard", PrimitiveType.Cube, new Vector3(0f, -0.014f, 0.035f), new Vector3(0.006f, 0.022f, 0.04f), polymer, shadows: false);
            P(pistol, "FrontSight", PrimitiveType.Cube, new Vector3(0f, 0.055f, 0.155f), new Vector3(0.004f, 0.008f, 0.006f), steel, shadows: false);
            P(pistol, "RearSight", PrimitiveType.Cube, new Vector3(0f, 0.055f, -0.015f), new Vector3(0.022f, 0.008f, 0.006f), metal, shadows: false);
            Hand(pistol, new Vector3(0f, -0.05f, -0.015f), new Vector3(-10f, 0f, 0f));
            var pistolMuzzle = Pivot(pistol, "Muzzle", new Vector3(0f, 0.037f, 0.175f));

            // Pump shotgun: receiver, barrel, magazine tube, sliding fore-end (pump), wooden stock
            var shotgun = Pivot(eyes.transform, "Shotgun", new Vector3(0.14f, -0.17f, 0.3f));
            P(shotgun, "Receiver", PrimitiveType.Cube, new Vector3(0f, 0.02f, 0.05f), new Vector3(0.042f, 0.058f, 0.16f), metal, shadows: false);
            P(shotgun, "Barrel", PrimitiveType.Cylinder, new Vector3(0f, 0.038f, 0.38f), new Vector3(0.022f, 0.25f, 0.022f), steel, new Vector3(90f, 0f, 0f), false);
            P(shotgun, "Tube", PrimitiveType.Cylinder, new Vector3(0f, 0.007f, 0.33f), new Vector3(0.019f, 0.2f, 0.019f), metal, new Vector3(90f, 0f, 0f), false);
            P(shotgun, "Bead", PrimitiveType.Sphere, new Vector3(0f, 0.052f, 0.62f), Vector3.one * 0.007f, steel, shadows: false);
            P(shotgun, "Stock", PrimitiveType.Cube, new Vector3(0f, -0.015f, -0.14f), new Vector3(0.038f, 0.065f, 0.22f), walnut, new Vector3(8f, 0f, 0f), false);
            P(shotgun, "Wrist", PrimitiveType.Cube, new Vector3(0f, -0.02f, -0.02f), new Vector3(0.032f, 0.05f, 0.07f), walnut, new Vector3(-20f, 0f, 0f), false);
            var pump = Pivot(shotgun, "Pump", new Vector3(0f, 0.007f, 0.22f));
            P(pump, "ForeEnd", PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.033f, 0.065f, 0.033f), walnut, new Vector3(90f, 0f, 0f), false);
            Hand(shotgun, new Vector3(0f, -0.045f, -0.03f), new Vector3(-20f, 0f, 0f));
            Hand(pump, new Vector3(-0.01f, -0.03f, 0f), new Vector3(-5f, 28f, 0f));
            var shotgunMuzzle = Pivot(shotgun, "Muzzle", new Vector3(0f, 0.038f, 0.64f));

            // Weapon visibility uses scene lighting; a player point light leaks onto nearby walls.

            gun.guns[0].model = pistol; gun.guns[0].muzzle = pistolMuzzle;
            gun.guns[1].model = shotgun; gun.guns[1].muzzle = shotgunMuzzle; gun.guns[1].pump = pump;
            return root;
        }

        /// <summary>Gun metal: dark and a little shiny.</summary>
        static Material GunMetal(string name, Color c)
        {
            var m = M(name, c);
            m.SetFloat("_Metallic", 0.75f);
            m.SetFloat("_Smoothness", 0.55f);
            return m;
        }

        // Shorthand for sculpt points
        static Vector3 V(float x, float y, float z) => new Vector3(x, y, z);
        static (Vector3, float) Pt(float x, float y, float z, float r) => (new Vector3(x, y, z), r);

        static GameObject BuildHuman(string name, EnemyKind kind)
        {
            bool guard = kind == EnemyKind.Guard, hunter = kind == EnemyKind.Hunter;
            bool trousers = guard || hunter;   // the worker wears a sarong
            var root = Root(name, kind, new Vector3(0f, 0.9f, 0f), 0.3f, 1.8f, 1, 0.35f, 1.8f);
            var anim = root.GetComponent<CreatureAnimator>(); anim.stride = 35f; anim.cadence = 2.6f; anim.lunge = 0.35f;
            anim.twoHanded = !guard;   // the worker's shotgun and the hunter's rifle take both hands
            var m = Model(root);
            Material skin = M("Skin", new Color(0.42f, 0.27f, 0.17f)), dark = M("Black", new Color(0.04f, 0.04f, 0.05f)),
                     hair = M("Hair", new Color(0.05f, 0.04f, 0.035f)), shoe = M("Shoe", new Color(0.09f, 0.07f, 0.06f));
            Material shirt = guard ? Cloth("GuardKhaki", new Color(0.58f, 0.52f, 0.36f)) : hunter ? Cloth("HunterJacket", new Color(0.27f, 0.3f, 0.18f)) : Cloth("WorkerShirt", new Color(0.34f, 0.47f, 0.4f));
            Material lower = guard ? Cloth("GuardTrousers", new Color(0.16f, 0.18f, 0.25f)) : hunter ? Cloth("HunterTrousers", new Color(0.36f, 0.31f, 0.22f)) : Cloth("WorkerSarong", new Color(0.5f, 0.13f, 0.17f));
            Material hat = guard ? Cloth("GuardCap", new Color(0.1f, 0.12f, 0.22f)) : hunter ? Cloth("HunterHat", new Color(0.3f, 0.22f, 0.13f)) : Cloth("WhiteCloth", new Color(0.88f, 0.86f, 0.8f));
            Material metal = GunMetal("GunMetal", new Color(0.11f, 0.11f, 0.12f)), walnut = M("GunWood", new Color(0.33f, 0.18f, 0.09f));

            // Torso: chest, waist and shoulders melted together
            S(m, "Torso", name, new Sculpt()
                .Ell(V(0f, 1.36f, 0.005f), V(0.2f, 0.17f, 0.115f))
                .Cone(V(0f, 1.06f, 0f), V(0f, 1.3f, 0f), 0.14f, 0.165f, 0.08f)
                .Cone(V(-0.17f, 1.45f, 0f), V(0.17f, 1.45f, 0f), 0.075f, 0.075f, 0.08f)
                .Axis(V(0f, 1f, 0f), V(0f, 1.6f, 0f), Vector3.forward), shirt, 0.02f);
            if (trousers)
            {
                S(m, "Hips", name, new Sculpt().Ell(V(0f, 0.98f, 0f), V(0.17f, 0.12f, 0.115f)).Cone(V(0f, 0.98f, 0f), V(0f, 1.1f, 0f), 0.15f, 0.142f, 0.06f), lower, 0.02f);
                S(m, "Belt", name, new Sculpt().Ell(V(0f, 1.08f, 0f), V(0.163f, 0.028f, 0.122f)), dark, 0.012f);
                if (guard) S(m, "Badge", name, new Sculpt().Ell(V(0.09f, 1.44f, 0.115f), V(0.022f, 0.026f, 0.008f)), M("Brass", new Color(0.85f, 0.65f, 0.2f)), 0.006f);
                else S(m, "Bandolier", name, new Sculpt().Cone(V(-0.16f, 1.5f, 0.11f), V(0.15f, 1.08f, 0.12f), 0.025f, 0.025f, 0.01f)
                    .Cone(V(-0.16f, 1.5f, -0.11f), V(0.15f, 1.08f, -0.12f), 0.025f, 0.025f, 0.01f), M("Leather", new Color(0.28f, 0.17f, 0.1f)), 0.01f);
            }
            else
                S(m, "Sarong", name, new Sculpt().Cone(V(0f, 1.1f, 0f), V(0f, 0.48f, 0f), 0.155f, 0.168f, 0.05f).Axis(V(0f, 1.1f, 0f), V(0f, 0.4f, 0f), Vector3.forward), lower, 0.022f);

            // Head: neck, skull, jaw, nose, ears
            var head = Pivot(m, "Head", new Vector3(0f, 1.6f, 0f));
            S(head, "Face", "Human", new Sculpt()
                .Cone(V(0f, -0.07f, -0.005f), V(0f, 0.07f, 0f), 0.056f, 0.054f)
                .Ell(V(0f, 0.16f, 0f), V(0.095f, 0.118f, 0.108f), 0.04f)
                .Ell(V(0f, 0.09f, 0.03f), V(0.078f, 0.075f, 0.082f), 0.05f)
                .Cone(V(0f, 0.15f, 0.1f), V(0f, 0.105f, 0.122f), 0.013f, 0.02f, 0.02f)
                .Ell(V(-0.097f, 0.14f, 0f), V(0.017f, 0.032f, 0.022f), 0.015f)
                .Ell(V(0.097f, 0.14f, 0f), V(0.017f, 0.032f, 0.022f), 0.015f), skin, 0.011f);
            foreach (float sx in new[] { -1f, 1f })
                P(head, "Eye", PrimitiveType.Sphere, new Vector3(sx * 0.036f, 0.165f, 0.099f), new Vector3(0.024f, 0.02f, 0.014f), dark);
            if (guard)
            {
                S(head, "Cap", "Guard", new Sculpt().Cone(V(0f, 0.2f, -0.005f), V(0f, 0.27f, 0f), 0.108f, 0.118f, 0.02f)
                    .Ell(V(0f, 0.205f, 0.1f), V(0.085f, 0.012f, 0.065f), 0.02f), hat, 0.011f);
                S(head, "Moustache", "Guard", new Sculpt().Ell(V(0f, 0.093f, 0.104f), V(0.034f, 0.009f, 0.012f)), hair, 0.005f);
            }
            else if (hunter)
            {
                // Wide-brimmed bush hat and a stubbly beard
                S(head, "Hat", "Hunter", new Sculpt().Cone(V(0f, 0.2f, -0.005f), V(0f, 0.29f, 0f), 0.11f, 0.1f, 0.03f)
                    .Ell(V(0f, 0.21f, 0f), V(0.19f, 0.014f, 0.19f), 0.02f), hat, 0.011f);
                S(head, "Beard", "Hunter", new Sculpt().Ell(V(0f, 0.06f, 0.07f), V(0.07f, 0.045f, 0.05f)).Ell(V(0f, 0.093f, 0.104f), V(0.034f, 0.009f, 0.012f), 0.02f), hair, 0.006f);
            }
            else
            {
                S(head, "HeadCloth", "Worker", new Sculpt().Ell(V(0f, 0.2f, -0.005f), V(0.105f, 0.08f, 0.116f))
                    .Ell(V(0f, 0.17f, -0.1f), V(0.05f, 0.04f, 0.045f), 0.03f), hat, 0.011f);
            }

            foreach (var (side, sx) in new[] { ("L", -1f), ("R", 1f) })
            {
                // Legs: thigh to ankle, with a shoe
                var leg = Pivot(m, "Leg_" + side, new Vector3(sx * 0.095f, 0.92f, 0f));
                S(leg, "Leg", name, new Sculpt().Chain(0.04f, Pt(0f, 0.02f, 0f, 0.083f), Pt(0f, -0.42f, 0.015f, 0.058f), Pt(0f, -0.8f, -0.005f, 0.044f))
                    .Axis(V(0f, 0f, 0f), V(0f, -0.85f, 0f), Vector3.forward), trousers ? lower : skin, 0.018f);
                S(leg, "Shoe", "Human", new Sculpt().Cone(V(0f, -0.84f, -0.025f), V(0f, -0.865f, 0.09f), 0.047f, 0.04f, 0.03f)
                    .Ell(V(0f, -0.875f, 0.03f), V(0.05f, 0.02f, 0.09f), 0.02f), shoe, 0.011f);

                // Arms: sleeve, forearm, hand
                var arm = Pivot(m, "Arm_" + side, new Vector3(sx * 0.235f, 1.46f, 0f));
                if (trousers)
                    S(arm, "Sleeve", name + side, new Sculpt().Chain(0.03f, Pt(0f, 0f, 0f, 0.06f), Pt(sx * 0.02f, -0.29f, 0f, 0.047f), Pt(sx * 0.025f, -0.52f, 0.02f, 0.039f)), shirt, 0.014f);
                else
                {
                    S(arm, "Sleeve", name + side, new Sculpt().Chain(0.03f, Pt(0f, 0f, 0f, 0.062f), Pt(sx * 0.015f, -0.19f, 0f, 0.055f)), shirt, 0.014f);
                    S(arm, "Forearm", "Worker" + side, new Sculpt().Chain(0.03f, Pt(sx * 0.012f, -0.16f, 0f, 0.043f), Pt(sx * 0.02f, -0.3f, 0f, 0.041f), Pt(sx * 0.025f, -0.52f, 0.02f, 0.033f)), skin, 0.013f);
                }
                S(arm, "Hand", "Human" + side, new Sculpt().Ell(V(sx * 0.028f, -0.6f, 0.025f), V(0.032f, 0.048f, 0.026f))
                    .Ell(V(sx * 0.005f, -0.57f, 0.05f), V(0.013f, 0.024f, 0.013f), 0.015f), skin, 0.008f);
                if (side == "R")
                {
                    // The gun lies along the arm (barrel pointing down the arm, past the hand), so when
                    // the arm comes up to aim the barrel points at the target. Local +Z is "up" then.
                    var g = Pivot(arm, "Gun", new Vector3(0.03f, -0.6f, 0.03f));
                    if (guard)
                    {
                        P(g, "Slide", PrimitiveType.Cube, new Vector3(0f, -0.08f, 0.03f), new Vector3(0.03f, 0.18f, 0.034f), metal);
                        P(g, "Grip", PrimitiveType.Cube, new Vector3(0f, 0f, -0.01f), new Vector3(0.027f, 0.04f, 0.08f), dark);
                        Pivot(g, "Muzzle", new Vector3(0f, -0.18f, 0.03f));
                    }
                    else if (hunter)
                    {
                        P(g, "Barrel", PrimitiveType.Cylinder, new Vector3(0f, -0.38f, 0.04f), new Vector3(0.017f, 0.3f, 0.017f), metal);
                        P(g, "Receiver", PrimitiveType.Cube, new Vector3(0f, -0.04f, 0.04f), new Vector3(0.034f, 0.2f, 0.05f), metal);
                        P(g, "Scope", PrimitiveType.Cylinder, new Vector3(0f, -0.06f, 0.09f), new Vector3(0.03f, 0.09f, 0.03f), dark);
                        P(g, "Stock", PrimitiveType.Cube, new Vector3(0f, 0.15f, 0.01f), new Vector3(0.04f, 0.3f, 0.07f), walnut);
                        P(g, "Fore", PrimitiveType.Cube, new Vector3(0f, -0.25f, 0.025f), new Vector3(0.038f, 0.2f, 0.04f), walnut);
                        Pivot(g, "Muzzle", new Vector3(0f, -0.69f, 0.04f));
                    }
                    else
                    {
                        foreach (float bx in new[] { -0.012f, 0.012f })
                            P(g, "Barrel", PrimitiveType.Cylinder, new Vector3(bx, -0.3f, 0.035f), new Vector3(0.022f, 0.25f, 0.022f), metal);
                        P(g, "Stock", PrimitiveType.Cube, new Vector3(0f, 0.12f, 0.005f), new Vector3(0.04f, 0.26f, 0.06f), walnut);
                        P(g, "Fore", PrimitiveType.Cube, new Vector3(0f, -0.14f, 0.015f), new Vector3(0.04f, 0.14f, 0.035f), walnut);
                        Pivot(g, "Muzzle", new Vector3(0f, -0.56f, 0.035f));
                    }
                }
            }

            if (hunter)
            {
                // Headlamp: a narrow beam that gives the hunter away in the dark jungle
                var lamp = new GameObject("Headlamp").AddComponent<Light>();
                lamp.transform.SetParent(head, false);
                lamp.transform.localPosition = new Vector3(0f, 0.21f, 0.12f);
                lamp.transform.localRotation = Quaternion.Euler(10f, 0f, 0f);
                lamp.type = LightType.Spot; lamp.spotAngle = 32f; lamp.range = 22f; lamp.intensity = 3f;
                lamp.color = new Color(1f, 0.97f, 0.88f); lamp.shadows = LightShadows.None;
                P(head, "LampBulb", PrimitiveType.Sphere, new Vector3(0f, 0.21f, 0.115f), Vector3.one * 0.03f, M("LampGlow", new Color(1f, 0.95f, 0.8f), 4f), shadows: false);
            }
            else if (guard)
            {
                // Torch beam: guards are easy to spot at night (and they light you up)
                var beam = new GameObject("Flashlight").AddComponent<Light>();
                beam.transform.SetParent(head, false);
                beam.transform.localPosition = new Vector3(0.12f, 0.1f, 0.15f);
                beam.transform.localRotation = Quaternion.Euler(12f, 0f, 0f);
                beam.type = LightType.Spot; beam.spotAngle = 45f; beam.range = 14f; beam.intensity = 3f;
                beam.color = new Color(1f, 0.95f, 0.85f); beam.shadows = LightShadows.None;
            }
            else
            {
                // Night-shift workers carry a kerosene lantern: you can see them coming
                var holder = Pivot(m.Find("Arm_L"), "Lantern", new Vector3(-0.03f, -0.74f, 0.06f));   // swings with the arm
                P(holder, "LanternGlass", PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.1f, 0.08f, 0.1f), M("LanternGlow", new Color(1f, 0.6f, 0.25f), 4f), shadows: false);
                P(holder, "LanternTop", PrimitiveType.Cylinder, new Vector3(0f, 0.1f, 0f), new Vector3(0.07f, 0.02f, 0.07f), dark);
                var lantern = new GameObject("LanternLight").AddComponent<Light>();
                lantern.transform.SetParent(holder, false);
                lantern.transform.localPosition = new Vector3(-0.12f, 0f, 0.15f);   // a little away from the body: no hot spot on the sarong
                lantern.type = LightType.Point; lantern.range = 5f; lantern.intensity = 1.4f;
                lantern.color = new Color(1f, 0.65f, 0.3f); lantern.shadows = LightShadows.None;
            }
            return root;
        }

        static GameObject BuildTiger()
        {
            var root = Root("Tiger", EnemyKind.Tiger, new Vector3(0f, 0.8f, 0.05f), 0.4f, 2.1f, 2, 0.5f, 1.1f);
            var anim = root.GetComponent<CreatureAnimator>(); anim.stride = 38f; anim.cadence = 2.1f; anim.lunge = 0.8f;
            var m = Model(root);
            Material fur = FurMat("TigerFur", "Tiger_Fur"), dark = M("Black", new Color(0.04f, 0.04f, 0.05f)),
                     eye = M("EyeGlowYellow", new Color(1f, 0.85f, 0.2f), 3f), white = M("FurWhite", new Color(0.92f, 0.9f, 0.84f));

            // Heavy, low body: deep chest, long barrel, strong hips, thick neck
            S(m, "Body", "Tiger", new Sculpt()
                .Ell(V(0f, 0.82f, 0.42f), V(0.28f, 0.32f, 0.45f))
                .Cone(V(0f, 0.8f, 0.35f), V(0f, 0.78f, -0.5f), 0.28f, 0.26f, 0.14f)
                .Ell(V(0f, 0.8f, -0.58f), V(0.25f, 0.29f, 0.33f), 0.14f)
                .Cone(V(0f, 0.92f, 0.62f), V(0f, 0.96f, 0.9f), 0.22f, 0.19f, 0.12f)
                .Axis(V(0f, 0.8f, -0.95f), V(0f, 0.8f, 1f), Vector3.up), fur, 0.028f);

            var head = Pivot(m, "Head", new Vector3(0f, 0.97f, 0.92f));
            S(head, "Head", "Tiger", new Sculpt()
                .Ell(V(0f, 0.06f, 0.11f), V(0.22f, 0.2f, 0.22f))
                .Ell(V(0f, -0.02f, 0.13f), V(0.27f, 0.14f, 0.17f), 0.08f)
                .Ell(V(0f, -0.03f, 0.32f), V(0.12f, 0.1f, 0.12f), 0.07f)
                .Ell(V(-0.14f, 0.23f, 0.03f), V(0.06f, 0.065f, 0.032f), 0.03f)
                .Ell(V(0.14f, 0.23f, 0.03f), V(0.06f, 0.065f, 0.032f), 0.03f)
                .Axis(V(0f, 0f, -0.12f), V(0f, 0f, 0.45f), Vector3.up), fur, 0.014f);
            S(head, "Muzzle", "Tiger", new Sculpt().Ell(V(0f, -0.065f, 0.35f), V(0.1f, 0.07f, 0.09f)), white, 0.01f);
            S(head, "Nose", "Tiger", new Sculpt().Ell(V(0f, 0.01f, 0.44f), V(0.04f, 0.028f, 0.022f)), dark, 0.006f);
            foreach (float sx in new[] { -1f, 1f })
                P(head, "Eye", PrimitiveType.Sphere, new Vector3(sx * 0.09f, 0.09f, 0.305f), Vector3.one * 0.042f, eye, shadows: false);

            foreach (var (n, x, z, front) in new[] { ("FL", -0.16f, 0.5f, true), ("FR", 0.16f, 0.5f, true), ("BL", -0.16f, -0.6f, false), ("BR", 0.16f, -0.6f, false) })
            {
                var leg = Pivot(m, "Leg_" + n, new Vector3(x, 0.7f, z));
                var sc = front
                    ? new Sculpt().Chain(0.05f, Pt(0f, 0.05f, 0f, 0.13f), Pt(0f, -0.32f, 0.03f, 0.09f), Pt(0f, -0.6f, 0f, 0.072f))
                    : new Sculpt().Chain(0.05f, Pt(0f, 0.05f, 0f, 0.15f), Pt(0f, -0.26f, -0.07f, 0.095f), Pt(0f, -0.6f, 0f, 0.068f));
                sc.Ell(V(0f, -0.64f, 0.05f), V(0.085f, 0.05f, 0.1f), 0.05f).Axis(V(0f, 0f, 0f), V(0f, -0.7f, 0f), Vector3.forward);
                S(leg, "Leg", front ? "TigerFront" : "TigerBack", sc, fur, 0.016f);
            }
            var tail = Pivot(m, "Tail", new Vector3(0f, 0.88f, -0.88f));
            S(tail, "Tail", "Tiger", new Sculpt().Chain(0.03f, Pt(0f, 0f, 0f, 0.065f), Pt(0f, -0.15f, -0.28f, 0.055f), Pt(0f, -0.4f, -0.5f, 0.047f),
                Pt(0f, -0.55f, -0.68f, 0.043f), Pt(0f, -0.5f, -0.88f, 0.04f)).Axis(V(0f, 0f, 0f), V(0f, -0.5f, -0.88f), Vector3.up), fur, 0.014f);
            return root;
        }

        static GameObject BuildDeer()
        {
            var root = Root("Deer", EnemyKind.Deer, new Vector3(0f, 1.1f, 0f), 0.3f, 1.3f, 2, 0.4f, 1.6f);
            var anim = root.GetComponent<CreatureAnimator>(); anim.stride = 38f; anim.cadence = 2.4f;
            var m = Model(root);
            Material coat = FurMat("DeerCoat", "Deer_Coat"), dark = M("Black", new Color(0.04f, 0.04f, 0.05f)),
                     light = M("DeerLight", new Color(0.88f, 0.82f, 0.7f)), antler = M("Antler", new Color(0.62f, 0.54f, 0.42f)),
                     eye = M("EyeGlowGreen", new Color(0.5f, 1f, 0.6f), 1.5f);

            S(m, "Body", "Deer", new Sculpt()
                .Ell(V(0f, 1.0f, -0.05f), V(0.21f, 0.26f, 0.52f))
                .Ell(V(0f, 1.03f, 0.3f), V(0.2f, 0.27f, 0.26f), 0.1f)
                .Ell(V(0f, 1.0f, -0.42f), V(0.2f, 0.25f, 0.22f), 0.1f)
                .Cone(V(0f, 1.12f, 0.4f), V(0f, 1.48f, 0.6f), 0.12f, 0.08f, 0.08f)
                .Axis(V(0f, 1.05f, -0.6f), V(0f, 1.05f, 0.7f), Vector3.up), coat, 0.022f);

            var head = Pivot(m, "Head", new Vector3(0f, 1.54f, 0.64f));
            S(head, "Head", "Deer", new Sculpt()
                .Ell(V(0f, 0.03f, 0.03f), V(0.075f, 0.08f, 0.1f))
                .Cone(V(0f, 0.01f, 0.08f), V(0f, -0.04f, 0.22f), 0.055f, 0.03f, 0.04f)
                .Ell(V(-0.085f, 0.09f, -0.03f), V(0.05f, 0.022f, 0.028f), 0.02f)
                .Ell(V(0.085f, 0.09f, -0.03f), V(0.05f, 0.022f, 0.028f), 0.02f)
                .Axis(V(0f, 0f, -0.1f), V(0f, 0f, 0.25f), Vector3.up), coat, 0.009f);
            S(head, "Nose", "Deer", new Sculpt().Ell(V(0f, -0.035f, 0.232f), V(0.025f, 0.02f, 0.015f)), dark, 0.005f);
            var antlers = new Sculpt();
            foreach (float sx in new[] { -1f, 1f })
            {
                antlers.Chain(0.01f, Pt(sx * 0.035f, 0.09f, 0f, 0.014f), Pt(sx * 0.1f, 0.3f, -0.05f, 0.011f), Pt(sx * 0.13f, 0.45f, 0.02f, 0.008f));
                antlers.Chain(0.01f, Pt(sx * 0.07f, 0.2f, -0.025f, 0.01f), Pt(sx * 0.06f, 0.32f, 0.07f, 0.007f));
                P(head, "Eye", PrimitiveType.Sphere, new Vector3(sx * 0.06f, 0.05f, 0.08f), Vector3.one * 0.028f, eye, shadows: false);
            }
            S(head, "Antlers", "Deer", antlers, antler, 0.007f);

            foreach (var (n, x, z, front) in new[] { ("FL", -0.1f, 0.34f, true), ("FR", 0.1f, 0.34f, true), ("BL", -0.1f, -0.36f, false), ("BR", 0.1f, -0.36f, false) })
            {
                var leg = Pivot(m, "Leg_" + n, new Vector3(x, 0.84f, z));
                S(leg, "Leg", front ? "DeerFront" : "DeerBack", new Sculpt()
                    .Chain(0.04f, Pt(0f, 0.04f, 0f, front ? 0.075f : 0.085f), Pt(0f, -0.38f, front ? 0.02f : -0.06f, 0.045f), Pt(0f, -0.8f, 0f, 0.03f))
                    .Axis(V(0f, 0f, 0f), V(0f, -0.82f, 0f), Vector3.forward), coat, 0.011f);
                S(leg, "Hoof", "Deer", new Sculpt().Ell(V(0f, -0.82f, 0.012f), V(0.032f, 0.024f, 0.04f)), dark, 0.006f);
            }
            var tail = Pivot(m, "Tail", new Vector3(0f, 1.12f, -0.55f));
            S(tail, "Tail", "Deer", new Sculpt().Ell(V(0f, -0.03f, -0.02f), V(0.035f, 0.07f, 0.025f)), light, 0.008f);
            return root;
        }

        static GameObject BuildElephant()
        {
            var root = Root("Elephant", EnemyKind.Elephant, new Vector3(0f, 2.2f, 0.3f), 1f, 3.8f, 2, 1f, 3.2f);
            var anim = root.GetComponent<CreatureAnimator>(); anim.stride = 22f; anim.cadence = 1.2f; anim.lunge = 1f;
            var m = Model(root);
            Material skin = FurMat("ElephantSkin", "Elephant_Skin"), earSkin = FurMat("ElephantEar", "Elephant_Skin", new Color(0.82f, 0.78f, 0.78f)),
                     ivory = M("Ivory", new Color(0.9f, 0.87f, 0.78f)), dark = M("Black", new Color(0.04f, 0.04f, 0.05f));

            S(m, "Body", "Elephant", new Sculpt()
                .Ell(V(0f, 2.2f, 0f), V(0.95f, 0.92f, 1.3f))
                .Ell(V(0f, 2.35f, 0.65f), V(0.9f, 0.95f, 0.7f), 0.35f)
                .Ell(V(0f, 2.25f, -0.75f), V(0.85f, 0.88f, 0.65f), 0.35f)
                .Axis(V(0f, 2.3f, -1.5f), V(0f, 2.3f, 1.5f), Vector3.up), skin, 0.06f);

            var head = Pivot(m, "Head", new Vector3(0f, 2.65f, 1.4f));
            S(head, "Head", "Elephant", new Sculpt()
                .Ell(V(0f, 0.05f, 0.35f), V(0.68f, 0.75f, 0.6f))
                .Ell(V(0f, 0.42f, 0.42f), V(0.5f, 0.36f, 0.42f), 0.2f)
                .Cone(V(0f, -0.3f, 0.72f), V(0f, -0.55f, 0.94f), 0.4f, 0.3f, 0.25f)
                .Axis(V(0f, 0f, 0f), V(0f, 0f, 1f), Vector3.up), skin, 0.035f);
            S(head, "Ears", "Elephant", new Sculpt().Ell(V(-0.74f, -0.05f, 0.12f), V(0.07f, 0.7f, 0.58f)).Ell(V(0.74f, -0.05f, 0.12f), V(0.07f, 0.7f, 0.58f), 0f)
                .Axis(V(0f, -0.75f, 0f), V(0f, 0.65f, 0f), Vector3.forward), earSkin, 0.025f);
            var tusks = new Sculpt();
            foreach (float sx in new[] { -1f, 1f })
            {
                tusks.Chain(0.02f, Pt(sx * 0.3f, -0.48f, 0.78f, 0.08f), Pt(sx * 0.33f, -0.85f, 1.1f, 0.06f), Pt(sx * 0.3f, -0.85f, 1.4f, 0.035f));
                P(head, "Eye", PrimitiveType.Sphere, new Vector3(sx * 0.5f, 0.15f, 0.73f), Vector3.one * 0.075f, dark);
            }
            S(head, "Tusks", "Elephant", tusks, ivory, 0.02f);
            var trunk = Pivot(head, "Trunk", new Vector3(0f, -0.45f, 0.98f));
            S(trunk, "Trunk", "Elephant", new Sculpt().Chain(0.06f, Pt(0f, 0f, 0f, 0.26f), Pt(0f, -0.5f, 0.1f, 0.21f), Pt(0f, -1f, 0.12f, 0.16f),
                Pt(0f, -1.35f, 0.25f, 0.13f), Pt(0f, -1.45f, 0.42f, 0.11f)).Axis(V(0f, 0f, 0f), V(0f, -1.45f, 0.3f), Vector3.forward), skin, 0.03f);

            foreach (var (n, x, z) in new[] { ("FL", -0.55f, 0.75f), ("FR", 0.55f, 0.75f), ("BL", -0.55f, -0.75f), ("BR", 0.55f, -0.75f) })
            {
                var leg = Pivot(m, "Leg_" + n, new Vector3(x, 1.6f, z));
                S(leg, "Leg", "Elephant", new Sculpt().Cone(V(0f, 0.15f, 0f), V(0f, -1.42f, 0f), 0.33f, 0.29f)
                    .Ell(V(0f, -1.5f, 0.03f), V(0.31f, 0.12f, 0.33f), 0.1f).Axis(V(0f, 0f, 0f), V(0f, -1.6f, 0f), Vector3.forward), skin, 0.04f);
            }
            var tail = Pivot(m, "Tail", new Vector3(0f, 2.5f, -1.38f));
            S(tail, "Tail", "Elephant", new Sculpt().Chain(0.02f, Pt(0f, 0f, 0f, 0.06f), Pt(0f, -0.5f, -0.15f, 0.04f), Pt(0f, -0.85f, -0.12f, 0.03f)), skin, 0.015f);
            S(tail, "Tuft", "Elephant", new Sculpt().Ell(V(0f, -0.95f, -0.12f), V(0.05f, 0.08f, 0.05f)), dark, 0.012f);
            return root;
        }

        // ------------------------------------------------------------------ sculpted parts & materials

        static MeshBank bank;
        static readonly Dictionary<string, Mesh> built = new Dictionary<string, Mesh>();
        static readonly Dictionary<string, Texture2D> texCache = new Dictionary<string, Texture2D>();

        /// <summary>A sculpted part. Meshes are shared by key (left and right legs of the same shape, etc.).</summary>
        static GameObject S(Transform parent, string name, string owner, Sculpt sculpt, Material mat, float cell, bool shadows = true)
        {
            string key = $"{owner}_{name}";
            if (!built.TryGetValue(key, out var mesh))
            {
                mesh = bank.Add(sculpt.Build(key, cell));
                built[key] = mesh;
            }
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            if (!shadows) r.shadowCastingMode = ShadowCastingMode.Off;
            return go;
        }

        /// <summary>Fabric: a plain colour over a subtle woven texture.</summary>
        static Material Cloth(string name, Color c)
        {
            var m = M(name, c);
            if (!texCache.TryGetValue("Fabric", out var tex)) texCache["Fabric"] = tex = CreatureTextures.Get("Fabric");
            m.SetTexture("_BaseMap", tex);
            m.SetTextureScale("_BaseMap", new Vector2(3f, 3f));
            m.SetFloat("_Smoothness", 0.12f);
            return m;
        }

        /// <summary>Fur or skin with a generated texture (wraps around the part, 1 repeat per metre).</summary>
        static Material FurMat(string name, string texture, Color? tint = null)
        {
            var m = M(name, tint ?? Color.white);
            if (!texCache.TryGetValue(texture, out var tex)) texCache[texture] = tex = CreatureTextures.Get(texture);
            m.SetTexture("_BaseMap", tex);
            m.SetTextureScale("_BaseMap", Vector2.one);
            m.SetFloat("_Smoothness", 0.18f);
            return m;
        }

        // ------------------------------------------------------------------ building blocks

        static GameObject Root(string name, EnemyKind kind, Vector3 colCentre, float colRadius, float colHeight, int colDir, float agentRadius, float agentHeight)
        {
            var root = new GameObject(name);
            var col = root.AddComponent<CapsuleCollider>();
            col.center = colCentre; col.radius = colRadius; col.height = colHeight; col.direction = colDir;
            var agent = root.AddComponent<NavMeshAgent>();
            agent.radius = agentRadius; agent.height = agentHeight; agent.baseOffset = 0f;
            agent.obstacleAvoidanceType = ObstacleAvoidanceType.MedQualityObstacleAvoidance;
            root.AddComponent<Health>();
            root.AddComponent<EnemyAI>().kind = kind;
            root.AddComponent<CreatureAnimator>();
            return root;
        }

        static Transform Model(GameObject root)
        {
            var m = new GameObject("Model").transform;
            m.SetParent(root.transform, false);
            return m;
        }

        static Transform Pivot(Transform parent, string name, Vector3 pos)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = pos;
            return t;
        }

        static GameObject P(Transform parent, string name, PrimitiveType type, Vector3 pos, Vector3 scale, Material mat, Vector3 euler = default, bool shadows = true)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());   // the root capsule is the only collider
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(euler);
            go.transform.localScale = scale;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            if (!shadows) r.shadowCastingMode = ShadowCastingMode.Off;
            return go;
        }

        static Material M(string name, Color c, float emission = 0f)
        {
            if (mats.TryGetValue(name, out var m)) return m;
            string path = $"{MatFolder}/M_Char_{name}.mat";
            m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(m, path); }
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Smoothness", 0.22f);
            if (emission > 0f)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", c * emission);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            }
            EditorUtility.SetDirty(m);
            mats[name] = m;
            return m;
        }

        static GameObject Save(GameObject go)
        {
            string path = $"{PrefabFolder}/{go.name}.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            return prefab;
        }

        static Transform Group(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        static Transform Fresh(Transform parent, string name)
        {
            var old = parent.Find(name);
            if (old != null) Undo.DestroyObjectImmediate(old.gameObject);
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Kirimala gameplay");
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            int slash = path.LastIndexOf('/');
            EnsureFolder(path.Substring(0, slash));
            AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
        }
    }
}
