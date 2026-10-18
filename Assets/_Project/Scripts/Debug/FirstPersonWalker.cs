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
    /// Controls: WASD move, mouse look, Shift sprint, Space jump, F torch, 1-6 teleport to
    /// key areas, H hide help, Esc free the mouse (click to capture it again).
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class FirstPersonWalker : MonoBehaviour
    {
        // Player body roughly matching a person and the NavMesh agent (radius 0.5, height 2).
        const float Height = 1.8f, Radius = 0.3f, EyeHeight = 1.65f;

        public float walkSpeed = 3.5f, sprintSpeed = 6.5f, jumpHeight = 1.0f, gravity = -20f;
        public float lookSensitivity = 0.12f;

        CharacterController cc;
        Transform eyes;
        Light torch;
        float pitch, verticalSpeed;
        bool showHelp = true;
        Vector3 spawn;

        // Teleports for checking each area (level plan coordinates from CLAUDE.md)
        static readonly (string name, Vector3 pos, float yaw)[] Points =
        {
            ("Spawn (estate)",        Vector3.zero,                  0f),   // filled in with the real spawn
            ("Yard",                  new Vector3(30f, 8.1f, 45f),   0f),
            ("Rolling floor (G1)",    new Vector3(23f, 8.1f, 55f),  -30f),
            ("Withering loft (G2)",   new Vector3(19f, 12.6f, 70f),  -60f),
            ("Boiler room (G3)",      new Vector3(41f, 8.1f, 66f),   45f),
            ("Main gate",             new Vector3(13f, 8.1f, 46f),  -90f),
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

            // Move
            var input = new Vector2((kb.dKey.isPressed ? 1 : 0) - (kb.aKey.isPressed ? 1 : 0),
                                    (kb.wKey.isPressed ? 1 : 0) - (kb.sKey.isPressed ? 1 : 0));
            input = Vector2.ClampMagnitude(input, 1f);
            float speed = kb.leftShiftKey.isPressed ? sprintSpeed : walkSpeed;
            Vector3 move = (transform.right * input.x + transform.forward * input.y) * speed;

            if (cc.isGrounded)
            {
                if (verticalSpeed < 0f) verticalSpeed = -2f;  // keep pressed to the ground on slopes
                if (kb.spaceKey.wasPressedThisFrame) verticalSpeed = Mathf.Sqrt(jumpHeight * -2f * gravity);
            }
            verticalSpeed += gravity * Time.deltaTime;
            move.y = verticalSpeed;
            cc.Move(move * Time.deltaTime);

            // Extras
            if (kb.fKey.wasPressedThisFrame) torch.enabled = !torch.enabled;
            if (kb.hKey.wasPressedThisFrame) showHelp = !showHelp;
            for (int i = 0; i < Points.Length; i++)
                if (kb[Key.Digit1 + i].wasPressedThisFrame) Teleport(Points[i].pos, Points[i].yaw);
            if (transform.position.y < -20f) Teleport(spawn, 0f); // fell out of the level
        }

        void Teleport(Vector3 pos, float yaw)
        {
            cc.enabled = false;   // a CharacterController ignores direct position changes
            transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
            pitch = 0f;
            verticalSpeed = 0f;
            cc.enabled = true;
        }

        static void Capture(bool on)
        {
            Cursor.lockState = on ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !on;
        }

        void OnGUI()
        {
            if (!showHelp) return;
            Vector3 p = transform.position;
            string text =
                "WALK TEST   WASD move  Mouse look  Shift sprint  Space jump  F torch  H hide  Esc free mouse\n" +
                "Teleport:   1 Spawn  2 Yard  3 Rolling floor (G1)  4 Loft (G2)  5 Boiler room (G3)  6 Main gate\n" +
                $"Position:   X {p.x:0.0}   Y {p.y:0.0}   Z {p.z:0.0}   (level plan metres)";
            var style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 14 };
            style.normal.textColor = Color.white;
            GUI.Box(new Rect(10, 10, 760, 70), text, style);
        }
    }
}
