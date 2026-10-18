using UnityEngine;

namespace WorldBuilder.Testing
{
    public enum GreyboxKind { SwingDoor, SlideDoor, Barricade, Pickup, HideSpot }

    /// <summary>
    /// World Builder placeholder for things the player interacts with: doors, shutters, gates,
    /// barricades, tea-bag pickups and hiding lockers. It only exists so the level can be
    /// walk-tested (press E in the Walk Test). The Systems Engineer replaces it with the real
    /// interaction scripts; the object names, positions and the "locked" flag are the hand-over.
    /// </summary>
    [DisallowMultipleComponent]
    public class GreyboxInteractable : MonoBehaviour
    {
        public GreyboxKind kind;
        public string label = "Door";
        public bool locked;
        [Tooltip("SlideDoor: how far the door moves when open (local space)")]
        public Vector3 openOffset = new Vector3(0f, 3f, 0f);
        [Tooltip("SwingDoor: opening angle around the hinge (Y axis)")]
        public float swingAngle = 95f;

        bool open;
        float t;
        Vector3 closedPos;
        Quaternion closedRot;

        void Awake()
        {
            closedPos = transform.localPosition;
            closedRot = transform.localRotation;
        }

        /// <summary>Called by the walk-test player (E). Returns a message to show on screen.</summary>
        public string Interact()
        {
            switch (kind)
            {
                case GreyboxKind.Pickup:
                    gameObject.SetActive(false);
                    return $"Picked up: {label}";
                case GreyboxKind.Barricade:
                    gameObject.SetActive(false);   // also removes its NavMesh carving
                    return $"{label} broken";
                case GreyboxKind.HideSpot:
                    return $"{label}: hiding spot (hiding is added by the Systems Engineer)";
                default:
                    if (locked) return $"{label}: locked";
                    open = !open;
                    return open ? $"{label} opened" : $"{label} closed";
            }
        }

        void Update()
        {
            if (kind != GreyboxKind.SwingDoor && kind != GreyboxKind.SlideDoor) return;
            t = Mathf.MoveTowards(t, open ? 1f : 0f, Time.deltaTime * 1.6f);
            float e = t * t * (3f - 2f * t);
            if (kind == GreyboxKind.SwingDoor) transform.localRotation = closedRot * Quaternion.Euler(0f, swingAngle * e, 0f);
            else transform.localPosition = closedPos + openOffset * e;
        }
    }
}
