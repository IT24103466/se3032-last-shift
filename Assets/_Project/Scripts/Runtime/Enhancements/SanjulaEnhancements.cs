using UnityEngine;
using UnityEngine.SceneManagement;

namespace LastShift
{
    /// <summary>
    /// Sanjula (IT24103715) enhancements: root object that wires up better graphics, permanent
    /// (non-regenerating) health and Sanjula's Mammoth. Created automatically when a level with a
    /// PlayerController loads - no scene edits, no changes to teammates' files.
    /// </summary>
    public class SanjulaEnhancements : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Init()
        {
            SceneManager.sceneLoaded += (s, m) => Spawn();
            Spawn();
        }

        static void Spawn()
        {
            if (FindAnyObjectByType<PlayerController>() == null) return;
            if (FindAnyObjectByType<SanjulaEnhancements>() != null) return;
            new GameObject("Sanjula_Enhancements").AddComponent<SanjulaEnhancements>();
        }

        void Awake()
        {
            gameObject.AddComponent<GraphicsEnhancer>();
            gameObject.AddComponent<PlayerVitality>();
            gameObject.AddComponent<MammothSpawner>();
            gameObject.AddComponent<ThreatMap>();
        }
    }
}
