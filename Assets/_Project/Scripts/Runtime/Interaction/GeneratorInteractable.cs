#pragma warning disable 0649

using UnityEngine;
using UnityEngine.Events;

namespace LastShift.Runtime.Interaction
{
    public enum GeneratorState
    {
        Inactive,
        Activated
    }

    /// <summary>
    /// Represents an estate generator that can be activated by the player.
    /// Implements IInteractable for integration with PlayerInteraction.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Collider))]
    public class GeneratorInteractable : MonoBehaviour, IInteractable
    {
        [Header("State")]
        [SerializeField] private GeneratorState _state = GeneratorState.Inactive;
        [SerializeField] private bool _singleUse = true;

        [Header("Interaction Prompts")]
        [SerializeField] private string _inactivePrompt = "Restore Generator";
        [SerializeField] private string _activatedPrompt = "Generator Active";

        [Header("Visual Feedback")]
        [Tooltip("Optional light that changes color/intensity upon activation.")]
        [SerializeField] private Light _indicatorLight;

        [Tooltip("Color of the indicator light when inactive.")]
        [SerializeField] private Color _inactiveLightColor = Color.red;

        [Tooltip("Color of the indicator light when activated.")]
        [SerializeField] private Color _activatedLightColor = Color.green;

        [Tooltip("Optional renderer whose material color changes on activation.")]
        [SerializeField] private Renderer _targetRenderer;

        [Tooltip("Color applied to the renderer when activated.")]
        [SerializeField] private Color _activeRendererColor = new Color(0.2f, 0.9f, 0.3f);

        [Header("Audio Feedback")]
        [Tooltip("Optional audio clip played once on activation.")]
        [SerializeField] private AudioClip _activationAudio;
        [Range(0f, 1f)]
        [SerializeField] private float _audioVolume = 0.8f;

        [Header("Events")]
        [Tooltip("Event fired when the generator is successfully activated.")]
        [SerializeField] private UnityEvent<GameObject> _onActivated;
        public UnityEvent<GameObject> OnActivated => _onActivated;
        public event System.Action<GeneratorInteractable> OnRestored;

        private MaterialPropertyBlock _propBlock;
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        public GeneratorState State => _state;
        public bool IsActivated => _state == GeneratorState.Activated;

        public string InteractionPrompt => _state == GeneratorState.Inactive ? _inactivePrompt : _activatedPrompt;

        public bool CanInteract => _state == GeneratorState.Inactive || (!_singleUse && _state == GeneratorState.Activated);

        private void Awake()
        {
            _propBlock = new MaterialPropertyBlock();
            if (_targetRenderer == null)
            {
                _targetRenderer = GetComponent<Renderer>();
            }

            UpdateVisualFeedback();
        }

        public void Interact(GameObject interactor)
        {
            if (!CanInteract)
            {
                return;
            }

            if (_state == GeneratorState.Inactive)
            {
                Activate(interactor);
            }
        }

        /// <summary>
        /// Activates the generator and triggers visual, audio, and event feedback.
        /// </summary>
        public void Activate(GameObject interactor)
        {
            if (_state == GeneratorState.Activated && _singleUse)
            {
                return;
            }

            _state = GeneratorState.Activated;
            UpdateVisualFeedback();

            if (_activationAudio != null)
            {
                AudioSource.PlayClipAtPoint(_activationAudio, transform.position, _audioVolume);
            }

            Debug.Log($"[GeneratorInteractable] Generator '{name}' restored by '{(interactor != null ? interactor.name : "Player")}'!");
            _onActivated?.Invoke(interactor);
            OnRestored?.Invoke(this);
        }

        /// <summary>
        /// Resets the generator to the Inactive state (useful for sandbox testing or round restarts).
        /// </summary>
        public void ResetGenerator()
        {
            _state = GeneratorState.Inactive;
            UpdateVisualFeedback();
        }

        private void UpdateVisualFeedback()
        {
            bool isAct = _state == GeneratorState.Activated;

            // Indicator light
            if (_indicatorLight != null)
            {
                _indicatorLight.enabled = true;
                _indicatorLight.color = isAct ? _activatedLightColor : _inactiveLightColor;
            }

            // Material property block (avoids creating material instances)
            if (_targetRenderer != null)
            {
                if (_propBlock == null)
                {
                    _propBlock = new MaterialPropertyBlock();
                }

                _targetRenderer.GetPropertyBlock(_propBlock);
                if (isAct)
                {
                    _propBlock.SetColor(BaseColorId, _activeRendererColor);
                }
                else
                {
                    _propBlock.Clear();
                }
                _targetRenderer.SetPropertyBlock(_propBlock);
            }
        }

        private void OnValidate()
        {
            // Update visual state in editor when values change
            if (_targetRenderer == null)
            {
                _targetRenderer = GetComponent<Renderer>();
            }
        }
    }
}
