#pragma warning disable 0649

using UnityEngine;
using UnityEngine.Events;

namespace LastShift.Runtime.Interaction
{
    /// <summary>
    /// Represents a physically movable barricade or heavy crate that can be pushed by the player
    /// via physical contact (CharacterController collision) or direct interaction (IInteractable [E] shove).
    /// Prevents excessive acceleration, tunneling through walls, and uncontrolled spinning.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(Collider))]
    public class MovableBarricade : MonoBehaviour, IInteractable
    {
        [Header("Interaction Configuration")]
        [SerializeField] private string _pushPrompt = "Push Barricade";
        [SerializeField] private bool _isInteractable = true;
        [SerializeField] private float _interactPushForce = 3.5f;
        [SerializeField] private float _interactionCooldown = 0.5f;

        [Header("Physical Push Settings")]
        [Tooltip("Force applied per fixed frame when the player walks into the barricade.")]
        [SerializeField] private float _contactPushForce = 15.0f;

        [Tooltip("Maximum horizontal speed (m/s) the barricade can reach when pushed.")]
        [SerializeField] private float _maxPushSpeed = 2.0f;

        [Tooltip("Extra mass to set on the Rigidbody if not configured in the inspector.")]
        [SerializeField] private float _targetMass = 75.0f;

        [Header("Audio / Feedback")]
        [SerializeField] private AudioClip _pushSound;
        [Range(0f, 1f)]
        [SerializeField] private float _soundVolume = 0.7f;

        [Header("Events")]
        [SerializeField] private UnityEvent<GameObject> _onPushed;

        private Rigidbody _rb;
        private float _lastInteractTime = -10f;
        private Vector3 _pendingContactPush = Vector3.zero;

        public Rigidbody AttachedRigidbody => _rb;
        public bool IsInteractable
        {
            get => _isInteractable;
            set => _isInteractable = value;
        }

        public string InteractionPrompt => _pushPrompt;
        public bool CanInteract => _isInteractable && (Time.time >= _lastInteractTime + _interactionCooldown);

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            ConfigureRigidbody();
        }

        private void OnValidate()
        {
            if (_rb == null)
            {
                _rb = GetComponent<Rigidbody>();
            }

            if (_rb != null)
            {
                ConfigureRigidbody();
            }
        }

        private void ConfigureRigidbody()
        {
            if (_rb == null) return;

            // Set sensible mass
            if (_rb.mass <= 1f)
            {
                _rb.mass = _targetMass;
            }

            // Prevent physics tunneling through walls
            _rb.collisionDetectionMode = CollisionDetectionMode.Continuous;

            // Smooth interpolation
            _rb.interpolation = RigidbodyInterpolation.Interpolate;

            // High linear drag so barricade comes to a clean halt when not being pushed
#if UNITY_6000_0_OR_NEWER
            if (_rb.linearDamping < 2f)
            {
                _rb.linearDamping = 3.5f;
            }

            if (_rb.angularDamping < 4f)
            {
                _rb.angularDamping = 5.0f;
            }
#else
            if (_rb.drag < 2f)
            {
                _rb.drag = 3.5f;
            }

            if (_rb.angularDrag < 4f)
            {
                _rb.angularDrag = 5.0f;
            }
#endif

            // Freeze X and Z rotation so the barricade stays strictly upright
            _rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
        }

        private void FixedUpdate()
        {
            // Apply pending contact push from player collisions
            if (_pendingContactPush != Vector3.zero)
            {
                _rb.AddForce(_pendingContactPush, ForceMode.Force);
                _pendingContactPush = Vector3.zero;
            }

            // Clamp horizontal velocity to prevent runaway physics acceleration
#if UNITY_6000_0_OR_NEWER
            Vector3 vel = _rb.linearVelocity;
#else
            Vector3 vel = _rb.velocity;
#endif
            Vector3 horizontalVel = new Vector3(vel.x, 0f, vel.z);
            if (horizontalVel.magnitude > _maxPushSpeed)
            {
                Vector3 clampedHorizontal = horizontalVel.normalized * _maxPushSpeed;
#if UNITY_6000_0_OR_NEWER
                _rb.linearVelocity = new Vector3(clampedHorizontal.x, vel.y, clampedHorizontal.z);
#else
                _rb.velocity = new Vector3(clampedHorizontal.x, vel.y, clampedHorizontal.z);
#endif
            }
        }

        private void OnCollisionStay(Collision collision)
        {
            // Detect if colliding with player (via tag or CharacterController)
            if (collision.collider.CompareTag("Player") || collision.collider.GetComponent<CharacterController>() != null)
            {
                // Calculate push direction away from the player
                Vector3 playerPos = collision.transform.position;
                Vector3 pushDir = transform.position - playerPos;
                pushDir.y = 0f;

                if (pushDir.sqrMagnitude > 0.001f)
                {
                    pushDir.Normalize();
                    _pendingContactPush = pushDir * _contactPushForce;
                }
            }
        }

        public void Interact(GameObject interactor)
        {
            if (!CanInteract) return;

            _lastInteractTime = Time.time;

            // Calculate forward push direction based on interactor's facing or relative position
            Vector3 pushDirection;
            if (interactor != null)
            {
                pushDirection = interactor.transform.forward;
            }
            else
            {
                pushDirection = -transform.forward;
            }
            pushDirection.y = 0f;
            pushDirection.Normalize();

            // Apply deliberate shove impulse
            _rb.AddForce(pushDirection * _interactPushForce, ForceMode.VelocityChange);

            if (_pushSound != null)
            {
                AudioSource.PlayClipAtPoint(_pushSound, transform.position, _soundVolume);
            }

            _onPushed?.Invoke(interactor);
            Debug.Log($"[MovableBarricade] '{name}' pushed by '{(interactor != null ? interactor.name : "Player")}'.");
        }
    }
}
