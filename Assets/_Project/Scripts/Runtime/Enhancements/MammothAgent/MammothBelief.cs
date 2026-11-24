using UnityEngine;

namespace LastShift
{
    public enum BeliefState { Observed, Recent, Nearby, Uncertain, Lost }

    /// <summary>
    /// Sanjula's Mammoth - Bayesian belief about WHERE THE PLAYER IS.
    ///
    /// State S_t = the waypoint (of the tactical graph) the player is at. The mammoth keeps b_t(n) = P(S_t = n | O_1:t),
    /// a discrete probability distribution over the waypoints, and updates it with the standard recursive Bayes filter:
    ///
    ///   predict : b'(j)  = sum_i b(i) T(i->j)                     (the player may have moved)
    ///   update  : b''(j) = eta * L(o | j) * b'(j)                 (a sighting, a sound, or "I looked and saw nothing")
    ///
    /// T: each tick a waypoint's mass moves to its neighbours with probability m = v*dt/spacing (v = assumed player speed),
    ///    spread over the neighbours, biased towards the direction the player was last SEEN moving (momentum, fading with time).
    /// L, sighting : Gaussian around the seen position, sigma = observationSigma (about one waypoint spacing).
    /// L, sound    : eps + (1-eps) * Gaussian around the PERCEIVED source, sigma = max(source uncertainty, observationSigma).
    ///               eps = 0.05 + 0.4(1-strength): a faint sound is weak evidence (flatter likelihood).
    /// L, nothing  : places the mammoth can currently see get b *= (1 - pd) (negative evidence), pd = 0.9(1 - 0.6 d/range).
    ///               A rolling slice of the graph is checked each tick so the raycast cost stays constant.
    /// Normalisation: eta = 1/sum. A small uniform floor (floorMix) is added before each update so the filter can recover
    ///               if the player does something the model called very unlikely. If the sum ever collapses or is NaN the belief
    ///               is reset to uniform. Every probability is kept in [0,1].
    /// Confidence = 1 - H/Hmax (normalised Shannon entropy): 1 = certain, 0 = no idea. It falls by itself as time passes,
    ///               because prediction diffuses the mass.
    /// The player's true position is read ONLY while the perception says he is visible.
    /// </summary>
    [DefaultExecutionOrder(10)]
    public class MammothBelief : MonoBehaviour
    {
        [Header("Prediction (motion model)")]
        public float tickInterval = 0.2f;
        public float assumedPlayerSpeed = 3.2f;
        public float headingBias = 2f;
        public float headingMemory = 6f;
        [Header("Observation model")]
        public float observationSigma = 6f;
        public float floorMix = 0.01f;
        public int negativeSliceSize = 12;
        public float viewDetectChance = 0.9f;
        [Header("State classification")]
        public float lostConfidence = 0.15f;
        public float recentSeconds = 4f;
        public float nearbyRadius = 25f;
        public float nearbyMass = 0.5f;
        public bool logEvents = true;

        public TacticalGraph Graph { get; private set; }
        public bool Ready { get; private set; }
        public BeliefState State { get; private set; } = BeliefState.Lost;
        public float Confidence { get; private set; }
        public float PeakMass { get; private set; }
        public int BestNode { get; private set; } = -1;
        public Vector3 EstimatedPosition { get; private set; }
        public float Probability(int i) => Ready && i >= 0 && i < b.Length ? b[i] : 0f;

        MammothPerception perc;
        float[] b, next;
        float nextTick, lastTickTime;
        int sliceCursor, nReach;
        float logT;

        void Start()
        {
            perc = GetComponent<MammothPerception>();
            Graph = GetComponent<TacticalGraph>();
            if (perc == null || Graph == null) { enabled = false; return; }
            perc.NoiseHeard += OnNoise;
            if (Graph.Ready) Init(); else Graph.Built += Init;
        }

        void OnDestroy()
        {
            if (perc != null) perc.NoiseHeard -= OnNoise;
            if (Graph != null) Graph.Built -= Init;
        }

        void Init()
        {
            int n = Graph.Count;
            b = new float[n]; next = new float[n];
            nReach = Graph.ReachableCount;
            for (int i = 0; i < n; i++) b[i] = Graph.Reachable(i) ? 1f / nReach : 0f;   // prior: no idea where he is
            lastTickTime = Time.time;
            Ready = true;
            Summarise();
        }

        void Update()
        {
            if (!Ready || Time.time < nextTick) return;
            nextTick = Time.time + tickInterval;
            float dt = Time.time - lastTickTime; lastTickTime = Time.time;
            Predict(dt);
            if (perc.PlayerVisible) ApplySighting();
            else ApplyNegativeEvidence();
            Normalise();
            Summarise();
        }

        // ------------------------------------------------------------------ predict
        void Predict(float dt)
        {
            int n = b.Length;
            float move = Mathf.Clamp(assumedPlayerSpeed * dt / Mathf.Max(1f, Graph.spacing), 0f, 0.5f);
            Vector3 vel = perc.LastSeenVelocity; vel.y = 0f;
            float sp = vel.magnitude;
            Vector3 vdir = sp > 0.5f ? vel / sp : Vector3.zero;
            float bias = vdir == Vector3.zero || !perc.HasSeenPlayer ? 0f : headingBias * Mathf.Exp(-perc.TimeSinceSeen / headingMemory);

            System.Array.Clear(next, 0, n);
            for (int i = 0; i < n; i++)
            {
                float m = b[i];
                if (m <= 0f) continue;
                var nb = Graph.Neighbours(i);
                if (nb.Length == 0) { next[i] += m; continue; }
                float leave = m * move;
                next[i] += m - leave;
                var dirs = Graph.NeighbourDirections(i);
                float wsum = 0f;
                for (int k = 0; k < nb.Length; k++) wsum += 1f + bias * Mathf.Max(0f, Vector3.Dot(dirs[k], vdir));
                for (int k = 0; k < nb.Length; k++)
                    next[nb[k]] += leave * (1f + bias * Mathf.Max(0f, Vector3.Dot(dirs[k], vdir))) / wsum;
            }
            var t = b; b = next; next = t;
        }

        // ------------------------------------------------------------------ update
        void ApplySighting()
        {
            Vector3 p = perc.LastSeenPos;
            float inv = 1f / (2f * observationSigma * observationSigma);
            float floor = floorMix / Mathf.Max(1, nReach);
            for (int i = 0; i < b.Length; i++)
            {
                if (!Graph.Reachable(i)) continue;
                Vector3 q = Graph.Position(i);
                float dx = q.x - p.x, dz = q.z - p.z;
                b[i] = (b[i] + floor) * (Mathf.Exp(-(dx * dx + dz * dz) * inv) + 1e-5f);
            }
        }

        void OnNoise(NoiseEvent ev)
        {
            if (!Ready) return;
            float eps = 0.05f + 0.4f * (1f - Mathf.Clamp01(ev.strength));
            float sigma = Mathf.Max(ev.sigma, observationSigma);
            float inv = 1f / (2f * sigma * sigma);
            float floor = floorMix / Mathf.Max(1, nReach);
            for (int i = 0; i < b.Length; i++)
            {
                if (!Graph.Reachable(i)) continue;
                Vector3 q = Graph.Position(i);
                float dx = q.x - ev.pos.x, dz = q.z - ev.pos.z;
                b[i] = (b[i] + floor) * (eps + (1f - eps) * Mathf.Exp(-(dx * dx + dz * dz) * inv));
            }
            Normalise();
            Summarise();
        }

        void ApplyNegativeEvidence()
        {
            int n = b.Length;
            for (int s = 0; s < negativeSliceSize && s < n; s++)
            {
                int i = sliceCursor; sliceCursor = (sliceCursor + 1) % n;
                if (!Graph.Reachable(i) || b[i] <= 0f) continue;
                if (perc.CanSeePoint(Graph.Position(i) + Vector3.up * 1.0f, out float d))
                {
                    float pd = viewDetectChance * (1f - 0.6f * Mathf.Clamp01(d / perc.viewRange));
                    b[i] *= (1f - pd);
                }
            }
        }

        void Normalise()
        {
            float sum = 0f;
            for (int i = 0; i < b.Length; i++)
            {
                float v = b[i];
                if (float.IsNaN(v) || float.IsInfinity(v) || v < 0f) v = 0f;
                b[i] = v; sum += v;
            }
            if (sum < 1e-12f)       // evidence contradicted everything: forget and start over
            {
                for (int i = 0; i < b.Length; i++) b[i] = Graph.Reachable(i) ? 1f / nReach : 0f;
                return;
            }
            float inv = 1f / sum;
            for (int i = 0; i < b.Length; i++) b[i] = Mathf.Clamp01(b[i] * inv);
        }

        // ------------------------------------------------------------------ read-outs
        void Summarise()
        {
            int best = -1; float bp = -1f, h = 0f;
            for (int i = 0; i < b.Length; i++)
            {
                float v = b[i];
                if (v > bp) { bp = v; best = i; }
                if (v > 1e-9f) h -= v * Mathf.Log(v);
            }
            float hmax = nReach > 1 ? Mathf.Log(nReach) : 1f;
            Confidence = Mathf.Clamp01(1f - h / hmax);
            PeakMass = Mathf.Max(0f, bp);
            BestNode = best;
            if (best >= 0)
            {
                Vector3 c = Graph.Position(best); float w = 0f; Vector3 acc = Vector3.zero;
                for (int i = 0; i < b.Length; i++)
                {
                    if (b[i] <= 0f) continue;
                    Vector3 q = Graph.Position(i);
                    float dx = q.x - c.x, dz = q.z - c.z;
                    if (dx * dx + dz * dz > 225f) continue;      // within 15 m of the peak
                    acc += q * b[i]; w += b[i];
                }
                EstimatedPosition = w > 0f ? acc / w : c;
            }

            BeliefState s;
            if (perc.PlayerVisible) s = BeliefState.Observed;
            else if (perc.HasSeenPlayer && perc.TimeSinceSeen < recentSeconds) s = BeliefState.Recent;
            else if (Confidence < lostConfidence) s = BeliefState.Lost;
            else if (MassWithin(transform.position, nearbyRadius) > nearbyMass) s = BeliefState.Nearby;
            else s = BeliefState.Uncertain;
            if (s != State && logEvents) Debug.Log($"[MammothAI] belief {State} -> {s} (confidence {Confidence:F2}, peak {PeakMass:F2})");
            State = s;
        }

        /// <summary>Total probability that the player is within <paramref name="r"/> metres (flat) of a point.</summary>
        public float MassWithin(Vector3 centre, float r)
        {
            if (!Ready) return 0f;
            float r2 = r * r, sum = 0f;
            for (int i = 0; i < b.Length; i++)
            {
                if (b[i] <= 0f) continue;
                Vector3 q = Graph.Position(i);
                float dx = q.x - centre.x, dz = q.z - centre.z;
                if (dx * dx + dz * dz <= r2) sum += b[i];
            }
            return sum;
        }
    }
}
