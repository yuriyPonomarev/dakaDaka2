using UnityEngine;

namespace Emberdelve.Feedback
{
    /// <summary>
    /// Placeholder sound: short clips synthesised at runtime so the prototype has audio feedback
    /// without shipping any audio assets. Swapping in real samples later means replacing this class
    /// with serialized AudioClip references; FeedbackDirector is the only consumer.
    /// </summary>
    public static class ProceduralAudio
    {
        private const int SampleRate = 44100;

        public static AudioClip SwordSwing() => Build("sfx_swing", 0.18f, 1100f, 240f, 0.55f, 0.05f, 0.18f);
        public static AudioClip Impact() => Build("sfx_impact", 0.14f, 420f, 90f, 0.75f, 0.02f, 0.55f);
        public static AudioClip Block() => Build("sfx_block", 0.2f, 1800f, 700f, 0.35f, 0.01f, 0.4f);
        public static AudioClip BowDraw() => Build("sfx_draw", 0.12f, 300f, 900f, 0.15f, 0.4f, 0.3f);
        public static AudioClip BowRelease() => Build("sfx_bow", 0.24f, 1400f, 320f, 0.25f, 0.02f, 0.5f);
        public static AudioClip Hurt() => Build("sfx_hurt", 0.32f, 300f, 110f, 0.6f, 0.02f, 0.7f);
        public static AudioClip Pickup() => Build("sfx_pickup", 0.22f, 620f, 1180f, 0.05f, 0.1f, 0.35f);
        public static AudioClip DryFire() => Build("sfx_dry", 0.08f, 1600f, 1200f, 0.4f, 0.01f, 0.2f);
        public static AudioClip Extract() => Build("sfx_extract", 1.1f, 320f, 880f, 0.08f, 0.25f, 0.85f);
        public static AudioClip Death() => Build("sfx_death", 1.4f, 240f, 55f, 0.35f, 0.05f, 0.95f);

        private static AudioClip Build(string name, float duration, float startFrequency, float endFrequency,
            float noiseMix, float attackFraction, float sustainFraction)
        {
            int sampleCount = Mathf.Max(1, Mathf.RoundToInt(duration * SampleRate));
            AudioClip clip = AudioClip.Create(name, sampleCount, 1, SampleRate, false);
            float[] samples = new float[sampleCount];

            float phase = 0f;
            float attackSamples = Mathf.Max(1f, attackFraction * sampleCount);
            float sustainEnd = Mathf.Clamp01(sustainFraction) * sampleCount;

            for (int i = 0; i < sampleCount; i++)
            {
                float t = i / (float)sampleCount;
                float frequency = Mathf.Lerp(startFrequency, endFrequency, t);
                phase += 2f * Mathf.PI * frequency / SampleRate;

                float tone = Mathf.Sin(phase);
                float hash = Mathf.Repeat(Mathf.Sin(i * 12.9898f) * 43758.5453f, 1f);
                float noise = hash * 2f - 1f;
                float mixed = Mathf.Lerp(tone, noise, noiseMix);

                float envelope;
                if (i < attackSamples) envelope = i / attackSamples;
                else if (i < sustainEnd) envelope = 1f;
                else envelope = Mathf.Clamp01(1f - (i - sustainEnd) / Mathf.Max(1f, sampleCount - sustainEnd));

                samples[i] = mixed * envelope * 0.45f;
            }

            clip.SetData(samples, 0);
            clip.hideFlags = HideFlags.HideAndDontSave;
            return clip;
        }
    }
}
