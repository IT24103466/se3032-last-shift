using UnityEngine;

namespace LastShift
{
    /// <summary>
    /// Keeps fights one-on-one: only ONE enemy (an animal, a guard, a worker, the dog, the mammoth)
    /// may attack the player at a time. Everyone else who notices him keeps their distance and
    /// watches until that fight is over, then there's a short breather before the next can come.
    ///
    /// An attacker keeps its turn by calling <see cref="TryAttack"/> every frame it is attacking
    /// (a heartbeat); if it stops (killed, lost the player, gave up), the turn frees itself after
    /// <see cref="Timeout"/> seconds, so nobody has to remember to release it.
    /// The start camp is a safe zone: nothing attacks there (the camp fire keeps animals away).
    /// </summary>
    public static class ThreatDirector
    {
        public const float CampSafeRadius = 22f;
        const float Timeout = 1.2f;      // heartbeat (longer than a flinch)
        const float Breather = 2.5f;     // pause between one fight ending and the next starting

        static Object holder;
        static string holderName;
        static float holderSeen, freeAt;
        static Vector3 camp; static bool campKnown;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset() { holder = null; holderName = null; freeAt = 0f; campKnown = false; }

        /// <summary>Who has the turn right now (null if nobody).</summary>
        public static Object Holder
        {
            get
            {
                if (holder != null && Time.time - holderSeen > Timeout) { holder = null; freeAt = Time.time + Breather; }
                if (holder == null) holderName = null;
                return holder;
            }
        }

        /// <summary>Name to show in the HUD ("Tiger", "Guard", "Kalu"...), or null.</summary>
        public static string HolderName => Holder != null ? holderName : null;

        /// <summary>
        /// May <paramref name="who"/> attack now? True if it already has the turn (refreshes it)
        /// or the turn is free; false while someone else is fighting, during the breather, or
        /// when the player is at the camp.
        /// </summary>
        public static bool TryAttack(Object who, string displayName, Vector3 playerPos)
        {
            if (InCamp(playerPos)) return false;
            var h = Holder;
            if (h == who) { holderSeen = Time.time; return true; }
            if (h != null || Time.time < freeAt) return false;
            holder = who; holderName = displayName; holderSeen = Time.time;
            return true;
        }

        /// <summary>Give the turn up straight away (e.g. the attacker died).</summary>
        public static void Release(Object who)
        {
            if (holder != null && holder == who) { holder = null; holderName = null; freeAt = Time.time + Breather; }
        }

        public static bool InCamp(Vector3 playerPos)
        {
            if (!campKnown)
            {
                var spawn = GameObject.Find("Spawn_Player");
                if (spawn == null) return false;
                camp = spawn.transform.position; campKnown = true;
            }
            Vector3 d = playerPos - camp; d.y = 0f;
            return d.magnitude < CampSafeRadius;
        }
    }
}
