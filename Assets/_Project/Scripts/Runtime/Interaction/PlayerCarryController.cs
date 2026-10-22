#pragma warning disable 0649

using System;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using StarterAssets;

namespace LastShift.Runtime.Interaction
{
    /// <summary>
    /// Manages player picking up, carrying, throwing, and dropping CarryableObject physics props.
    /// Integrates seamlessly with PlayerInteraction, StarterAssetsInputs, and PlayerInput SendMessages.
    /// Includes obstruction guarding to prevent dropping objects into walls or solid geometry.
    /// Compatible with CharacterController-based movement.
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerCarryController : MonoBehaviour
    {
        [Header("Hold Configuration")]
        [Tooltip("Transform used as anchor for carried props. If null, a socket is created dynamically.")]
        [SerializeField] private Transform _holdSocket;

        [Tooltip("Default local offset for the hold socket if auto-created.")]
        [SerializeField] private Vector3 _defaultHoldOffset = new Vector3(0.25f, 0.95f, 0.85f);

        [Header("Throw & Drop Settings")]
        [Tooltip("Impulse force applied when throwing a carried object.")]
        [SerializeField] private float _throwForce = 9.5f;

        [Tooltip("Forward distance in front of player where object is placed when dropped gently.")]
        [SerializeField] private float _dropForwardOffset = 0.9f;

        [Tooltip("Upward offset when dropping an object gently.")]
        [SerializeField] private float _dropUpOffset = 0.3f;

        [Tooltip("Minimum time (seconds) between pick up, drop, or throw actions to prevent double-triggering.")]
        [SerializeField] private float _actionCooldown = 0.25f;

        [Header("Drop Obstruction Guard")]
        [Tooltip("Layers considered solid obstacles that block object drop placement.")]
        [SerializeField] private LayerMask _dropObstructionLayers = 1; // Default layer (walls/geometry)

        [Tooltip("Clearance sphere radius around the dropped object. Uses object's collider bounds if larger.")]
        [SerializeField] private float _dropCheckRadius = 0.35f;

        [Tooltip("If the default forward drop point is blocked, test candidate fallback positions (closer to player, at feet, slight lateral offsets).")]
        [SerializeField] private bool _searchNearbySafePosition = true;

        [Header("Audio Feedback")]
        [Tooltip("Audio played when a drop attempt fails because placement is blocked.")]
        [SerializeField] private AudioClip _dropObstructedAudio;
        [Range(0f, 1f)]
        [SerializeField] private float _audioVolume = 0.8f;

        [Header("Prompt Configuration")]
        [Tooltip("Contextual prompt shown while carrying an object.")]
        [SerializeField] private string _carryPrompt = "[LMB] Throw  |  [E / Q] Drop";

        [Tooltip("Temporary prompt shown when drop is obstructed.")]
        [SerializeField] private string _obstructedPrompt = "Cannot Drop Here (Blocked)  |  [LMB] Throw";

        // Current carried prop
        private CarryableObject _carriedObject;
        private Camera _mainCamera;
        private StarterAssetsInputs _starterInputs;
        private float _lastActionTime = -10f;
        private int _lastActionFrame = -1;
        private float _lastObstructedNotificationTime = -10f;
        private const float ObstructedNotificationDuration = 1.8f;

        // Events
        public event Action<CarryableObject> OnObjectPickedUp;
        public event Action<CarryableObject> OnObjectDropped;
        public event Action<CarryableObject> OnObjectThrown;
        public event Action<CarryableObject> OnDropObstructed;
        public event Action OnCarryStateChanged;

        public CarryableObject CarriedObject => _carriedObject;
        public bool IsCarrying => IsValid(_carriedObject);
        public string CarryPrompt
        {
            get
            {
                if (Time.time < _lastObstructedNotificationTime + ObstructedNotificationDuration)
                {
                    return _obstructedPrompt;
                }
                return _carryPrompt;
            }
        }
        public Transform HoldSocket => _holdSocket;

        private void Awake()
        {
            _mainCamera = Camera.main;
            _starterInputs = GetComponent<StarterAssetsInputs>();
            EnsureHoldSocket();
        }

        private void Start()
        {
            if (_mainCamera == null)
            {
                _mainCamera = Camera.main;
            }
            if (_starterInputs == null)
            {
                _starterInputs = GetComponent<StarterAssetsInputs>();
            }
        }

        private void Update()
        {
            if (_mainCamera == null)
            {
                _mainCamera = Camera.main;
            }

            // Detect if carried object was destroyed externally
            if (_carriedObject != null && !IsValid(_carriedObject))
            {
                _carriedObject = null;
                OnCarryStateChanged?.Invoke();
                return;
            }

            if (IsCarrying)
            {
                HandleCarriedInput();
            }
        }

        private void EnsureHoldSocket()
        {
            if (_holdSocket != null) return;

            // Search for an existing socket child
            var existingSocket = transform.Find("HoldSocket");
            if (existingSocket != null)
            {
                _holdSocket = existingSocket;
                return;
            }

            // Create new socket child
            var socketGo = new GameObject("HoldSocket");
            socketGo.transform.SetParent(transform, false);
            socketGo.transform.localPosition = _defaultHoldOffset;
            socketGo.transform.localRotation = Quaternion.identity;
            _holdSocket = socketGo.transform;
        }

        /// <summary>
        /// Attempts to pick up the specified carryable object.
        /// </summary>
        public bool PickUp(CarryableObject obj)
        {
            if (IsCarrying || obj == null || !obj.CanInteract)
            {
                return false;
            }

            // Prevent rapid multi-trigger in the same frame
            if (Time.frameCount == _lastActionFrame || Time.time < _lastActionTime + _actionCooldown)
            {
                return false;
            }

            EnsureHoldSocket();

            _carriedObject = obj;
            _lastActionTime = Time.time;
            _lastActionFrame = Time.frameCount;

            _carriedObject.OnPickedUp(gameObject, _holdSocket);

            OnObjectPickedUp?.Invoke(_carriedObject);
            OnCarryStateChanged?.Invoke();
            return true;
        }

        /// <summary>
        /// Drops the currently carried object gently in front of the player, verifying that the drop
        /// position is free of wall/geometry obstruction. If obstructed, attempts nearby fallback
        /// positions or retains the object in hand with a clear warning.
        /// </summary>
        public bool Drop()
        {
            if (!IsCarrying) return false;

            if (Time.frameCount == _lastActionFrame || Time.time < _lastActionTime + _actionCooldown)
            {
                return false;
            }

            var obj = _carriedObject;
            if (!IsValid(obj))
            {
                _carriedObject = null;
                OnCarryStateChanged?.Invoke();
                return false;
            }

            // Gather colliders to exclude from obstruction checks (player + carried prop)
            Collider[] objColliders = obj.GetComponentsInChildren<Collider>();
            Collider[] playerColliders = GetComponentsInChildren<Collider>();

            // Calculate clearance radius
            float radius = CalculateClearanceRadius(objColliders);

            // Determine safe placement position
            if (!TryFindSafeDropPosition(radius, objColliders, playerColliders, out Vector3 safePosition))
            {
                // Obstructed! Retain object in hands and provide clear feedback
                _lastObstructedNotificationTime = Time.time;
                _lastActionTime = Time.time;
                _lastActionFrame = Time.frameCount;

                if (_dropObstructedAudio != null)
                {
                    AudioSource.PlayClipAtPoint(_dropObstructedAudio, transform.position, _audioVolume);
                }

                Debug.LogWarning($"[PlayerCarryController] Cannot drop '{obj.name}': drop placement is obstructed by solid geometry.");
                OnDropObstructed?.Invoke(obj);
                return false;
            }

            // Safe position found: proceed with drop
            _carriedObject = null;
            _lastActionTime = Time.time;
            _lastActionFrame = Time.frameCount;

            obj.transform.position = safePosition;
            obj.OnReleased(false, Vector3.zero);

            OnObjectDropped?.Invoke(obj);
            OnCarryStateChanged?.Invoke();
            return true;
        }

        private float CalculateClearanceRadius(Collider[] objColliders)
        {
            float radius = _dropCheckRadius;
            if (objColliders != null && objColliders.Length > 0)
            {
                float maxExtent = 0f;
                for (int i = 0; i < objColliders.Length; i++)
                {
                    var col = objColliders[i];
                    if (col != null && !col.isTrigger)
                    {
                        Vector3 ext = col.bounds.extents;
                        maxExtent = Mathf.Max(maxExtent, ext.x, ext.y, ext.z);
                    }
                }
                if (maxExtent > 0.05f)
                {
                    radius = Mathf.Max(radius, maxExtent);
                }
            }
            return radius;
        }

        private bool TryFindSafeDropPosition(float radius, Collider[] objColliders, Collider[] playerColliders, out Vector3 safePosition)
        {
            Vector3 forward = transform.forward;
            Vector3 right = transform.right;
            Vector3 origin = transform.position;

            // 1. Primary candidate: default forward drop point
            Vector3 primaryCandidate = origin + forward * _dropForwardOffset + Vector3.up * _dropUpOffset;
            if (IsPositionSafeForDrop(primaryCandidate, radius, objColliders, playerColliders))
            {
                safePosition = primaryCandidate;
                return true;
            }

            // If nearby searching is disabled, fail immediately
            if (!_searchNearbySafePosition)
            {
                safePosition = Vector3.zero;
                return false;
            }

            // 2. Secondary candidate: closer to player
            Vector3 closerCandidate = origin + forward * (_dropForwardOffset * 0.6f) + Vector3.up * _dropUpOffset;
            if (IsPositionSafeForDrop(closerCandidate, radius, objColliders, playerColliders))
            {
                safePosition = closerCandidate;
                return true;
            }

            // 3. Low candidate (closer to player feet level)
            Vector3 feetCandidate = origin + forward * (_dropForwardOffset * 0.45f) + Vector3.up * 0.15f;
            if (IsPositionSafeForDrop(feetCandidate, radius, objColliders, playerColliders))
            {
                safePosition = feetCandidate;
                return true;
            }

            // 4. Slight lateral offset right
            Vector3 rightCandidate = origin + (forward * _dropForwardOffset * 0.6f + right * 0.35f) + Vector3.up * _dropUpOffset;
            if (IsPositionSafeForDrop(rightCandidate, radius, objColliders, playerColliders))
            {
                safePosition = rightCandidate;
                return true;
            }

            // 5. Slight lateral offset left
            Vector3 leftCandidate = origin + (forward * _dropForwardOffset * 0.6f - right * 0.35f) + Vector3.up * _dropUpOffset;
            if (IsPositionSafeForDrop(leftCandidate, radius, objColliders, playerColliders))
            {
                safePosition = leftCandidate;
                return true;
            }

            safePosition = Vector3.zero;
            return false;
        }

        private bool IsPositionSafeForDrop(Vector3 testPosition, float radius, Collider[] objColliders, Collider[] playerColliders)
        {
            // 1. Line-of-sight check from player chest/eyes to target position (prevent placing through thin walls)
            Vector3 playerOrigin = transform.position + Vector3.up * Mathf.Max(0.6f, _dropUpOffset);
            Vector3 toTarget = testPosition - playerOrigin;
            float distance = toTarget.magnitude;

            if (distance > 0.05f)
            {
                if (Physics.Raycast(playerOrigin, toTarget.normalized, out RaycastHit wallHit, distance, _dropObstructionLayers, QueryTriggerInteraction.Ignore))
                {
                    if (!IsExcludedCollider(wallHit.collider, objColliders, playerColliders))
                    {
                        return false;
                    }
                }
            }

            // 2. Overlap query at proposed placement position
            Collider[] overlaps = Physics.OverlapSphere(testPosition, radius, _dropObstructionLayers, QueryTriggerInteraction.Ignore);
            if (overlaps != null && overlaps.Length > 0)
            {
                for (int i = 0; i < overlaps.Length; i++)
                {
                    Collider col = overlaps[i];
                    if (col == null || col.isTrigger) continue;
                    if (!IsExcludedCollider(col, objColliders, playerColliders))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool IsExcludedCollider(Collider col, Collider[] objColliders, Collider[] playerColliders)
        {
            if (col == null) return true;

            if (objColliders != null)
            {
                for (int i = 0; i < objColliders.Length; i++)
                {
                    if (objColliders[i] == col) return true;
                }
            }

            if (playerColliders != null)
            {
                for (int i = 0; i < playerColliders.Length; i++)
                {
                    if (playerColliders[i] == col) return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Throws the currently carried object forward with configurable impulse.
        /// Guards against launching from inside a blocking wall.
        /// </summary>
        public bool Throw()
        {
            if (!IsCarrying) return false;

            if (Time.frameCount == _lastActionFrame || Time.time < _lastActionTime + _actionCooldown)
            {
                return false;
            }

            var obj = _carriedObject;
            if (!IsValid(obj))
            {
                _carriedObject = null;
                OnCarryStateChanged?.Invoke();
                return false;
            }

            _carriedObject = null;
            _lastActionTime = Time.time;
            _lastActionFrame = Time.frameCount;

            // Calculate throw direction: forward and slightly upward
            Vector3 throwDirection;
            Vector3 eyeOrigin;
            if (_mainCamera != null)
            {
                eyeOrigin = _mainCamera.transform.position;
                throwDirection = _mainCamera.transform.forward;
            }
            else
            {
                eyeOrigin = transform.position + Vector3.up * 1.2f;
                throwDirection = transform.forward;
            }

            throwDirection += Vector3.up * 0.15f;
            throwDirection.Normalize();

            Vector3 throwVelocity = throwDirection * _throwForce;

            // Guard release point against spawning inside a wall directly in front of player
            Vector3 releasePosition = transform.position + transform.forward * _dropForwardOffset + Vector3.up * 0.9f;
            if (Physics.Raycast(eyeOrigin, throwDirection, out RaycastHit wallHit, _dropForwardOffset + 0.15f, _dropObstructionLayers, QueryTriggerInteraction.Ignore))
            {
                Collider[] objCols = obj.GetComponentsInChildren<Collider>();
                Collider[] playerCols = GetComponentsInChildren<Collider>();
                if (!IsExcludedCollider(wallHit.collider, objCols, playerCols))
                {
                    releasePosition = eyeOrigin + throwDirection * Mathf.Max(0.2f, wallHit.distance - 0.15f);
                }
            }

            obj.transform.position = releasePosition;
            obj.OnReleased(true, throwVelocity);

            OnObjectThrown?.Invoke(obj);
            OnCarryStateChanged?.Invoke();
            return true;
        }

        private void HandleCarriedInput()
        {
            // Do not process throw/drop on the exact frame the object was picked up
            if (Time.frameCount == _lastActionFrame || Time.time < _lastActionTime + _actionCooldown)
            {
                return;
            }

            // 1. Check StarterAssetsInputs state
            if (_starterInputs != null)
            {
                if (_starterInputs.throwAction)
                {
                    _starterInputs.throwAction = false;
                    Throw();
                    return;
                }
                if (_starterInputs.dropAction)
                {
                    _starterInputs.dropAction = false;
                    Drop();
                    return;
                }
            }

            // 2. Direct input polling fallback
            bool throwPressed = false;
#if ENABLE_INPUT_SYSTEM
            if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
            {
                throwPressed = true;
            }
            else if (Gamepad.current != null && Gamepad.current.rightTrigger.wasPressedThisFrame)
            {
                throwPressed = true;
            }
#else
            if (Input.GetMouseButtonDown(0))
            {
                throwPressed = true;
            }
#endif
            if (throwPressed)
            {
                Throw();
                return;
            }

            bool dropPressed = false;
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && (Keyboard.current.qKey.wasPressedThisFrame || Keyboard.current.eKey.wasPressedThisFrame))
            {
                dropPressed = true;
            }
            else if (Gamepad.current != null && (Gamepad.current.buttonEast.wasPressedThisFrame || Gamepad.current.buttonWest.wasPressedThisFrame))
            {
                dropPressed = true;
            }
#else
            if (Input.GetKeyDown(KeyCode.Q) || Input.GetKeyDown(KeyCode.E))
            {
                dropPressed = true;
            }
#endif
            if (dropPressed)
            {
                Drop();
            }
        }

#if ENABLE_INPUT_SYSTEM
        /// <summary>
        /// PlayerInput SendMessages callback for Throw action.
        /// </summary>
        public void OnThrow(InputValue value)
        {
            if (value.isPressed)
            {
                if (_starterInputs != null) _starterInputs.throwAction = false;
                if (IsCarrying)
                {
                    Throw();
                }
            }
        }

        /// <summary>
        /// PlayerInput SendMessages callback for Drop action.
        /// </summary>
        public void OnDrop(InputValue value)
        {
            if (value.isPressed)
            {
                if (_starterInputs != null) _starterInputs.dropAction = false;
                if (IsCarrying)
                {
                    Drop();
                }
            }
        }
#endif

        private void OnDisable()
        {
            if (IsCarrying)
            {
                Drop();
            }
        }

        private static bool IsValid(CarryableObject obj)
        {
            if (obj == null) return false;
            if (obj is UnityEngine.Object uObj && uObj == null) return false;
            return true;
        }
    }
}
