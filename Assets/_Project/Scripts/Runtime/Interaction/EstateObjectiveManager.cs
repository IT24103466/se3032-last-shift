#pragma warning disable 0649

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace LastShift.Runtime.Interaction
{
    /// <summary>
    /// Coordinates estate generator restoration objectives and unlocks the main estate exit.
    /// Tracks unique generator activations, supports configurable generator lists,
    /// publishes progression events, and prevents duplicate completion.
    /// </summary>
    [DisallowMultipleComponent]
    public class EstateObjectiveManager : MonoBehaviour
    {
        [Header("Objective Configuration")]
        [Tooltip("The list of specific generator interactables required to escape. If empty, can be populated via inspector or script.")]
        [SerializeField] private List<GeneratorInteractable> _trackedGenerators = new List<GeneratorInteractable>();

        [Tooltip("Number of restored generators required to unlock the exit. Set to 0 to require all tracked generators.")]
        [SerializeField] private int _requiredRestoredCount = 3;

        [Header("Exit Door / Gate")]
        [Tooltip("The exit door that will be unlocked once all required generators are restored.")]
        [SerializeField] private DoorInteractable _exitDoor;

        [Tooltip("If true, ensures the exit door is locked on Start.")]
        [SerializeField] private bool _lockExitOnStart = true;

        [Header("Events")]
        [Tooltip("Event fired when a generator is restored. Parameters: (currentCount, requiredCount)")]
        [SerializeField] private UnityEvent<int, int> _onGeneratorRestored;

        [Tooltip("Event fired once when all required generators are restored and exit is unlocked.")]
        [SerializeField] private UnityEvent _onObjectiveCompleted;

        // C# Events for code subscribers
        public event Action<int, int> OnGeneratorRestored;
        public event Action OnAllGeneratorsRestored;

        private readonly HashSet<GeneratorInteractable> _restoredGenerators = new HashSet<GeneratorInteractable>();
        private bool _isCompleted = false;

        public int RestoredCount => _restoredGenerators.Count;
        public int RequiredCount => _requiredRestoredCount > 0 ? _requiredRestoredCount : (_trackedGenerators != null ? _trackedGenerators.Count : 3);
        public bool IsComplete => _isCompleted;
        public DoorInteractable ExitDoor => _exitDoor;
        public IReadOnlyCollection<GeneratorInteractable> TrackedGenerators => _trackedGenerators;

        private void Awake()
        {
            ValidateConfiguration();
        }

        private void Start()
        {
            if (_exitDoor != null && _lockExitOnStart)
            {
                _exitDoor.SetLocked(true);
            }

            // Check if any tracked generators were already active in the scene
            for (int i = 0; i < _trackedGenerators.Count; i++)
            {
                var gen = _trackedGenerators[i];
                if (gen != null && gen.IsActivated)
                {
                    HandleGeneratorRestored(gen);
                }
            }
        }

        private void OnEnable()
        {
            SubscribeGenerators();
        }

        private void OnDisable()
        {
            UnsubscribeGenerators();
        }

        private void ValidateConfiguration()
        {
            if (_trackedGenerators == null || _trackedGenerators.Count == 0)
            {
                Debug.LogWarning("[EstateObjectiveManager] No generators assigned in _trackedGenerators list. Assign G1, G2, G3 in inspector.");
            }
            else
            {
                // Remove null slots if any
                for (int i = _trackedGenerators.Count - 1; i >= 0; i--)
                {
                    if (_trackedGenerators[i] == null)
                    {
                        Debug.LogWarning($"[EstateObjectiveManager] Null generator reference found at index {i} in _trackedGenerators. Removing.");
                        _trackedGenerators.RemoveAt(i);
                    }
                }
            }

            if (_exitDoor == null)
            {
                Debug.LogWarning("[EstateObjectiveManager] No exit door assigned. Exit unlock logic will not have a target.");
            }
        }

        private void SubscribeGenerators()
        {
            if (_trackedGenerators == null) return;

            for (int i = 0; i < _trackedGenerators.Count; i++)
            {
                var gen = _trackedGenerators[i];
                if (gen != null)
                {
                    gen.OnRestored -= HandleGeneratorRestored;
                    gen.OnRestored += HandleGeneratorRestored;
                }
            }
        }

        private void UnsubscribeGenerators()
        {
            if (_trackedGenerators == null) return;

            for (int i = 0; i < _trackedGenerators.Count; i++)
            {
                var gen = _trackedGenerators[i];
                if (gen != null)
                {
                    gen.OnRestored -= HandleGeneratorRestored;
                }
            }
        }

        /// <summary>
        /// Registers a new generator to be tracked by this objective coordinator.
        /// </summary>
        public void RegisterGenerator(GeneratorInteractable generator)
        {
            if (generator == null) return;

            if (!_trackedGenerators.Contains(generator))
            {
                _trackedGenerators.Add(generator);
                generator.OnRestored -= HandleGeneratorRestored;
                generator.OnRestored += HandleGeneratorRestored;

                if (generator.IsActivated)
                {
                    HandleGeneratorRestored(generator);
                }
            }
        }

        /// <summary>
        /// Sets the exit door to unlock upon objective completion.
        /// </summary>
        public void SetExitDoor(DoorInteractable door)
        {
            _exitDoor = door;
            if (_exitDoor != null && _lockExitOnStart && !_isCompleted)
            {
                _exitDoor.SetLocked(true);
            }
        }

        /// <summary>
        /// Callback invoked when any generator is restored.
        /// Uses HashSet deduplication to guarantee each generator is counted at most once.
        /// </summary>
        public void HandleGeneratorRestored(GeneratorInteractable generator)
        {
            if (generator == null) return;

            // HashSet.Add returns false if already present
            if (!_restoredGenerators.Add(generator))
            {
                return;
            }

            int current = RestoredCount;
            int required = RequiredCount;

            Debug.Log($"[EstateObjectiveManager] Generator '{generator.name}' restored. Progress: {current}/{required}.");

            _onGeneratorRestored?.Invoke(current, required);
            OnGeneratorRestored?.Invoke(current, required);

            // Check if objective requirement met
            if (current >= required && !_isCompleted)
            {
                _isCompleted = true;
                CompleteObjective();
            }
        }

        private void CompleteObjective()
        {
            Debug.Log($"[EstateObjectiveManager] All {RequiredCount} generators restored! Unlocking estate exit...");

            if (_exitDoor != null)
            {
                _exitDoor.Unlock();
            }

            _onObjectiveCompleted?.Invoke();
            OnAllGeneratorsRestored?.Invoke();
        }

        /// <summary>
        /// Resets the objective manager state (useful for test resets).
        /// </summary>
        public void ResetObjectives()
        {
            _restoredGenerators.Clear();
            _isCompleted = false;

            if (_exitDoor != null && _lockExitOnStart)
            {
                _exitDoor.SetLocked(true);
            }

            _onGeneratorRestored?.Invoke(0, RequiredCount);
            OnGeneratorRestored?.Invoke(0, RequiredCount);
        }
    }
}
