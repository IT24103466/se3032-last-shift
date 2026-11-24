using UnityEngine;

namespace LastShift
{
    /// <summary>
    /// Supplies are collected automatically within reach, with E as an optional fallback.
    /// The level has crates along the route; knocked-out guards, workers and the hunter drop a small box.
    /// </summary>
    public class AmmoBox : MonoBehaviour
    {
        public int pistolRounds = 18, shells = 6;
        PlayerGun nearbyPlayer;
        float nextCheck;
        void Start()
        {
            pistolRounds = Random.Range(6, 11); shells = Random.Range(5, 8);
            PickupTargeting.Fit(this);
            nearbyPlayer = FindAnyObjectByType<PlayerGun>();
        }

        void Update()
        {
            if (Time.time < nextCheck || (GameManager.Instance != null && !GameManager.Instance.IsPlaying)) return;
            nextCheck = Time.time + 0.15f;
            if (nearbyPlayer == null) nearbyPlayer = FindAnyObjectByType<PlayerGun>();
            if (nearbyPlayer == null || !nearbyPlayer.CanAddAmmo) return;
            var player = nearbyPlayer.GetComponent<PlayerController>();
            if (player == null || player.Health.Dead || player.InLocker) return;
            var volume = transform.Find("InteractionVolume");
            var collider = volume != null ? volume.GetComponent<Collider>() : GetComponentInChildren<Collider>();
            Vector3 point = collider != null ? collider.ClosestPoint(player.transform.position + Vector3.up * 0.45f) : transform.position;
            if (Vector3.Distance(point, player.transform.position + Vector3.up * 0.45f) > 1.15f) return;
            Vector3 from = player.Chest, to = transform.position + Vector3.up * 0.1f;
            foreach (var hit in Physics.RaycastAll(from, (to - from).normalized, Vector3.Distance(from, to), ~(1 << 2), QueryTriggerInteraction.Ignore))
                if (!hit.transform.IsChildOf(player.transform) && !hit.transform.IsChildOf(transform)) return;
            Take(nearbyPlayer);
        }

        public void Take(PlayerGun gun)
        {
            if (!gameObject.activeInHierarchy) return;
            int beforePistol = gun.guns[0].reserve, beforeShotgun = gun.guns[1].reserve;
            if (!gun.AddAmmo(pistolRounds, shells)) return;
            string what = $"{gun.guns[0].reserve - beforePistol} pistol rounds and {gun.guns[1].reserve - beforeShotgun} shells";
            GameManager.Instance?.Toast("Picked up " + what, 1.5f);
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
