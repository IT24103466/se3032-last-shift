#pragma warning disable 0649

using UnityEngine;
using TMPro;

namespace LastShift.Runtime.Interaction
{
    /// <summary>
    /// Displays a contextual interaction prompt when the player targets a valid interactable.
    /// Supports TextMeshPro, CanvasGroup / panel toggling, and an OnGUI fallback for instant testing.
    /// </summary>
    [DisallowMultipleComponent]
    public class InteractionPromptUI : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("The PlayerInteraction component to monitor. If null, attempts to find one in the scene or on the player.")]
        [SerializeField] private PlayerInteraction _playerInteraction;

        [Header("Carry Integration (Optional)")]
        [Tooltip("The PlayerCarryController to monitor for carry state. If null, attempts to find one.")]
        [SerializeField] private PlayerCarryController _carryController;

        [Header("UI Elements (Optional)")]
        [Tooltip("GameObject container for the prompt (e.g., panel or background). Toggled on/off with target availability.")]
        [SerializeField] private GameObject _promptContainer;

        [Tooltip("TextMeshPro component displaying the prompt text.")]
        [SerializeField] private TextMeshProUGUI _promptText;

        [Tooltip("CanvasGroup to fade or toggle prompt visibility.")]
        [SerializeField] private CanvasGroup _canvasGroup;

        [Header("Formatting")]
        [Tooltip("Format string for the prompt text. {0} is replaced with the interaction key, {1} with the prompt.")]
        [SerializeField] private string _promptFormat = "[{0}] {1}";

        [Tooltip("Key label displayed in the prompt.")]
        [SerializeField] private string _keyLabel = "E";

        [Header("Fallback")]
        [Tooltip("If true and no UI elements are assigned, renders an OnGUI prompt in the center of the screen.")]
        [SerializeField] private bool _useOnGuiFallback = true;

        private bool _isShowing;
        private string _currentText = string.Empty;

        public bool IsShowing => _isShowing;
        public string CurrentText => _currentText;

        private void Awake()
        {
            if (_playerInteraction == null)
            {
#if UNITY_6000_3_OR_NEWER
                _playerInteraction = FindAnyObjectByType<PlayerInteraction>();
#else
                _playerInteraction = FindObjectOfType<PlayerInteraction>();
#endif
            }

            if (_carryController == null)
            {
#if UNITY_6000_3_OR_NEWER
                _carryController = FindAnyObjectByType<PlayerCarryController>();
#else
                _carryController = FindObjectOfType<PlayerCarryController>();
#endif
            }

            HidePrompt();
        }

        private void OnEnable()
        {
            if (_playerInteraction == null)
            {
#if UNITY_6000_3_OR_NEWER
                _playerInteraction = FindAnyObjectByType<PlayerInteraction>();
#else
                _playerInteraction = FindObjectOfType<PlayerInteraction>();
#endif
            }

            if (_playerInteraction != null)
            {
                _playerInteraction.OnInteractableFound += HandleInteractableFound;
                _playerInteraction.OnInteractableLost += HandleInteractableLost;

                // Sync initial state
                if (_playerInteraction.CurrentInteractable != null && _playerInteraction.CurrentInteractable.CanInteract)
                {
                    HandleInteractableFound(_playerInteraction.CurrentInteractable);
                }
                else
                {
                    HidePrompt();
                }
            }
        }

        private void OnDisable()
        {
            if (_playerInteraction != null)
            {
                _playerInteraction.OnInteractableFound -= HandleInteractableFound;
                _playerInteraction.OnInteractableLost -= HandleInteractableLost;
            }

            HidePrompt();
        }

        private void Update()
        {
            // Acquire components if player spawned dynamically
            if (_playerInteraction == null)
            {
#if UNITY_6000_3_OR_NEWER
                _playerInteraction = FindAnyObjectByType<PlayerInteraction>();
#else
                _playerInteraction = FindObjectOfType<PlayerInteraction>();
#endif
                if (_playerInteraction != null)
                {
                    _playerInteraction.OnInteractableFound -= HandleInteractableFound;
                    _playerInteraction.OnInteractableFound += HandleInteractableFound;
                    _playerInteraction.OnInteractableLost -= HandleInteractableLost;
                    _playerInteraction.OnInteractableLost += HandleInteractableLost;
                }
            }

            if (_carryController == null)
            {
#if UNITY_6000_3_OR_NEWER
                _carryController = FindAnyObjectByType<PlayerCarryController>();
#else
                _carryController = FindObjectOfType<PlayerCarryController>();
#endif
            }

            // 1. If player is carrying an object, display carry prompt with priority
            if (_carryController != null && _carryController.IsCarrying)
            {
                string prompt = _carryController.CarryPrompt;
                if (!_isShowing || _currentText != prompt)
                {
                    ShowPrompt(prompt);
                }
                return;
            }

            // 2. Continuously verify target validity in case CanInteract changes externally
            if (_isShowing)
            {
                if (_playerInteraction == null ||
                    _playerInteraction.CurrentInteractable == null ||
                    !_playerInteraction.CurrentInteractable.CanInteract)
                {
                    HidePrompt();
                }
            }
            else
            {
                // In case an interactable became available or player interaction was acquired late
                if (_playerInteraction != null &&
                    _playerInteraction.CurrentInteractable != null &&
                    _playerInteraction.CurrentInteractable.CanInteract)
                {
                    HandleInteractableFound(_playerInteraction.CurrentInteractable);
                }
            }
        }

        private void HandleInteractableFound(IInteractable interactable)
        {
            if (interactable == null || !interactable.CanInteract)
            {
                HidePrompt();
                return;
            }

            string actionText = interactable.InteractionPrompt;
            _currentText = string.Format(_promptFormat, _keyLabel, actionText);

            ShowPrompt(_currentText);
        }

        private void HandleInteractableLost()
        {
            HidePrompt();
        }

        public void ShowPrompt(string text)
        {
            _currentText = text;
            _isShowing = true;

            if (_promptText != null)
            {
                _promptText.text = text;
            }

            if (_promptContainer != null)
            {
                _promptContainer.SetActive(true);
            }

            if (_canvasGroup != null)
            {
                _canvasGroup.alpha = 1f;
                _canvasGroup.blocksRaycasts = false;
                _canvasGroup.interactable = false;
            }
        }

        public void HidePrompt()
        {
            _isShowing = false;
            _currentText = string.Empty;

            if (_promptContainer != null)
            {
                _promptContainer.SetActive(false);
            }

            if (_canvasGroup != null)
            {
                _canvasGroup.alpha = 0f;
            }
        }

        private void OnGUI()
        {
            // Fallback rendering when no Canvas UI is hooked up yet (e.g. in test scenes)
            if (_useOnGuiFallback && _isShowing && _promptContainer == null && _promptText == null)
            {
                GUI.color = Color.white;
                var style = new GUIStyle(GUI.skin.box)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 16,
                    fontStyle = FontStyle.Bold
                };
                style.normal.textColor = Color.white;

                float width = 300f;
                float height = 40f;
                float x = (Screen.width - width) * 0.5f;
                float y = (Screen.height * 0.6f);

                GUI.Box(new Rect(x, y, width, height), _currentText, style);
            }
        }
    }
}
