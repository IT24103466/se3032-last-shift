using UnityEngine;

namespace LastShift.Runtime.Interaction
{
    /// <summary>
    /// Contract for all interactable objects in the world (generators, doors, switches, items).
    /// </summary>
    public interface IInteractable
    {
        /// <summary>
        /// Text prompt displayed when the player focuses on this interactable (e.g. "Restore Generator").
        /// </summary>
        string InteractionPrompt { get; }

        /// <summary>
        /// Whether the interactable can currently be interacted with.
        /// </summary>
        bool CanInteract { get; }

        /// <summary>
        /// Executes the interaction logic when triggered by the player.
        /// </summary>
        /// <param name="interactor">The GameObject initiating the interaction (typically the player).</param>
        void Interact(GameObject interactor);
    }
}
