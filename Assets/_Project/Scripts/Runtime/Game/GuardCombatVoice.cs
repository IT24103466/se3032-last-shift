using UnityEngine;
namespace LastShift
{
    /// <summary>Guard-only combat lines, spaced per speaker and across the squad.</summary>
    public sealed class GuardCombatVoice : MonoBehaviour
    {
        EnemyAI ai; Health health; AudioSource voice;
        float nextLine, nextAttempt;
        static float nextSquadLine;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetSquad() => nextSquadLine=0f;
        public static GuardCombatVoice For(EnemyAI ai)
        {
            if(ai==null || ai.kind!=EnemyKind.Guard)return null;
            var existing=ai.GetComponent<GuardCombatVoice>();
            return existing!=null?existing:ai.gameObject.AddComponent<GuardCombatVoice>();
        }
        void Awake()
        {
            ai=GetComponent<EnemyAI>();health=GetComponent<Health>();
            if(health!=null)health.Died+=Stop;
        }
        void OnDestroy(){if(health!=null)health.Died-=Stop;Stop();}
        void Stop(){if(voice!=null){voice.Stop();Destroy(voice.gameObject);voice=null;}}
        public bool Threaten()=>TrySpeak(Random.value<0.5f?GunSound.GuardThreat2:GunSound.GuardThreat3,0.4f);
        public bool FireCallout()=>TrySpeak(Random.value<0.5f?GunSound.GuardFire1:GunSound.GuardFire4,0.25f);
        bool TrySpeak(GunSound line,float chance)
        {
            if(ai==null || ai.kind!=EnemyKind.Guard || health==null || health.Dead ||
                (GameManager.Instance!=null && !GameManager.Instance.IsPlaying) ||
                Time.time<nextLine || Time.time<nextAttempt || Time.time<nextSquadLine || (voice!=null && voice.isPlaying))return false;
            nextAttempt=Time.time+2.5f;
            if(Random.value>chance)return false;
            voice=GunFX.Play(line,transform.position+Vector3.up*1.4f,0.95f);
            voice.transform.SetParent(transform,true);
            nextLine=Time.time+Random.Range(12f,22f);
            nextSquadLine=Time.time+Mathf.Max(4f,voice.clip.length+1f);
            return true;
        }
    }
}
