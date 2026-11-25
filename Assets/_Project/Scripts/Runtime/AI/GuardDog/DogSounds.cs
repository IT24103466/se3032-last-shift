using UnityEngine;

namespace LastShift.GuardDog
{
    /// <summary>Supplied dog barks/hurt cries, with generated growl and whimper accents.</summary>
    public static class DogSounds
    {
        const int Rate = 22050;
        static AudioClip growl, whimper;

        public static AudioClip Bark => GunFX.GetClip(Random.value < 0.5f ? GunSound.DogBark1 : GunSound.DogBark2);
        public static AudioClip Growl => growl ??= Make("Dog_Growl", 0.9f, GrowlWave);
        public static AudioClip Whimper => whimper ??= Make("Dog_Whimper", 0.6f, WhimperWave);
        public static AudioClip Yelp => Bark;

        static readonly System.Random rng = new System.Random(24103466);
        static float Noise() => (float)rng.NextDouble() * 2f - 1f;

        // Two short "woofs": a falling harmonic tone with a breathy burst on top
        static float BarkWave(float t)
        {
            float local = t < 0.25f ? t : t - 0.28f;
            if (local < 0f || local > 0.2f) return 0f;
            float env = Mathf.Sin(Mathf.Clamp01(local / 0.2f) * Mathf.PI) * Mathf.Exp(-local * 9f);
            float f = Mathf.Lerp(520f, 300f, local / 0.2f);
            float ph = 2f * Mathf.PI * f * local;
            float tone = Mathf.Sin(ph) + 0.6f * Mathf.Sin(2f * ph) + 0.35f * Mathf.Sin(3f * ph) + 0.2f * Mathf.Sin(5f * ph);
            return (tone * 0.45f + Noise() * 0.35f) * env * 1.4f;
        }

        // Low rumble with a fast amplitude flutter
        static float GrowlWave(float t)
        {
            float env = Mathf.Sin(Mathf.Clamp01(t / 0.9f) * Mathf.PI);
            float flutter = 0.6f + 0.4f * Mathf.Sin(2f * Mathf.PI * 26f * t);
            float ph = 2f * Mathf.PI * 85f * t;
            return (Mathf.Sin(ph) * 0.5f + Mathf.Sin(2.02f * ph) * 0.3f + Noise() * 0.35f) * flutter * env * 0.8f;
        }

        // Thin rising-and-falling whine
        static float WhimperWave(float t)
        {
            float f = 900f + 450f * Mathf.Sin(Mathf.Clamp01(t / 0.6f) * Mathf.PI);
            float env = Mathf.Sin(Mathf.Clamp01(t / 0.6f) * Mathf.PI);
            return Mathf.Sin(2f * Mathf.PI * f * t) * env * 0.35f;
        }

        // Sharp high cry when hurt
        static float YelpWave(float t)
        {
            float f = Mathf.Lerp(1300f, 650f, t / 0.3f);
            float env = Mathf.Exp(-t * 10f) * Mathf.Clamp01(t * 80f);
            float ph = 2f * Mathf.PI * f * t;
            return (Mathf.Sin(ph) + 0.4f * Mathf.Sin(2f * ph) + Noise() * 0.2f) * env * 0.7f;
        }

        static AudioClip Make(string name, float length, System.Func<float, float> wave, bool normalize = false)
        {
            int n = Mathf.CeilToInt(length * Rate);
            var data = new float[n];
            for (int i = 0; i < n; i++) data[i] = Mathf.Clamp(wave(i / (float)Rate), -1f, 1f);
            if (normalize)
            {
                // Bring the loudest sample up to full scale so the bark carries across the estate
                float peak = 0f;
                for (int i = 0; i < n; i++) peak = Mathf.Max(peak, Mathf.Abs(data[i]));
                if (peak > 0.001f) for (int i = 0; i < n; i++) data[i] *= 0.95f / peak;
            }
            var clip = AudioClip.Create(name, n, 1, Rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
