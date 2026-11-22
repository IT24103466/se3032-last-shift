using UnityEngine;

namespace LastShift
{
    /// <summary>
    /// A box of ammunition: look at it and press E to take the rounds and shells.
    /// The level has crates along the route; knocked-out guards, workers and the hunter drop a small box.
    /// </summary>
    public class AmmoBox : MonoBehaviour
    {
        public int pistolRounds = 18, shells = 6;

        public void Take(PlayerGun gun)
        {
            if (!gun.AddAmmo(pistolRounds, shells)) { GameManager.Instance?.Toast("You can't carry any more ammo.", 1.5f); return; }
            string what = pistolRounds > 0 && shells > 0 ? $"{pistolRounds} rounds and {shells} shells"
                        : pistolRounds > 0 ? $"{pistolRounds} pistol rounds" : $"{shells} shotgun shells";
            GameManager.Instance?.Toast("Picked up " + what, 1.5f);
            GunFX.Play(GunSound.Reload, transform.position, 0.5f);
            gameObject.SetActive(false);
        }

        /// <summary>Drops a small box where a character went down.</summary>
        public static void Drop(Vector3 pos, int rounds, int shells)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = "AmmoBox_Dropped";
            if (Physics.Raycast(pos + Vector3.up * 1.5f, Vector3.down, out var hit, 4f, ~(1 << 2), QueryTriggerInteraction.Ignore))
                pos = hit.point;
            box.transform.position = pos + Vector3.up * 0.09f;
            box.transform.rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            box.transform.localScale = new Vector3(0.32f, 0.18f, 0.22f);
            box.GetComponent<MeshRenderer>().material.color = new Color(0.28f, 0.34f, 0.2f);
            var lid = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.Destroy(lid.GetComponent<Collider>());
            lid.transform.SetParent(box.transform, false);
            lid.transform.localPosition = new Vector3(0f, 0.52f, 0f);
            lid.transform.localScale = new Vector3(1.02f, 0.1f, 1.02f);
            lid.GetComponent<MeshRenderer>().material.color = new Color(0.85f, 0.7f, 0.15f);
            var a = box.AddComponent<AmmoBox>();
            a.pistolRounds = rounds; a.shells = shells;
        }
    }
}
