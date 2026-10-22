using System;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using StarterAssets;

namespace LastShift.Runtime.Interaction
{
    /// <summary>
    /// Detects interactable objects in front of the player and handles interaction triggers.
    /// Works with StarterAssetsInputs, PlayerInput SendMessages, and direct input fallbacks.
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerInteraction : MonoBehaviour
    {
        [Header("Detection Settings")]
        [Tooltip("Maximum distance from the origin to detect interactable objects.")]
        [SerializeField] private float _interactionRange = 2.5f;

        [Tooltip("Radius of the detection spherecast. A value around 0.25m provides forgiving aiming.")]
        [SerializeField] private float _detectionRadius = 0.25f;

        [Tooltip("Vertical offset from the player origin when casting without a dedicated raycastOrigin.")]
        [SerializeField] private float _heightOffset = 1.2f;

        [Tooltip("If assigned, raycasts originate from this transform (e.g. camera or eyes). If null, uses player position + height offset.")]
        [SerializeField] private Transform _raycastOrigin;

        [Tooltip("If true, detection casts along the Main Camera's forward direction. If false, casts along character forward.")]
        [SerializeField] private bool _useCameraDirection = true;

        [Tooltip("Layers to consider for interactable objects. Default is Everything.")]
        [SerializeField] private LayerMask _interactableLayers = ~0;

        [Tooltip("Layers that block line of sight to interactables (e.g., walls, terrain, static geometry).")]
        [SerializeField] private LayerMask _obstructionLayers = 1; // Default layer (walls/geometry)

        [Header("Debug")]
        [SerializeField] private bool _drawDebugGizmos = true;

        // Current focused interactable
        public IInteractable CurrentInteractable { get; private set; }

        // Events for UI or external systems
        public event Action<IInteractable> OnInteractableFound;
        public event Action OnInteractableLost;
        public event Action<IInteractable> OnInteracted;

        private StarterAssetsInputs _starterInputs;
        private Camera _mainCamera;
        private int _lastInteractedFrame = -1;
        private RaycastHit _lastHitInfo;

        public float InteractionRange => _interactionRange;
        public float DetectionRadius => _detectionRadius;

        private void Awake()
        {
            _starterInputs = GetComponent<StarterAssetsInputs>();
            _mainCamera = Camera.main;
        }

        private void Update()
        {
            if (_mainCamera == null)
            {
                _mainCamera = Camera.main;
            }

            PerformDetection();
            CheckInput();
        }

        /// <summary>
        /// Detects the nearest valid IInteractable in front of the player, rejecting any obstructed by walls.
        /// </summary>
        private void PerformDetection()
        {
            Vector3 origin = GetCastOrigin();
            Vector3 direction = GetCastDirection();

            IInteractable candidate = null;
            bool hitSomething = false;

            // 1. Try a direct raycast first (precise aiming)
            if (Physics.Raycast(origin, direction, out RaycastHit rayHit, _interactionRange, _interactableLayers, QueryTriggerInteraction.Collide))
            {
                candidate = rayHit.collider.GetComponentInParent<IInteractable>();
                if (candidate != null)
                {
                    hitSomething = true;
                    _lastHitInfo = rayHit;
                }
            }

            // 2. If direct ray didn't hit an interactable, try a SphereCast for forgiving detection
            if (candidate == null && _detectionRadius > 0f)
            {
                if (Physics.SphereCast(origin, _detectionRadius, direction, out RaycastHit sphereHit, _interactionRange, _interactableLayers, QueryTriggerInteraction.Collide))
                {
                    candidate = sphereHit.collider.GetComponentInParent<IInteractable>();
                    if (candidate != null)
                    {
                        hitSomething = true;
                        _lastHitInfo = sphereHit;
                    }
                }
            }

            // 3. Line-of-sight obstruction check: prevent interacting through walls
            if (candidate != null && hitSomething)
            {
                if (IsObstructedByWall(origin, _lastHitInfo))
                {
                    candidate = null;
                }
            }

            // 4. Reject candidates that cannot currently be interacted with
            if (candidate != null && !candidate.CanInteract)
            {
                candidate = null;
            }

            // 5. Update state and notify listeners
            if (candidate != CurrentInteractable)
            {
                if (CurrentInteractable != null)
                {
                    OnInteractableLost?.Invoke();
                }

                CurrentInteractable = candidate;

                if (CurrentInteractable != null && CurrentInteractable.CanInteract)
                {
                    OnInteractableFound?.Invoke(CurrentInteractable);
                }
            }
        }

        /// <summary>
        /// Checks if a solid geometry collider is between the cast origin and the interactable.
        /// </summary>
        private bool IsObstructedByWall(Vector3 origin, RaycastHit targetHit)
        {
            Vector3 targetPoint = targetHit.point;
            
            // If the hit point is at origin (e.g. initial overlap), use target collider center
            if (targetPoint == Vector3.zero || Vector3.Distance(origin, targetPoint) < 0.05f)
            {
                targetPoint = targetHit.collider.bounds.center;
            }

            Vector3 directionToTarget = targetPoint - origin;
            float distance = directionToTarget.magnitude;

            if (distance <= 0.1f)
            {
                return false;
            }

            // Raycast towards target on obstruction layers
            if (Physics.Raycast(origin, directionToTarget.normalized, out RaycastHit wallHit, distance - 0.05f, _obstructionLayers, QueryTriggerInteraction.Ignore))
            {
                // If the obstruction hit is NOT the interactable object itself, it's a blocking wall
                if (wallHit.collider != null && wallHit.collider != targetHit.collider)
                {
                    IInteractable hitInteractable = wallHit.collider.GetComponentInParent<IInteractable>();
                    if (hitInteractable != CurrentInteractable && hitInteractable != targetHit.collider.GetComponentInParent<IInteractable>())
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private Vector3 GetCastOrigin()
        {
            if (_raycastOrigin != null)
            {
                return _raycastOrigin.position;
            }

            return transform.position + Vector3.up * _heightOffset;
        }

        private Vector3 GetCastDirection()
        {
            if (_useCameraDirection && _mainCamera != null)
            {
                return _mainCamera.transform.forward;
            }

            return transform.forward;
        }

        private void CheckInput()
        {
            // 1. Check StarterAssetsInputs state
            if (_starterInputs != null && _starterInputs.interact)
            {
                _starterInputs.interact = false;
                TryInteract();
                return;
            }

            // 2. Direct input fallback (works in standalone scenes or without PlayerInput)
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && Keyboard.current.eKey.wasPressedThisFrame)
            {
                TryInteract();
            }
#else
            if (Input.GetKeyDown(KeyCode.E))
            {
                TryInteract();
            }
#endif
        }

#if ENABLE_INPUT_SYSTEM
        /// <summary>
        /// Callback invoked by PlayerInput when SendMessages notification behavior is active.
        /// </summary>
        public void OnInteract(InputValue value)
        {
            if (value.isPressed)
            {
                TryInteract();
            }
        }
#endif

        /// <summary>
        /// Executes interaction on the currently focused interactable if one is available and eligible.
        /// </summary>
        public void TryInteract()
        {
            // Prevent multiple triggers in a single frame across multiple input paths
            if (Time.frameCount == _lastInteractedFrame)
            {
                return;
            }
            _lastInteractedFrame = Time.frameCount;

            if (CurrentInteractable != null && CurrentInteractable.CanInteract)
            {
                IInteractable interactable = CurrentInteractable;
                interactable.Interact(gameObject);
                OnInteracted?.Invoke(interactable);

                // If target became unavailable from the interaction (e.g. single-use), clear immediately
                if (!interactable.CanInteract && CurrentInteractable == interactable)
                {
                    CurrentInteractable = null;
                    OnInteractableLost?.Invoke();
                }
            }
        }

        private void OnDrawGizmosSelected()
        {
            if (!_drawDebugGizmos) return;

            Vector3 origin = GetCastOrigin();
            Vector3 direction = GetCastDirection();

            Gizmos.color = CurrentInteractable != null ? Color.green : Color.yellow;
            Gizmos.DrawRay(origin, direction * _interactionRange);

            if (_detectionRadius > 0f)
            {
                Gizmos.DrawWireSphere(origin + direction * _interactionRange, _detectionRadius);
            }
        }
    }
}
