using UnityEngine;
using UnityEngine.Events;

namespace LastShift.Runtime.Interaction
{
    /// <summary>
    /// General-purpose interactable component for testing, props, and simple world interactions.
    /// Implements IInteractable.
    /// </summary>
    [DisallowMultipleComponent]
    public class SimpleInteractable : MonoBehaviour, IInteractable
    {
        [Header("Interaction Configuration")]
        [Tooltip("Prompt displayed to the player when looking at this object.")]
        [SerializeField] private string _interactionPrompt = "Interact";

        [Tooltip("Whether the object is currently ready to accept interactions.")]
        [SerializeField] private bool _canInteract = true;

        [Tooltip("If true, the object can only be interacted with once.")]
        [SerializeField] private bool _singleUse = false;

        [Header("Events")]
        [SerializeField] private UnityEvent<GameObject> _onInteract;

        private int _interactionCount = 0;

        public string InteractionPrompt => _interactionPrompt;
        public bool CanInteract => _canInteract;
        public int InteractionCount => _interactionCount;

        public void SetInteractable(bool canInteract)
        {
            _canInteract = canInteract;
        }

        public void SetPrompt(string prompt)
        {
            _interactionPrompt = prompt;
        }

        public virtual void Interact(GameObject interactor)
        {
            if (!_canInteract) return;

            _interactionCount++;
            Debug.Log($"[SimpleInteractable] '{name}' interacted with by '{(interactor != null ? interactor.name : "Unknown")}' (Count: {_interactionCount}).");

            _onInteract?.Invoke(interactor);

            if (_singleUse)
            {
                _canInteract = false;
            }
        }
    }
}
