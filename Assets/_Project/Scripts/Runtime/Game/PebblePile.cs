using UnityEngine;

namespace LastShift
{
    /// <summary>A heap of stones: press E to refill the slingshot.</summary>
    public class PebblePile : MonoBehaviour
    {
        public int amount = 8;
        void Start() => PickupTargeting.Fit(this);

        public void Take(PlayerSlingshot sling)
        {
            sling.AddAmmo(amount);
            GameManager.Instance?.Toast($"Picked up {amount} pebbles", 1.5f);
            gameObject.SetActive(false);
        }
    }
}
