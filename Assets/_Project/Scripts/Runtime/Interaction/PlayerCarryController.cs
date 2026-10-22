#pragma warning disable 0649

using System;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace LastShift.Runtime.Interaction
{
    /// <summary>
    /// Manages player picking up, carrying, throwing, and dropping CarryableObject physics props.
    /// Integrates seamlessly with PlayerInteraction and StarterAssets / PlayerInput architecture.
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

        [Header("Prompt Configuration")]
        [Tooltip("Contextual prompt shown while carrying an object.")]
        [SerializeField] private string _carryPrompt = "[LMB] Throw  |  [E] Drop";

        // Current carried prop
        private CarryableObject _carriedObject;
        private Camera _mainCamera;
        private float _lastActionTime = -10f;
        private int _lastActionFrame = -1;

        // Events
        public event Action<CarryableObject> OnObjectPickedUp;
        public event Action<CarryableObject> OnObjectDropped;
        public event Action<CarryableObject> OnObjectThrown;
        public event Action OnCarryStateChanged;

        public CarryableObject CarriedObject => _carriedObject;
        public bool IsCarrying => IsValid(_carriedObject);
        public string CarryPrompt => _carryPrompt;
        public Transform HoldSocket => _holdSocket;

        private void Awake()
        {
            _mainCamera = Camera.main;
            EnsureHoldSocket();
        }

        private void Start()
        {
            if (_mainCamera == null)
            {
                _mainCamera = Camera.main;
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
        /// Drops the currently carried object gently in front of the player.
        /// </summary>
        public bool Drop()
        {
            if (!IsCarrying) return false;

            if (Time.frameCount == _lastActionFrame || Time.time < _lastActionTime + _actionCooldown)
            {
                return false;
            }

            var obj = _carriedObject;
            _carriedObject = null;
            _lastActionTime = Time.time;
            _lastActionFrame = Time.frameCount;

            // Calculate drop position
            Vector3 dropPosition = transform.position + transform.forward * _dropForwardOffset + Vector3.up * _dropUpOffset;
            obj.transform.position = dropPosition;

            obj.OnReleased(false, Vector3.zero);

            OnObjectDropped?.Invoke(obj);
            OnCarryStateChanged?.Invoke();
            return true;
        }

        /// <summary>
        /// Throws the currently carried object forward with configurable impulse.
        /// </summary>
        public bool Throw()
        {
            if (!IsCarrying) return false;

            if (Time.frameCount == _lastActionFrame || Time.time < _lastActionTime + _actionCooldown)
            {
                return false;
            }

            var obj = _carriedObject;
            _carriedObject = null;
            _lastActionTime = Time.time;
            _lastActionFrame = Time.frameCount;

            // Calculate throw direction: forward and slightly upward
            Vector3 throwDirection;
            if (_mainCamera != null)
            {
                throwDirection = _mainCamera.transform.forward;
            }
            else
            {
                throwDirection = transform.forward;
            }

            throwDirection += Vector3.up * 0.15f;
            throwDirection.Normalize();

            Vector3 throwVelocity = throwDirection * _throwForce;

            // Position slightly in front before launching
            Vector3 releasePosition = transform.position + transform.forward * _dropForwardOffset + Vector3.up * 0.9f;
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

            // 1. Throw input (Left Mouse Button / Gamepad Right Trigger)
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

            // 2. Drop input (E key or Q key fallback)
            bool dropPressed = false;
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && (Keyboard.current.eKey.wasPressedThisFrame || Keyboard.current.qKey.wasPressedThisFrame))
            {
                dropPressed = true;
            }
            else if (Gamepad.current != null && (Gamepad.current.buttonWest.wasPressedThisFrame || Gamepad.current.buttonEast.wasPressedThisFrame))
            {
                dropPressed = true;
            }
#else
            if (Input.GetKeyDown(KeyCode.E) || Input.GetKeyDown(KeyCode.Q))
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
        /// PlayerInput SendMessages callback for Throw action (if mapped).
        /// </summary>
        public void OnThrow(InputValue value)
        {
            if (value.isPressed && IsCarrying)
            {
                Throw();
            }
        }
#endif

        private static bool IsValid(CarryableObject obj)
        {
            if (obj == null) return false;
            if (obj is UnityEngine.Object uObj && uObj == null) return false;
            return true;
        }
    }
}
