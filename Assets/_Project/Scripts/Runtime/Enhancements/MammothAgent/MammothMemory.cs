using System.Collections.Generic;
using UnityEngine;

namespace LastShift
{
    /// <summary>
    /// Sanjula's Mammoth - long-term memory (the belief is the short-term "where is he now").
    ///   * recent sightings (ring buffer)             - where and when the player was actually seen
    ///   * clues (noises) and whether they were checked - so it does not walk to the same sound twice without new evidence
    ///   * searched places, per waypoint, fading       - so a search does not keep returning to ground already covered
    ///   * route heat, per waypoint, slowly fading     - where the player tends to be seen; recent evidence outweighs old
    ///   * hunt failures                               - how many times in a row it gave up without finding him
    /// Nothing here knows more than the mammoth has perceived.
    /// </summary>
    [DefaultExecutionOrder(5)]
    public class MammothMemory : MonoBehaviour
    {
        public struct Sighting { public Vector3 pos; public float time; }
        public struct Clue { public Vector3 pos; public float time, sigma; public bool checkedOut; }

        [Header("Capacities / lifetimes")]
        public int sightingCapacity = 16;
        public int clueCapacity = 24;
        public float clueMemorySeconds = 45f;
        public float searchMemorySeconds = 40f;
        public float heatHalfLifeSeconds = 90f;
        [Tooltip("A new noise this close to an already-checked recent clue adds no new information")]
        public float repeatClueRadius = 12f;
        public float sightingRecordInterval = 1f;

        public int HuntFailures { get; private set; }
        public int IgnoredRepeatClues { get; private set; }
        public int SightingCount => sightCount;

        TacticalGraph graph; MammothPerception perc;
        Sighting[] sightings; int sightHead, sightCount;
        readonly List<Clue> clues = new List<Clue>();
        float[] searchedAt, heat; float maxHeat = 1f;
        float lastRecord = -99f, lastUpdate;

        void Start()
        {
            graph = GetComponent<TacticalGraph>(); perc = GetComponent<MammothPerception>();
            sightings = new Sighting[Mathf.Max(2, sightingCapacity)];
            if (perc == null || graph == null) { enabled = false; return; }
            perc.NoiseHeard += OnNoise;
            perc.SightChanged += OnSightChanged;
            if (graph.Ready) Init(); else graph.Built += Init;
            lastUpdate = Time.time;
        }

        void OnDestroy()
        {
            if (perc != null) { perc.NoiseHeard -= OnNoise; perc.SightChanged -= OnSightChanged; }
            if (graph != null) graph.Built -= Init;
        }

        void Init()
        {
            searchedAt = new float[graph.Count]; heat = new float[graph.Count];
            for (int i = 0; i < searchedAt.Length; i++) searchedAt[i] = -999f;
        }

        void OnSightChanged(bool seen) { if (seen) HuntFailures = 0; }

        void Update()
        {
            float dt = Time.time - lastUpdate; lastUpdate = Time.time;
            if (heat != null && dt > 0f)
            {
                float k = Mathf.Pow(0.5f, dt / heatHalfLifeSeconds);
                maxHeat = 1f;
                for (int i = 0; i < heat.Length; i++) { heat[i] *= k; if (heat[i] > maxHeat) maxHeat = heat[i]; }
            }
            if (perc.PlayerVisible && Time.time - lastRecord >= sightingRecordInterval)
            {
                lastRecord = Time.time;
                RecordSighting(perc.LastSeenPos);
            }
        }

        // ------------------------------------------------------------------ sightings + route heat
        public void RecordSighting(Vector3 pos)
        {
            sightings[sightHead] = new Sighting { pos = pos, time = Time.time };
            sightHead = (sightHead + 1) % sightings.Length;
            if (sightCount < sightings.Length) sightCount++;
            if (heat != null && graph.Ready) { int n = graph.NearestNode(pos); if (n >= 0) heat[n] += 1f; }
        }

        /// <summary>i = 0 is the most recent sighting.</summary>
        public bool TryGetSighting(int i, out Sighting s)
        {
            s = default;
            if (i < 0 || i >= sightCount) return false;
            s = sightings[((sightHead - 1 - i) % sightings.Length + sightings.Length) % sightings.Length];
            return true;
        }

        /// <summary>0..1: how often the player has recently been seen near this waypoint (old sightings have faded).</summary>
        public float RouteHeat(int node) => heat != null && node >= 0 && node < heat.Length ? Mathf.Clamp01(heat[node] / maxHeat) : 0f;

        // ------------------------------------------------------------------ clues
        void OnNoise(NoiseEvent ev)
        {
            if (IsRepeatClue(ev.pos)) { IgnoredRepeatClues++; return; }
            if (clues.Count >= clueCapacity) clues.RemoveAt(0);
            clues.Add(new Clue { pos = ev.pos, time = ev.time, sigma = ev.sigma, checkedOut = false });
        }

        bool IsRepeatClue(Vector3 p)
        {
            float r2 = repeatClueRadius * repeatClueRadius;
            for (int i = 0; i < clues.Count; i++)
            {
                var c = clues[i];
                if (!c.checkedOut || Time.time - c.time > clueMemorySeconds) continue;
                Vector3 d = c.pos - p; d.y = 0f;
                if (d.sqrMagnitude < r2) return true;
            }
            return false;
        }

        /// <summary>Newest clue nobody has checked yet and that is still fresh enough to be worth a visit.</summary>
        public bool TryGetFreshClue(out Clue clue, float maxAge)
        {
            for (int i = clues.Count - 1; i >= 0; i--)
                if (!clues[i].checkedOut && Time.time - clues[i].time <= maxAge) { clue = clues[i]; return true; }
            clue = default; return false;
        }

        /// <summary>The mammoth went there and found nothing new: every clue within the radius counts as checked.</summary>
        public void MarkCluesChecked(Vector3 where, float radius)
        {
            float r2 = radius * radius;
            for (int i = 0; i < clues.Count; i++)
            {
                Vector3 d = clues[i].pos - where; d.y = 0f;
                if (d.sqrMagnitude <= r2) { var c = clues[i]; c.checkedOut = true; clues[i] = c; }
            }
        }

        // ------------------------------------------------------------------ searched places
        public void MarkSearched(int node)
        {
            if (searchedAt != null && node >= 0 && node < searchedAt.Length) searchedAt[node] = Time.time;
        }

        /// <summary>0..1: 1 = just searched here (do not go back), fading to 0 over searchMemorySeconds.</summary>
        public float SearchPenalty(int node)
        {
            if (searchedAt == null || node < 0 || node >= searchedAt.Length) return 0f;
            return Mathf.Clamp01(1f - (Time.time - searchedAt[node]) / searchMemorySeconds);
        }

        public void ReportHuntFailure() { HuntFailures++; }
    }
}
