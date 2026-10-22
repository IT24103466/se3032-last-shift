#pragma warning disable 0649

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Events;

namespace LastShift.Runtime.Interaction
{
    /// <summary>
    /// Reusable physics prop that can be picked up, carried, and thrown or dropped by the player.
    /// Implements IInteractable for seamless integration with PlayerInteraction and InteractionPromptUI.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(Collider))]
    public class CarryableObject : MonoBehaviour, IInteractable
    {
        [Header("Interaction Configuration")]
        [SerializeField] private string _pickupPrompt = "Pick Up";
        [SerializeField] private bool _isInteractable = true;

        [Header("Physics Settings")]
        [Tooltip("Mass of the object when active in the physics world.")]
        [SerializeField] private float _mass = 12.0f;

        [Tooltip("Position offset relative to the hold socket when carried.")]
        [SerializeField] private Vector3 _holdPositionOffset = Vector3.zero;

        [Tooltip("Local rotation relative to the hold socket when carried.")]
        [SerializeField] private Vector3 _holdRotationEuler = Vector3.zero;

        [Header("Audio Feedback (Optional)")]
        [SerializeField] private AudioClip _pickupSound;
        [SerializeField] private AudioClip _dropSound;
        [SerializeField] private AudioClip _throwSound;
        [SerializeField] private AudioClip _impactSound;
        [Range(0f, 1f)]
        [SerializeField] private float _soundVolume = 0.7f;

        [Header("Events")]
        [SerializeField] private UnityEvent<GameObject> _onPickedUp;
        [SerializeField] private UnityEvent<GameObject> _onDropped;
        [SerializeField] private UnityEvent<GameObject> _onThrown;

        private Rigidbody _rb;
        private Collider[] _colliders;
        private bool _isCarried;
        private GameObject _carrier;
        private Collider[] _carrierColliders;

        // Cached physics properties before pickup
        private bool _cachedKinematic;
        private bool _cachedUseGravity;
        private RigidbodyInterpolation _cachedInterpolation;
        private CollisionDetectionMode _cachedCollisionDetection;

        public Rigidbody AttachedRigidbody => _rb;
        public bool IsCarried => _isCarried;
        public GameObject Carrier => _carrier;
        public Vector3 HoldPositionOffset => _holdPositionOffset;
        public Quaternion HoldRotation => Quaternion.Euler(_holdRotationEuler);

        public string InteractionPrompt => _pickupPrompt;
        public bool CanInteract => _isInteractable && !_isCarried;

        private void Awake()
        {
            if (_rb == null) _rb = GetComponent<Rigidbody>();
            if (_colliders == null || _colliders.Length == 0) _colliders = GetComponentsInChildren<Collider>();

            if (_rb != null && _rb.mass <= 1f)
            {
                _rb.mass = _mass;
            }
        }

        private void OnValidate()
        {
            if (_rb == null)
            {
                _rb = GetComponent<Rigidbody>();
            }

            if (_rb != null && _rb.mass <= 1f)
            {
                _rb.mass = _mass;
            }
        }

        public void Interact(GameObject interactor)
        {
            if (!CanInteract || interactor == null) return;

            if (interactor.TryGetComponent<PlayerCarryController>(out var carryController))
            {
                carryController.PickUp(this);
            }
        }

        /// <summary>
        /// Called by PlayerCarryController when the object is picked up.
        /// </summary>
        public void OnPickedUp(GameObject carrier, Transform holdSocket)
        {
            if (_isCarried) return;
            if (_rb == null) _rb = GetComponent<Rigidbody>();
            if (_rb == null) return;
            if (_colliders == null || _colliders.Length == 0) _colliders = GetComponentsInChildren<Collider>();

            _isCarried = true;
            _carrier = carrier;

            // Cache original Rigidbody settings
            _cachedKinematic = _rb.isKinematic;
            _cachedUseGravity = _rb.useGravity;
            _cachedInterpolation = _rb.interpolation;
            _cachedCollisionDetection = _rb.collisionDetectionMode;

            // Disable physics simulation while carried
            _rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            _rb.isKinematic = true;
            _rb.useGravity = false;
#if UNITY_6000_0_OR_NEWER
            _rb.linearVelocity = Vector3.zero;
#else
            _rb.velocity = Vector3.zero;
#endif
            _rb.angularVelocity = Vector3.zero;

            // Ignore collisions between this object and the carrier (CharacterController & colliders)
            if (_carrier != null)
            {
                _carrierColliders = _carrier.GetComponentsInChildren<Collider>();
                SetCollisionsWithCarrier(true);
            }

            // Parent to hold socket
            if (holdSocket != null)
            {
                transform.SetParent(holdSocket);
                transform.localPosition = _holdPositionOffset;
                transform.localRotation = Quaternion.Euler(_holdRotationEuler);
            }

            if (_pickupSound != null)
            {
                AudioSource.PlayClipAtPoint(_pickupSound, transform.position, _soundVolume);
            }

            _onPickedUp?.Invoke(_carrier);
            Debug.Log($"[CarryableObject] '{name}' picked up by '{carrier.name}'.");
        }

        /// <summary>
        /// Called by PlayerCarryController when released (dropped or thrown).
        /// </summary>
        public void OnReleased(bool wasThrown, Vector3 throwVelocity)
        {
            if (!_isCarried) return;
            if (_rb == null) _rb = GetComponent<Rigidbody>();
            if (_rb == null) return;

            transform.SetParent(null, true);

            // Restore Rigidbody
            _rb.isKinematic = _cachedKinematic;
            _rb.useGravity = _cachedUseGravity;
            _rb.interpolation = _cachedInterpolation;
            _rb.collisionDetectionMode = _cachedCollisionDetection;

            if (wasThrown)
            {
#if UNITY_6000_0_OR_NEWER
                _rb.linearVelocity = throwVelocity;
#else
                _rb.velocity = throwVelocity;
#endif
                _rb.AddTorque(UnityEngine.Random.insideUnitSphere * 2.5f, ForceMode.Impulse);

                if (_throwSound != null)
                {
                    AudioSource.PlayClipAtPoint(_throwSound, transform.position, _soundVolume);
                }

                _onThrown?.Invoke(_carrier);
                Debug.Log($"[CarryableObject] '{name}' thrown with velocity {throwVelocity.magnitude:F1} m/s.");
            }
            else
            {
#if UNITY_6000_0_OR_NEWER
                _rb.linearVelocity = Vector3.zero;
#else
                _rb.velocity = Vector3.zero;
#endif
                _rb.angularVelocity = Vector3.zero;

                if (_dropSound != null)
                {
                    AudioSource.PlayClipAtPoint(_dropSound, transform.position, _soundVolume);
                }

                _onDropped?.Invoke(_carrier);
                Debug.Log($"[CarryableObject] '{name}' dropped.");
            }

            // Re-enable collisions with player after a safe delay
            if (gameObject.activeInHierarchy)
            {
                StartCoroutine(RestoreCarrierCollisionsRoutine(0.35f));
            }
            else
            {
                SetCollisionsWithCarrier(false);
                _carrierColliders = null;
            }

            _isCarried = false;
            _carrier = null;
        }

        private void SetCollisionsWithCarrier(bool ignore)
        {
            if (_colliders == null || _colliders.Length == 0)
            {
                _colliders = GetComponentsInChildren<Collider>();
            }

            if (_colliders == null || _carrierColliders == null) return;

            for (int i = 0; i < _colliders.Length; i++)
            {
                var myCol = _colliders[i];
                if (myCol == null) continue;

                for (int j = 0; j < _carrierColliders.Length; j++)
                {
                    var carrierCol = _carrierColliders[j];
                    if (carrierCol != null && carrierCol != myCol)
                    {
                        Physics.IgnoreCollision(myCol, carrierCol, ignore);
                    }
                }
            }
        }

        private IEnumerator RestoreCarrierCollisionsRoutine(float delay)
        {
            yield return new WaitForSeconds(delay);
            if (this != null)
            {
                SetCollisionsWithCarrier(false);
                _carrierColliders = null;
            }
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (_impactSound != null && collision.relativeVelocity.magnitude > 2.0f)
            {
                AudioSource.PlayClipAtPoint(_impactSound, collision.contacts[0].point, _soundVolume);
            }
        }

        private void OnDisable()
        {
            SetCollisionsWithCarrier(false);
        }
    }
}
