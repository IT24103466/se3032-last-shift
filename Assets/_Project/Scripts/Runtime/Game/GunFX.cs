using System.Collections.Generic;
using UnityEngine;

namespace LastShift
{
    public enum GunSound { Pistol, Shotgun, Rifle, Reload, Empty, Pump, PistolReload, ShotgunReload, HumanHurt1, HumanHurt2, HumanDeath, TigerGrowl, M16, M16Reload, DogBark1, DogBark2, ElephantTrumpet1, ElephantTrumpet2, GuardFire1, GuardThreat2, GuardThreat3, GuardFire4 }

    /// <summary>
    /// Shared gunfire effects for the player and the enemies: tracers, muzzle flashes, impact sparks
    /// and positional sound. Supplied SFX are loaded from Resources, with generated fallbacks.
    /// </summary>
    public static class GunFX
    {
        static Material tracerMat, sparkMat, fleshMat;
        static readonly Dictionary<GunSound, AudioClip> clips = new Dictionary<GunSound, AudioClip>();

        /// <summary>Called by the player's gun with the materials the builder made.</summary>
        public static void Init(Material tracer, Material spark)
        {
            if (tracer != null) tracerMat = tracer;
            if (spark != null) sparkMat = spark;
            foreach(var sound in new[]{GunSound.Pistol,GunSound.Shotgun,GunSound.M16,GunSound.PistolReload,GunSound.ShotgunReload,GunSound.M16Reload}) GetClip(sound);
        }

        // ------------------------------------------------------------------ visuals

        /// <summary>A bright streak from the muzzle to where the bullet went, gone in a blink.</summary>
        public static void Tracer(Vector3 from, Vector3 to, float width = 0.025f, float life = 0.06f)
        {
            var go = new GameObject("Tracer");
            var lr = go.AddComponent<LineRenderer>();
            lr.positionCount = 2;
            lr.SetPosition(0, from);
            lr.SetPosition(1, to);
            lr.startWidth = width; lr.endWidth = width * 0.3f;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            if (tracerMat != null) lr.sharedMaterial = tracerMat;
            Object.Destroy(go, life);
        }

        /// <summary>Sparks and a puff where a bullet hits something.</summary>
        public static void Impact(Vector3 point, Vector3 normal, bool flesh)
        {
            int n = flesh ? 3 : 5;
            for (int i = 0; i < n; i++)
            {
                var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                s.name = "Spark";
                Object.Destroy(s.GetComponent<Collider>());
                s.transform.position = point + normal * 0.03f;
                s.transform.localScale = Vector3.one * (flesh ? 0.05f : 0.03f);
                var r = s.GetComponent<MeshRenderer>();
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                if (flesh)
                {
                    if (fleshMat == null) { fleshMat = new Material(r.sharedMaterial); fleshMat.color = new Color(0.35f, 0.02f, 0.02f); }
                    r.sharedMaterial = fleshMat;
                }
                else if (sparkMat != null) r.sharedMaterial = sparkMat;
                var fly = s.AddComponent<SparkFly>();
                fly.velocity = (normal + Random.insideUnitSphere * 0.8f).normalized * Random.Range(1.5f, 4f);
            }
        }

        /// <summary>A quick point light at the muzzle so a shot lights up the night.</summary>
        public static void Flash(Vector3 pos, float intensity = 6f)
        {
            var go = new GameObject("MuzzleFlash");
            go.transform.position = pos;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point; l.range = 7f; l.intensity = intensity;
            l.color = new Color(1f, 0.75f, 0.4f); l.shadows = LightShadows.None;
            Object.Destroy(go, 0.05f);
        }

        // ------------------------------------------------------------------ sound
        public static GunSound ReloadFor(GunSound gun) => gun == GunSound.M16 ? GunSound.M16Reload : gun == GunSound.Shotgun ? GunSound.ShotgunReload : gun == GunSound.Pistol ? GunSound.PistolReload : GunSound.Reload;

        public static AudioSource Play(GunSound s, Vector3 pos, float volume = 1f)
        {
            var clip = GetClip(s);
            var go = new GameObject("GunSound");
            go.transform.position = pos;
            var src = go.AddComponent<AudioSource>();
            src.clip = clip;
            src.priority = s == GunSound.Shotgun || s == GunSound.Pistol || s == GunSound.M16 ? 8 : IsGuardLine(s) ? 48 : 80;
            src.volume = volume;
            src.spatialBlend = 1f;
            src.rolloffMode = AudioRolloffMode.Linear;
            src.minDistance = IsGuardLine(s) ? 4f : 3.5f;
            src.dopplerLevel = 0f;
            src.maxDistance = IsGuardLine(s) ? 35f : s == GunSound.M16Reload || s == GunSound.Reload || s == GunSound.PistolReload || s == GunSound.ShotgunReload || s == GunSound.Empty || s == GunSound.Pump ? 12f
                : s == GunSound.HumanHurt1 || s == GunSound.HumanHurt2 || s == GunSound.HumanDeath ? 25f : 90f;
            src.pitch = Random.Range(0.94f, 1.06f);
            src.Play();
            go.name = s == GunSound.M16 ? "M16 shot (independent)" : "SFX " + s;
            go.AddComponent<SfxPlayback>().FinishAfter(src);
            return src;
        }

        static bool IsGuardLine(GunSound s) => s==GunSound.GuardFire1 || s==GunSound.GuardThreat2 || s==GunSound.GuardThreat3 || s==GunSound.GuardFire4;
        // A gentle soft limiter lifts quieter detail without samples exceeding full scale.
        static AudioClip Louder(AudioClip source,bool rifleTail=false)
        {
            var data=new float[source.samples*source.channels];if(!source.GetData(data,0))return source;
            int extra=rifleTail?Mathf.CeilToInt(source.frequency*0.20f):0;
            var output=new float[(source.samples+extra)*source.channels];
            if(rifleTail)
            {
                for(int i=0;i<data.Length;i++)
                {
                    int frame=i/source.channels;float fade=Mathf.Clamp01((source.samples-frame)/(source.frequency*0.006f));
                    output[i]+=data[i]*fade;
                    for(int echo=1;echo<=4;echo++)
                    {
                        int delay=Mathf.RoundToInt(source.frequency*0.045f*echo)*source.channels;
                        if(i+delay<output.Length)output[i+delay]+=data[i]*Mathf.Pow(0.3f,echo);
                    }
                }
            }
            else System.Array.Copy(data,output,data.Length);
            for(int i=0;i<output.Length;i++)output[i]=0.98f*(float)System.Math.Tanh(output[i]*1.8f);
            var clip=AudioClip.Create(source.name+" balanced",output.Length/source.channels,source.channels,source.frequency,false);
            clip.SetData(output,0);return clip;
        }
        static AudioClip TrimLeadingSilence(AudioClip source)
        {
            var data = new float[source.samples * source.channels];
            if(!source.GetData(data,0)) return source;
            int first=0;
            while(first<data.Length && Mathf.Abs(data[first])<0.008f) first++;
            // Preserve a tiny attack lead-in and never trim a silent/malformed clip away.
            int offset=Mathf.Max(0,first/source.channels-source.frequency/500);
            if(offset<source.frequency/100 || offset>=source.samples-32) return source;
            int count=(source.samples-offset)*source.channels;
            var samples=new float[count];System.Array.Copy(data,offset*source.channels,samples,0,count);
            var clip=AudioClip.Create(source.name+" immediate",source.samples-offset,source.channels,source.frequency,false);
            clip.SetData(samples,0);return clip;
        }
        public static AudioClip GetClip(GunSound s)
        {
            if (clips.TryGetValue(s, out var c) && c != null) return c;
            string file = s == GunSound.Pistol ? "pistol_fire" : s == GunSound.Shotgun ? "shotgun_fire"
                : s == GunSound.PistolReload || s == GunSound.Reload ? "pistol_reload" : s == GunSound.ShotgunReload ? "shotgun_reload"
                : s == GunSound.HumanHurt1 ? "human_hurt1" : s == GunSound.HumanHurt2 ? "human_hurt2"
                : s == GunSound.M16 ? "m16_fire" : s == GunSound.M16Reload ? "m16_reload"
                : s == GunSound.DogBark1 ? "dog_bark1" : s == GunSound.DogBark2 ? "dog_bark2"
                : s == GunSound.ElephantTrumpet1 ? "elephant_trumpet1" : s == GunSound.ElephantTrumpet2 ? "elephant_trumpet2"
                : s == GunSound.GuardFire1 ? "guard1" : s == GunSound.GuardThreat2 ? "guard2"
                : s == GunSound.GuardThreat3 ? "guard3" : s == GunSound.GuardFire4 ? "guard4"
                : s == GunSound.HumanDeath ? "human_death" : s == GunSound.TigerGrowl ? "tiger_growl" : null;
            if (file != null)
            {
                c = Resources.Load<AudioClip>("Audio/SFX/" + file);
                if (c != null)
                {
                    c.LoadAudioData();
                    if (s == GunSound.Shotgun || s == GunSound.M16) c = TrimLeadingSilence(c);
                    c = Louder(c,s==GunSound.M16);
                    clips[s] = c; return c;
                }
            }
            const int rate = 22050;
            float len; System.Func<float, float> wave;
            var rng = new System.Random((int)s * 977 + 13);
            float Noise() => (float)rng.NextDouble() * 2f - 1f;
            switch (s)
            {
                case GunSound.Pistol:
                    len = 0.35f;
                    wave = t => Noise() * Mathf.Exp(-t * 22f) * 0.9f + Mathf.Sin(t * 2f * Mathf.PI * 110f) * Mathf.Exp(-t * 18f) * 0.6f;
                    break;
                case GunSound.Shotgun:
                    len = 0.6f;
                    wave = t => Noise() * Mathf.Exp(-t * 11f) + Mathf.Sin(t * 2f * Mathf.PI * 70f) * Mathf.Exp(-t * 9f) * 0.8f;
                    break;
                case GunSound.Rifle:
                    len = 0.8f;
                    wave = t => Noise() * Mathf.Exp(-t * 15f) * 0.9f + Mathf.Sin(t * 2f * Mathf.PI * 90f) * Mathf.Exp(-t * 6f) * 0.5f
                                + Noise() * Mathf.Exp(-(t - 0.25f) * (t - 0.25f) * 60f) * 0.15f;   // echo off the hills
                    break;
                case GunSound.Pump:
                    len = 0.3f;
                    wave = t => Noise() * (Mathf.Exp(-t * 60f) + Mathf.Exp(-Mathf.Abs(t - 0.16f) * 70f)) * 0.5f;
                    break;
                case GunSound.Empty:
                    len = 0.08f;
                    wave = t => Noise() * Mathf.Exp(-t * 120f) * 0.4f;
                    break;
                default:   // reload: magazine out, magazine in
                    len = 0.45f;
                    wave = t => Noise() * (Mathf.Exp(-t * 80f) * 0.4f + Mathf.Exp(-Mathf.Abs(t - 0.3f) * 90f) * 0.6f);
                    break;
            }
            int n = Mathf.CeilToInt(len * rate);
            var data = new float[n];
            for (int i = 0; i < n; i++) data[i] = Mathf.Clamp(wave(i / (float)rate), -1f, 1f);
            c = AudioClip.Create("Gun_" + s, n, 1, rate, false);
            c.SetData(data, 0);
            clips[s] = c;
            return c;
        }
    }

    /// <summary>A spark that flies, falls and fades.</summary>
    public class SparkFly : MonoBehaviour
    {
        public Vector3 velocity;
        float age;
        Vector3 startScale;

        void Start() => startScale = transform.localScale;

        void Update()
        {
            age += Time.deltaTime;
            velocity += Physics.gravity * Time.deltaTime;
            transform.position += velocity * Time.deltaTime;
            transform.localScale = startScale * Mathf.Max(0f, 1f - age / 0.35f);
            if (age > 0.35f) Destroy(gameObject);
        }
    }
}
