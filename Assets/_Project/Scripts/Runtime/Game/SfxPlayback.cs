using UnityEngine;
namespace LastShift
{
    /// <summary>Each report owns its source until its DSP playback completes, independently of game speed.</summary>
    public sealed class SfxPlayback : MonoBehaviour
    {
        double end;
        public void FinishAfter(AudioSource source) => end=AudioSettings.dspTime+source.clip.length/Mathf.Max(0.1f,source.pitch)+0.15;
        void Update(){if(end>0 && AudioSettings.dspTime>=end)Destroy(gameObject);}
    }
}
