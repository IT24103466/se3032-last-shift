#pragma warning disable 0649

using UnityEngine;
using TMPro;

namespace LastShift.Runtime.Interaction
{
    /// <summary>
    /// Displays objective progression (e.g. "Generators Restored: 1/3") on screen.
    /// Supports TextMeshPro UGUI elements and features an automatic OnGUI HUD fallback.
    /// </summary>
    [DisallowMultipleComponent]
    public class ObjectiveProgressUI : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("The EstateObjectiveManager to observe. If null, attempts to find one in the scene.")]
        [SerializeField] private EstateObjectiveManager _objectiveManager;

        [Header("UI Elements (Optional)")]
        [Tooltip("TextMeshProUGUI label to display the objective progress string.")]
        [SerializeField] private TextMeshProUGUI _progressText;

        [Tooltip("Optional panel or container GameObject toggled with UI.")]
        [SerializeField] private GameObject _container;

        [Header("Formatting")]
        [Tooltip("Format string for the active objective. {0} = current restored, {1} = required.")]
        [SerializeField] private string _progressFormat = "Generators Restored: {0}/{1}";

        [Tooltip("Message displayed when all generators are restored.")]
        [SerializeField] private string _completedMessage = "All Generators Active! Escape through Main Gate!";

        [Header("Fallback")]
        [Tooltip("If true and no TextMeshPro element is assigned, renders via OnGUI at the top of the screen.")]
        [SerializeField] private bool _useOnGuiFallback = true;

        private string _currentDisplayText = string.Empty;

        public string CurrentDisplayText => _currentDisplayText;

        private void Awake()
        {
            if (_objectiveManager == null)
            {
#if UNITY_6000_3_OR_NEWER
                _objectiveManager = FindAnyObjectByType<EstateObjectiveManager>();
#else
                _objectiveManager = FindObjectOfType<EstateObjectiveManager>();
#endif
            }
        }

        private void Start()
        {
            if (_objectiveManager != null)
            {
                UpdateDisplay(_objectiveManager.RestoredCount, _objectiveManager.RequiredCount);
            }
        }

        private void OnEnable()
        {
            if (_objectiveManager == null)
            {
#if UNITY_6000_3_OR_NEWER
                _objectiveManager = FindAnyObjectByType<EstateObjectiveManager>();
#else
                _objectiveManager = FindObjectOfType<EstateObjectiveManager>();
#endif
            }

            if (_objectiveManager != null)
            {
                _objectiveManager.OnGeneratorRestored += HandleGeneratorRestored;
                _objectiveManager.OnAllGeneratorsRestored += HandleAllGeneratorsRestored;

                UpdateDisplay(_objectiveManager.RestoredCount, _objectiveManager.RequiredCount);
            }
        }

        private void OnDisable()
        {
            if (_objectiveManager != null)
            {
                _objectiveManager.OnGeneratorRestored -= HandleGeneratorRestored;
                _objectiveManager.OnAllGeneratorsRestored -= HandleAllGeneratorsRestored;
            }
        }

        private void Update()
        {
            // Acquire objective manager if spawned dynamically
            if (_objectiveManager == null)
            {
#if UNITY_6000_3_OR_NEWER
                _objectiveManager = FindAnyObjectByType<EstateObjectiveManager>();
#else
                _objectiveManager = FindObjectOfType<EstateObjectiveManager>();
#endif
                if (_objectiveManager != null)
                {
                    _objectiveManager.OnGeneratorRestored -= HandleGeneratorRestored;
                    _objectiveManager.OnGeneratorRestored += HandleGeneratorRestored;
                    _objectiveManager.OnAllGeneratorsRestored -= HandleAllGeneratorsRestored;
                    _objectiveManager.OnAllGeneratorsRestored += HandleAllGeneratorsRestored;

                    UpdateDisplay(_objectiveManager.RestoredCount, _objectiveManager.RequiredCount);
                }
            }
        }

        private void HandleGeneratorRestored(int current, int required)
        {
            UpdateDisplay(current, required);
        }

        private void HandleAllGeneratorsRestored()
        {
            SetText(_completedMessage);
        }

        private void UpdateDisplay(int current, int required)
        {
            if (_objectiveManager != null && _objectiveManager.IsComplete)
            {
                SetText(_completedMessage);
            }
            else
            {
                SetText(string.Format(_progressFormat, current, required));
            }
        }

        private void SetText(string text)
        {
            _currentDisplayText = text;

            if (_progressText != null)
            {
                _progressText.text = text;
            }

            if (_container != null)
            {
                _container.SetActive(true);
            }
        }

        private void OnGUI()
        {
            if (_useOnGuiFallback && _progressText == null && !string.IsNullOrEmpty(_currentDisplayText))
            {
                GUI.color = Color.white;
                var style = new GUIStyle(GUI.skin.box)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 14,
                    fontStyle = FontStyle.Bold
                };

                bool complete = _objectiveManager != null && _objectiveManager.IsComplete;
                style.normal.textColor = complete ? new Color(0.2f, 1f, 0.4f) : Color.white;

                float width = 360f;
                float height = 35f;
                float x = 20f;
                float y = 20f;

                GUI.Box(new Rect(x, y, width, height), _currentDisplayText, style);
            }
        }
    }
}
