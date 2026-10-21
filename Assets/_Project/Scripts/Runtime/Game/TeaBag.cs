using UnityEngine;

namespace LastShift
{
    /// <summary>A sack of tea leaves the player can search (hold E). One of them hides the wedding ring.</summary>
    public class TeaBag : MonoBehaviour
    {
        public bool hasRing;
        public bool searched;
        public string place = "factory";

        public void Search()
        {
            if (searched) return;
            searched = true;
            // Opened sack: slumps and spills
            var sack = transform.Find("Sack");
            if (sack != null)
            {
                sack.localScale = Vector3.Scale(sack.localScale, new Vector3(1.25f, 0.55f, 1.25f));
                sack.localPosition += Vector3.down * 0.15f;
            }
            var tag = transform.Find("Tag");
            if (tag != null) tag.gameObject.SetActive(false);
            GameManager.Instance?.OnBagSearched(this);
        }
    }
}
