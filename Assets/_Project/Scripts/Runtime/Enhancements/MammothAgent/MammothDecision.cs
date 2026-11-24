using System;
using UnityEngine;

namespace LastShift
{
    public enum MammothAction { Patrol, InvestigateSound, TrackLastKnown, Search, Pursue, Ambush, ReturnHome }

    /// <summary>
    /// Sanjula's Mammoth - utility-based decision engine. It only DECIDES (which action is best now); MammothAI executes it.
    /// Every candidate action gets a utility U(a) in roughly [0,1] from a weighted sum of considerations (all weights are public
    /// and explained in Docs/MammothAI.md):
    ///
    ///   Pursue   = [gate: seen AND (inside aggro range OR provoked/enraged OR continuing)] (wSeen + wProx*prox + wProvoked*provoked + wEnrage*enraged)
    ///              + wConf*confidence*freshness while already chasing  - leashPenalty*beyondLeash*(1 - provokedOrEnraged)
    ///   Track    = seen inside notice range: wTrackVisible*(0.6+0.4*closeness);   not seen: (wFresh*freshness + wConf*confidence)*(1 - arrived)
    ///   Investigate = wClue * clueQuality * clueFreshness        (clueQuality = 1 - sigma/40: a pinpointed shot beats a vague rustle)
    ///   Search   = wSearch * recentEvidence * (1 - lost) * (ready ? 1 : 0.35)   (only after tracking is exhausted)
    ///   Return   = wTerritory*excursion + wLost*lost*timeSinceEvidence*farFromHome
    ///   Patrol   = patrolBase (the floor: do this when nothing else matters)
    ///
    /// freshness = exp(-timeSinceSeen/12 s); recentEvidence = exp(-timeSinceAnyEvidence/40 s); lost = 1 - clamp(confidence/0.3);
    /// the hidden notice/aggro/leash ranges (scaled by the player's stealth) are the mammoth's territorial personality.
    ///
    /// Stability: the current action gets a x1.15 bonus (+0.02) so a rival must clearly beat it; each action has a minimum commit time;
    /// decisions are taken 4 times a second; an imminent threat (seen inside aggro range with high pursuit utility) overrides immediately.
    /// </summary>
    [DefaultExecutionOrder(20)]
    public class MammothDecision : MonoBehaviour
    {
        [Header("Pursue")]
        public float wPursueSeen = 0.35f, wPursueProx = 0.45f, wPursueConf = 0.20f, wProvoked = 0.30f, wEnrage = 0.25f, leashPenalty = 0.8f;
        [Header("Track last known position")]
        public float wTrackVisible = 0.45f, wTrackFresh = 0.55f, wTrackConf = 0.25f;
        [Header("Investigate / Search / Return / Patrol")]
        public float wClue = 0.6f;
        public float wSearch = 0.55f;
        public float wTerritory = 0.7f, wLost = 0.5f;
        public float patrolBase = 0.25f;
        [Header("Stability")]
        public float decisionInterval = 0.25f;
        public float hysteresis = 1.15f, hysteresisBonus = 0.02f;
        public bool logChanges = true;

        public MammothAction Current { get; private set; } = MammothAction.Patrol;
        public readonly float[] Utilities = new float[7];
        public float TimeInAction => Time.time - changedAt;
        public event Action<MammothAction, MammothAction> Changed;

        static readonly float[] MinCommit = { 3f, 2f, 1.5f, 3f, 1f, 3f, 3f };   // seconds, indexed by MammothAction

        MammothAI ai; MammothPerception perc; MammothBelief belief; MammothMemory memory;
        float nextDecide, changedAt;

        void Start()
        {
            ai = GetComponent<MammothAI>(); perc = GetComponent<MammothPerception>();
            belief = GetComponent<MammothBelief>(); memory = GetComponent<MammothMemory>();
            if (ai == null || perc == null) enabled = false;
        }

        static float Flat(Vector3 a, Vector3 b) { float dx = a.x - b.x, dz = a.z - b.z; return Mathf.Sqrt(dx * dx + dz * dz); }

        void Update()
        {
            if (Time.time < nextDecide) return;
            nextDecide = Time.time + decisionInterval;
            Decide();
        }

        void Decide()
        {
            var u = Utilities; Array.Clear(u, 0, u.Length);
            if (ai.IsDead) return;
            Vector3 pos = transform.position;
            float excess = Flat(pos, ai.Home) / Mathf.Max(1f, ai.LeashR);

            if (!ai.PlayerLive)       // no player to hunt (dead / paused): go home and idle
            {
                u[(int)MammothAction.Patrol] = patrolBase;
                u[(int)MammothAction.ReturnHome] = excess > 0.15f ? 0.5f : 0f;
                Commit(false);
                return;
            }

            float provoked = Mathf.Exp(-(Time.time - ai.LastHurtTime) / 8f);
            // Noise-driven senses: eyesight only CONFIRMS a target once a noise / hit has made the mammoth alert.
            // A silent player (walking, crouching, standing still) is ignored even when standing in plain view.
            bool alert = perc.IsAlert || provoked > 0.05f || ai.Enraged;
            bool inCamp = perc.HasSeenPlayer && MammothTerritory.InCamp(perc.LastSeenPos);   // the camp is out of bounds: never hunt into it
            bool vis = perc.PlayerVisible && alert && !inCamp;
            float dVis = vis ? Flat(pos, perc.LastSeenPos) : 999f;
            float mul = perc.StealthMul();
            float noticeEff = ai.NoticeR * mul, aggroEff = ai.AggroR * Mathf.Lerp(1f, mul, 0.6f);
            float enr = ai.Enraged ? 1f : 0f;
            float pe = Mathf.Max(provoked, enr);
            bool seenEver = perc.HasSeenPlayer && alert && !inCamp;
            float tSeen = seenEver ? perc.TimeSinceSeen : 999f;
            float tNoise = perc.HasHeardNoise ? perc.NoiseAge : 999f;
            float tEvid = Mathf.Min(tSeen, tNoise);
            float fresh = Mathf.Exp(-tSeen / 12f);
            float evRecent = Mathf.Exp(-tEvid / 40f);
            float conf = belief != null && belief.Ready ? belief.Confidence : 0f;
            float lost = 1f - Mathf.Clamp01(conf / 0.3f);
            float beyond = Mathf.Clamp01((excess - 0.9f) / 0.3f);
            bool chasing = Current == MammothAction.Pursue;

            // --- Pursue
            bool gate = vis && (dVis < aggroEff || (pe > 0.3f && dVis < noticeEff * 1.5f) || (chasing && dVis < noticeEff));
            float pursue = 0f;
            if (gate)
            {
                float prox = Mathf.Clamp01(1f - dVis / (aggroEff * 1.5f));
                pursue = wPursueSeen + wPursueProx * prox + wProvoked * provoked + wEnrage * enr;
            }
            if (chasing) pursue += wPursueConf * conf * fresh;           // keep running for a moment after he breaks line of sight
            pursue -= leashPenalty * beyond * (1f - pe);
            u[(int)MammothAction.Pursue] = Mathf.Max(0f, pursue);

            // --- Track last known position
            float arrived = 0f;
            if (vis) { if (dVis < noticeEff) u[(int)MammothAction.TrackLastKnown] = wTrackVisible * (0.6f + 0.4f * Mathf.Clamp01(1f - dVis / noticeEff)); }
            else if (seenEver)
            {
                arrived = Flat(pos, perc.LastSeenPos) < 6f ? 1f : 0f;
                u[(int)MammothAction.TrackLastKnown] = (wTrackFresh * fresh + wTrackConf * conf) * (1f - arrived);
            }

            // --- Investigate a sound
            if (!vis && memory != null && memory.TryGetFreshClue(out var clue, 20f))
            {
                float q = Mathf.Clamp01(1f - clue.sigma / 40f) * Mathf.Exp(-(Time.time - clue.time) / 15f);
                u[(int)MammothAction.InvestigateSound] = wClue * q;
            }

            // --- Search
            if (!vis)
            {
                bool ready = !seenEver || arrived > 0.5f || tSeen > 30f;
                u[(int)MammothAction.Search] = wSearch * evRecent * (1f - lost) * (ready ? 1f : 0.35f);
            }

            // --- Return to territory
            float lostTime = Mathf.Clamp01((tEvid - 15f) / 30f);
            float farHome = Mathf.Clamp01((Flat(pos, ai.Home) - 16f) / 20f);
            u[(int)MammothAction.ReturnHome] = wTerritory * Mathf.Clamp01(excess) + wLost * lost * lostTime * farHome * (vis ? 0f : 1f);

            // --- Patrol (the floor)
            u[(int)MammothAction.Patrol] = patrolBase;

            bool urgent = vis && dVis < aggroEff && u[(int)MammothAction.Pursue] > 0.6f;
            Commit(urgent);
        }

        void Commit(bool urgent)
        {
            int cur = (int)Current, best = cur;
            float bestU = Utilities[cur] * hysteresis + hysteresisBonus;
            for (int i = 0; i < Utilities.Length; i++)
            {
                if (i == cur) continue;
                if (Utilities[i] > bestU) { bestU = Utilities[i]; best = i; }
            }
            if (best == cur) return;
            if (!urgent && TimeInAction < MinCommit[cur] && Utilities[cur] >= 0.05f) return;   // minimum commit time
            var old = Current; Current = (MammothAction)best; changedAt = Time.time;
            if (logChanges) Debug.Log($"[MammothAI] decision {old} -> {Current} (u {Utilities[best]:F2} vs {Utilities[cur]:F2}; belief {(belief != null ? belief.State.ToString() : "-")}, conf {(belief != null ? belief.Confidence : 0f):F2})");
            Changed?.Invoke(old, Current);
        }
    }
}
