using UnityEngine;
namespace LastShift
{
    /// <summary>Supplied factory alarm: restrained positional loop and a brief, quiet discovery cue.</summary>
    public class AlarmSiren : MonoBehaviour
    {
        [Range(0f,0.5f)] public float volume=0.22f;
        static AudioClip clip;
        AudioSource near, far;
        float started;
        public static void Sound(Vector3 roof)
        {
            if(FindAnyObjectByType<AlarmSiren>()!=null)return;
            var go=new GameObject("AlarmSiren");go.transform.position=roof;go.AddComponent<AlarmSiren>();
        }
        void Start()
        {
            if(clip==null)clip=Resources.Load<AudioClip>("Audio/SFX/alarm");
            if(clip==null){Debug.LogWarning("Factory alarm clip is missing.");enabled=false;return;}
            clip.LoadAudioData();started=Time.time;
            far=gameObject.AddComponent<AudioSource>();far.clip=clip;far.loop=true;far.playOnAwake=false;
            far.spatialBlend=1f;far.volume=0f;far.priority=160;far.rolloffMode=AudioRolloffMode.Linear;
            far.minDistance=12f;far.maxDistance=110f;far.dopplerLevel=0f;
            near=gameObject.AddComponent<AudioSource>();near.clip=clip;near.loop=true;near.playOnAwake=false;
            near.spatialBlend=0f;near.volume=0f;near.priority=180;
            double start=AudioSettings.dspTime+0.05;far.PlayScheduled(start);near.PlayScheduled(start);
        }
        void Update()
        {
            if(far==null || near==null)return;
            var gm=GameManager.Instance;bool on=gm==null || gm.IsPlaying;
            float age=Time.time-started;
            float farTarget=on?volume*(age<8f?1f:0.6f):0f;
            float nearTarget=on && age<3f?0.055f:0f;
            far.volume=Mathf.MoveTowards(far.volume,farTarget,Time.unscaledDeltaTime*0.3f);
            near.volume=Mathf.MoveTowards(near.volume,nearTarget,Time.unscaledDeltaTime*0.15f);
            if(gm!=null && (gm.phase==GameManager.Phase.Won || gm.phase==GameManager.Phase.Lost) && far.volume<=0f && near.volume<=0f)Destroy(gameObject);
        }
    }
}
