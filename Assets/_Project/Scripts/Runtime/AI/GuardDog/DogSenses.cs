using LastShift.AI.Core;
using UnityEngine;

namespace LastShift.GuardDog
{
    /// <summary>
    /// Everything the dog currently believes about the world. Written by <see cref="DogSenses"/>
    /// and by the brain's actions, read by the behaviour tree's conditions. Keeping it in one
    /// place means the decision logic never touches GameObjects directly.
    /// </summary>
    public sealed class DogBlackboard
    {
        // Sight
        public bool SeesPlayer;
        public Vector3 PlayerPos;
        public float DistToPlayer = float.MaxValue;
        public float LastSeenTime = -99f;
        public bool PlayerHidden;
        public bool PlayerAimingAtMe;        // a gun pointed at the dog: approach in a zigzag

        // Smell
        public float ScentHere;
        public ScentField.Hit TrailAhead;    // where the trail continues (newer than TrailTime)
        public float TrailTime = float.NegativeInfinity;   // deposit time of the trail point reached
        public Vector3 TrailHeading = Vector3.forward;
        public Vector3 TrailLostPos;
        public float TrailLostTime = -99f;
        public bool WasOnTrail;

        // Hearing
        public Vector3 NoisePos;
        public float NoiseTime = -99f, NoiseLoudness;
        public bool NoiseHandled = true;
        public float Interest = 1f;          // habituation to small false alarms (thrown pebbles)
        public float AlertedUntil = -99f;    // a gunshot puts it on edge for a while

        // Self
        public bool Retreating;
        public bool Alerted(float now) => now < AlertedUntil;
    }

    /// <summary>
    /// The dog's perception, refreshed ten times a second:
    ///  - sight: a wide (220°) but short cone (dogs see poorly at night), longer when the player
    ///    has his torch on, shorter when he crouches; needs a clear line from the dog's eyes;
    ///  - smell: scent strength under its nose and where the trail continues (newer deposits
    ///    within its sniffing radius);
    ///  - hearing: every noise in the world via <see cref="EnemyAI.NoiseMade"/>, heard at 1.5× the
    ///    distance a person would;
    ///  - weapon awareness: is the player's gun pointing at it right now?
    /// </summary>
    public sealed class DogSenses
    {
        public float SightRange = 16f, SightRangeAlerted = 24f, HalfFov = 110f, SniffRadius = 4.5f, ScentThreshold = 0.06f;

        readonly DogBlackboard bb;
        readonly IDogBody body;
        readonly PlayerController player;
        readonly PlayerGun gun;
        readonly ScentTracker world;

        public DogSenses(DogBlackboard bb, IDogBody body, PlayerController player, ScentTracker world)
        {
            this.bb = bb; this.body = body; this.player = player; this.world = world;
            gun = player.GetComponent<PlayerGun>();
            EnemyAI.NoiseMade += OnNoise;
        }

        public void Dispose() => EnemyAI.NoiseMade -= OnNoise;

        public void Update(float now, float dt)
        {
            bb.Interest = Mathf.Min(1f, bb.Interest + dt / 90f);   // forgets false alarms over ~1.5 min

            // ---- sight
            bb.PlayerPos = player.transform.position;
            bb.DistToPlayer = Vector3.Distance(body.Position, bb.PlayerPos);
            bb.PlayerHidden = player.Hidden;
            float range = (bb.Alerted(now) ? SightRangeAlerted : SightRange) * (player.TorchOn ? 1.5f : 1f) * (player.Crouched ? 0.7f : 1f);
            bool sees = false;
            if (!player.Hidden && !player.Health.Dead && bb.DistToPlayer < range)
            {
                Vector3 to = player.Chest - body.Eyes;
                if (Vector3.Angle(body.Forward, Vector3.ProjectOnPlane(to, Vector3.up)) < HalfFov || bb.DistToPlayer < 2.5f)
                    sees = ClearLine(body.Eyes, player.Chest);
            }
            bb.SeesPlayer = sees;
            if (sees) bb.LastSeenTime = now;   // (the trail clock only moves when the dog reaches a trail point)

            // ---- gun pointed at me?
            bb.PlayerAimingAtMe = false;
            if (gun != null && gun.OnGun && player.eyes != null && bb.DistToPlayer < 30f)
            {
                Vector3 toDog = body.Position + Vector3.up * 0.4f - player.eyes.position;
                bb.PlayerAimingAtMe = Vector3.Angle(player.eyes.forward, toDog) < 12f;
            }

            // ---- smell
            int here = world.CellOf(body.Position);
            bb.ScentHere = world.Scent.Strength(here, now);
            bb.TrailAhead = world.Scent.FreshestNear(here, SniffRadius, ScentThreshold, bb.TrailTime, now);
            if (bb.TrailAhead.Valid) bb.WasOnTrail = true;
            else if (bb.WasOnTrail && !sees)
            {
                // The trail just went cold: remember where, so the dog can cast around for it
                bb.WasOnTrail = false;
                bb.TrailLostTime = now;
                bb.TrailLostPos = body.Position;
            }
        }

        bool ClearLine(Vector3 from, Vector3 to)
        {
            Vector3 d = to - from;
            foreach (var h in Physics.RaycastAll(from, d.normalized, d.magnitude, ~(1 << 2), QueryTriggerInteraction.Ignore))
            {
                if (h.collider.transform.IsChildOf(body.Transform)) continue;
                if (h.collider.GetComponentInParent<PlayerController>() != null) continue;
                return false;
            }
            return true;
        }

        void OnNoise(Vector3 pos, float radius)
        {
            float d = Vector3.Distance(body.Position, pos);
            if (d < 2f || d > radius * 1.5f) return;   // its own bark, or too far to hear
            float now = Time.time;
            bool louder = radius >= bb.NoiseLoudness || now - bb.NoiseTime > 3f || bb.NoiseHandled;
            if (!louder) return;
            bb.NoisePos = pos; bb.NoiseTime = now; bb.NoiseLoudness = radius; bb.NoiseHandled = false;
            if (radius >= 30f) bb.AlertedUntil = now + 60f;   // gunfire: on edge for a minute
        }
    }
}
