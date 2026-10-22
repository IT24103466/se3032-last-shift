using UnityEngine;
using UnityEngine.Events;

namespace LastShift.Runtime.Interaction
{
    public enum DoorState
    {
        Closed,
        Opening,
        Open,
        Closing
    }

    /// <summary>
    /// Reusable openable door component supporting hinged rotation, configurable open angles,
    /// smooth transitions, lock states, and integration with IInteractable.
    /// </summary>
    [DisallowMultipleComponent]
    public class DoorInteractable : MonoBehaviour, IInteractable
    {
        [Header("State")]
        [SerializeField] private bool _isOpen = false;
        [SerializeField] private bool _isLocked = false;

        [Header("Prompts")]
        [SerializeField] private string _openPrompt = "Open Door";
        [SerializeField] private string _closePrompt = "Close Door";
        [SerializeField] private string _lockedPrompt = "Door Locked";

        [Header("Hinge & Motion Settings")]
        [Tooltip("The transform that rotates. If null, this GameObject's transform is rotated.")]
        [SerializeField] private Transform _hingeTransform;

        [Tooltip("Local euler angles when closed.")]
        [SerializeField] private Vector3 _closedEulerAngles = Vector3.zero;

        [Tooltip("Local euler angles when opened (typically 90 degrees around Y).")]
        [SerializeField] private Vector3 _openEulerAngles = new Vector3(0f, 90f, 0f);

        [Tooltip("Rotation speed in degrees per second.")]
        [SerializeField] private float _rotationSpeed = 120f;

        [Tooltip("If true, pressing interact while moving immediately reverses the door.")]
        [SerializeField] private bool _allowReverseWhileMoving = true;

        [Header("Audio Feedback")]
        [SerializeField] private AudioClip _openAudio;
        [SerializeField] private AudioClip _closeAudio;
        [SerializeField] private AudioClip _lockedAudio;
        [Range(0f, 1f)]
        [SerializeField] private float _audioVolume = 0.8f;

        [Header("Events")]
        [SerializeField] private UnityEvent<GameObject> _onOpened;
        [SerializeField] private UnityEvent<GameObject> _onClosed;
        [SerializeField] private UnityEvent<GameObject> _onLockedAttempt;

        private Quaternion _closedRotation;
        private Quaternion _openRotation;
        private Quaternion _targetRotation;

        public bool IsOpen => _isOpen;
        public bool IsLocked
        {
            get => _isLocked;
            set => _isLocked = value;
        }

        public DoorState State
        {
            get
            {
                Transform target = _hingeTransform != null ? _hingeTransform : transform;
                float angleDiff = Quaternion.Angle(target.localRotation, _targetRotation);
                if (angleDiff < 0.5f)
                {
                    return _isOpen ? DoorState.Open : DoorState.Closed;
                }
                return _isOpen ? DoorState.Opening : DoorState.Closing;
            }
        }

        public string InteractionPrompt
        {
            get
            {
                if (_isLocked) return _lockedPrompt;
                return _isOpen ? _closePrompt : _openPrompt;
            }
        }

        public bool CanInteract
        {
            get
            {
                if (_isLocked) return false;
                if (!_allowReverseWhileMoving && IsMoving()) return false;
                return true;
            }
        }

        private void Awake()
        {
            if (_hingeTransform == null)
            {
                _hingeTransform = transform;
            }

            _closedRotation = Quaternion.Euler(_closedEulerAngles);
            _openRotation = Quaternion.Euler(_openEulerAngles);

            // Initialize rotation to match initial state
            _targetRotation = _isOpen ? _openRotation : _closedRotation;
            _hingeTransform.localRotation = _targetRotation;
        }

        private void Update()
        {
            Transform target = _hingeTransform != null ? _hingeTransform : transform;
            if (Quaternion.Angle(target.localRotation, _targetRotation) > 0.05f)
            {
                target.localRotation = Quaternion.RotateTowards(
                    target.localRotation,
                    _targetRotation,
                    _rotationSpeed * Time.deltaTime
                );
            }
        }

        public void Interact(GameObject interactor)
        {
            if (_isLocked)
            {
                if (_lockedAudio != null)
                {
                    AudioSource.PlayClipAtPoint(_lockedAudio, transform.position, _audioVolume);
                }
                _onLockedAttempt?.Invoke(interactor);
                return;
            }

            if (!_allowReverseWhileMoving && IsMoving())
            {
                return;
            }

            ToggleDoor(interactor);
        }

        public void ToggleDoor(GameObject interactor = null)
        {
            if (_isOpen)
            {
                CloseDoor(interactor);
            }
            else
            {
                OpenDoor(interactor);
            }
        }

        public void OpenDoor(GameObject interactor = null)
        {
            if (_isLocked) return;

            _isOpen = true;
            _targetRotation = _openRotation;

            if (_openAudio != null)
            {
                AudioSource.PlayClipAtPoint(_openAudio, transform.position, _audioVolume);
            }

            _onOpened?.Invoke(interactor);
            Debug.Log($"[DoorInteractable] Door '{name}' opened.");
        }

        public void CloseDoor(GameObject interactor = null)
        {
            _isOpen = false;
            _targetRotation = _closedRotation;

            if (_closeAudio != null)
            {
                AudioSource.PlayClipAtPoint(_closeAudio, transform.position, _audioVolume);
            }

            _onClosed?.Invoke(interactor);
            Debug.Log($"[DoorInteractable] Door '{name}' closed.");
        }

        public void SetLocked(bool locked)
        {
            _isLocked = locked;
        }

        public bool IsMoving()
        {
            Transform target = _hingeTransform != null ? _hingeTransform : transform;
            return Quaternion.Angle(target.localRotation, _targetRotation) > 0.5f;
        }
    }
}
