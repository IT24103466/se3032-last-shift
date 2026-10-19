using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.Universal;

namespace WorldBuilder.Testing
{
    /// <summary>
    /// World Builder test player: walk the level from a player's eye height to check scale,
    /// door heights, stairs, cover, sight lines and lighting. NOT the game's player (that comes
    /// from the team); it is only spawned by Tools > World Builder > Walk Test, never saved
    /// in the scene.
    ///
    /// Controls: WASD move, mouse look, Shift sprint, Space jump (vault low obstacles), C crouch,
    /// E use doors / barricades / tea bags / lockers, F torch, 1-9 and 0 teleport, H hide help,
    /// Esc free the mouse (click to capture it again).
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class FirstPersonWalker : MonoBehaviour
    {
        // Player body roughly matching a person and the NavMesh agent (radius 0.5, height 2).
        const float Height = 1.8f, CrouchHeight = 1.0f, Radius = 0.3f, EyeHeight = 1.65f, CrouchEye = 0.85f;

        public float walkSpeed = 3.5f, sprintSpeed = 6.5f, crouchSpeed = 1.8f, jumpHeight = 1.0f, gravity = -20f;
        public float lookSensitivity = 0.12f;

        CharacterController cc;
        Transform eyes;
        Light torch;
        float pitch, verticalSpeed;
        bool showHelp = true, crouched;
        Vector3 spawn;
        string message;
        float messageUntil;
        int bags;

        // Teleports for checking each area (level plan coordinates from CLAUDE.md).
        // Y = NaN: find the ground below (outdoor spots on the terrain).
        static readonly (string name, Vector3 pos, float yaw)[] Points =
        {
            ("Jungle camp (spawn)",   Vector3.zero,                       0f),   // filled in with the real spawn
            ("Stream crossing",       new Vector3(30f, float.NaN, -43f),  0f),
            ("Estate fence",          new Vector3(30f, float.NaN, -3f),   0f),
            ("Fence breach",          new Vector3(47.5f, float.NaN, 36f), 0f),
            ("Factory yard",          new Vector3(30f, 8.1f, 45f),        0f),
            ("Rolling floor",         new Vector3(23f, 8.1f, 55f),      -30f),
            ("Withering loft",        new Vector3(19f, 12.6f, 70f),     -60f),
            ("East compound",         new Vector3(60f, 8.1f, 43f),        0f),
            ("Warehouse",             new Vector3(57.5f, 8.1f, 67.5f),    0f),
            ("Main gate (escape)",    new Vector3(13f, 8.1f, 46f),      -90f),
        };

        public static FirstPersonWalker Spawn(Vector3 position, float yaw)
        {
            var go = new GameObject("WalkTest_Player");
            go.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
            var cc = go.AddComponent<CharacterController>();
            cc.height = Height;
            cc.radius = Radius;
            cc.center = new Vector3(0f, Height * 0.5f, 0f);
            cc.stepOffset = 0.4f;
            cc.slopeLimit = 45f;
            var walker = go.AddComponent<FirstPersonWalker>();
            walker.spawn = position;
            Points[0].pos = position;
            return walker;
        }

        void Awake()
        {
            cc = GetComponent<CharacterController>();

            var camGo = new GameObject("Eyes");
            camGo.transform.SetParent(transform, false);
            camGo.transform.localPosition = new Vector3(0f, EyeHeight, 0f);
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f;
            cam.fieldOfView = 70f;
            camGo.AddComponent<AudioListener>();
            var data = cam.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;   // same grade as the real game camera
            data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            eyes = camGo.transform;

            // The night watchman's torch
            torch = new GameObject("Torch").AddComponent<Light>();
            torch.transform.SetParent(eyes, false);
            torch.type = LightType.Spot;
            torch.spotAngle = 55f;
            torch.range = 18f;
            torch.intensity = 4f;
            torch.color = new Color(1f, 0.95f, 0.85f);
            torch.shadows = LightShadows.Soft;
            torch.enabled = false;

            Capture(true);
        }

        void Update()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb == null || mouse == null) return;

            if (kb.escapeKey.wasPressedThisFrame) Capture(false);
            else if (mouse.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked) Capture(true);

            // Look
            if (Cursor.lockState == CursorLockMode.Locked)
            {
                Vector2 d = mouse.delta.ReadValue() * lookSensitivity;
                transform.Rotate(0f, d.x, 0f);
                pitch = Mathf.Clamp(pitch - d.y, -85f, 85f);
                eyes.localEulerAngles = new Vector3(pitch, 0f, 0f);
            }

            // Crouch (C toggles; only stands up if there is room above)
            if (kb.cKey.wasPressedThisFrame) SetCrouch(!crouched);
            float eyeTarget = crouched ? CrouchEye : EyeHeight;
            eyes.localPosition = new Vector3(0f, Mathf.MoveTowards(eyes.localPosition.y, eyeTarget, Time.deltaTime * 4f), 0f);

            // Move
            var input = new Vector2((kb.dKey.isPressed ? 1 : 0) - (kb.aKey.isPressed ? 1 : 0),
                                    (kb.wKey.isPressed ? 1 : 0) - (kb.sKey.isPressed ? 1 : 0));
            input = Vector2.ClampMagnitude(input, 1f);
            float speed = crouched ? crouchSpeed : kb.leftShiftKey.isPressed ? sprintSpeed : walkSpeed;
            Vector3 move = (transform.right * input.x + transform.forward * input.y) * speed;

            if (cc.isGrounded)
            {
                if (verticalSpeed < 0f) verticalSpeed = -2f;  // keep pressed to the ground on slopes
                if (kb.spaceKey.wasPressedThisFrame && !crouched) verticalSpeed = Mathf.Sqrt(jumpHeight * -2f * gravity);
            }
            verticalSpeed += gravity * Time.deltaTime;
            move.y = verticalSpeed;
            cc.Move(move * Time.deltaTime);

            // Use: doors, shutters, barricades, tea bags, lockers (World Builder placeholders)
            if (kb.eKey.wasPressedThisFrame &&
                Physics.Raycast(eyes.position, eyes.forward, out var hit, 2.8f, ~0, QueryTriggerInteraction.Ignore))
            {
                var thing = hit.collider.GetComponentInParent<GreyboxInteractable>();
                if (thing != null)
                {
                    bool bag = thing.kind == GreyboxKind.Pickup;
                    string text = thing.Interact();
                    if (bag) { bags++; text += $"  ({bags}/5)"; }
                    Say(text);
                }
            }

            // Extras
            if (kb.fKey.wasPressedThisFrame) torch.enabled = !torch.enabled;
            if (kb.hKey.wasPressedThisFrame) showHelp = !showHelp;
            for (int i = 0; i < Points.Length; i++)
            {
                var key = i < 9 ? Key.Digit1 + i : Key.Digit0;
                if (kb[key].wasPressedThisFrame) { Teleport(Points[i].pos, Points[i].yaw); Say(Points[i].name); }
            }
            if (transform.position.y < -30f) Teleport(spawn, 0f); // fell out of the level
        }

        void SetCrouch(bool down)
        {
            if (!down)
            {
                // Room to stand up?
                var bottom = transform.position + Vector3.up * Radius;
                if (Physics.SphereCast(bottom, Radius * 0.9f, Vector3.up, out _, Height - Radius, ~0, QueryTriggerInteraction.Ignore))
                {
                    Say("No room to stand up");
                    return;
                }
            }
            crouched = down;
            cc.height = down ? CrouchHeight : Height;
            cc.center = new Vector3(0f, cc.height * 0.5f, 0f);
        }

        void Teleport(Vector3 pos, float yaw)
        {
            if (float.IsNaN(pos.y))
            {
                pos.y = 2f;
                if (Physics.Raycast(new Vector3(pos.x, 80f, pos.z), Vector3.down, out var ground, 200f, ~0, QueryTriggerInteraction.Ignore))
                    pos.y = ground.point.y + 0.1f;
            }
            cc.enabled = false;   // a CharacterController ignores direct position changes
            transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
            pitch = 0f;
            verticalSpeed = 0f;
            cc.enabled = true;
        }

        void Say(string text)
        {
            message = text;
            messageUntil = Time.time + 2.5f;
        }

        static void Capture(bool on)
        {
            Cursor.lockState = on ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !on;
        }

        void OnGUI()
        {
            var style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 14 };
            style.normal.textColor = Color.white;
            if (showHelp)
            {
                Vector3 p = transform.position;
                string text =
                    "WALK TEST   WASD move  Mouse look  Shift sprint  Space jump  C crouch  E use  F torch  H hide  Esc free mouse\n" +
                    "Teleport:   1 Jungle camp  2 Stream  3 Estate fence  4 Fence breach  5 Yard  6 Rolling floor  7 Loft\n" +
                    "            8 East compound  9 Warehouse  0 Main gate\n" +
                    $"Position:   X {p.x:0.0}   Y {p.y:0.0}   Z {p.z:0.0}   (level plan metres)   Tea bags: {bags}/5" + (crouched ? "   CROUCHED" : "");
                GUI.Box(new Rect(10, 10, 860, 88), text, style);
            }
            if (message != null && Time.time < messageUntil)
            {
                var m = new GUIStyle(style) { alignment = TextAnchor.MiddleCenter, fontSize = 18 };
                GUI.Box(new Rect(Screen.width * 0.5f - 260, Screen.height * 0.62f, 520, 40), message, m);
            }
            // Small crosshair so you can aim at doors
            GUI.Label(new Rect(Screen.width * 0.5f - 4, Screen.height * 0.5f - 10, 20, 20), "+");
        }
    }
}
