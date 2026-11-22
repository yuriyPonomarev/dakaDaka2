using UnityEngine;

namespace Emberdelve.Feedback
{
    /// <summary>
    /// Short clips synthesised at runtime for interface and world feedback.
    /// </summary>
    public static class ProceduralAudio
    {
        private const int SampleRate = 44100;

        public static AudioClip SwordSwing() => Noise("sfx_swing", .24f, .22f, 0f);
        public static AudioClip Impact() => Noise("sfx_impact", .18f, .07f, 95f);
        public static AudioClip Block() => Build("sfx_block", 0.2f, 1800f, 700f, 0.35f, 0.01f, 0.4f);
        public static AudioClip Parry() => Build("sfx_parry", .32f, 3100f, 2040f, .12f, .006f, .16f);
        public static AudioClip BowDraw() => Build("sfx_draw", 0.12f, 300f, 900f, 0.15f, 0.4f, 0.3f);
        public static AudioClip BowRelease() => Build("sfx_bow", 0.24f, 1400f, 320f, 0.25f, 0.02f, 0.5f);
        public static AudioClip Hurt() => Build("sfx_hurt", 0.32f, 300f, 110f, 0.6f, 0.02f, 0.7f);
        public static AudioClip Pickup() => Build("sfx_pickup", 0.22f, 620f, 1180f, 0.05f, 0.1f, 0.35f);
        public static AudioClip DryFire() => Build("sfx_dry", 0.08f, 1600f, 1200f, 0.4f, 0.01f, 0.2f);
        public static AudioClip Extract() => Build("sfx_extract", 1.1f, 320f, 880f, 0.08f, 0.25f, 0.85f);
        public static AudioClip Death() => Build("sfx_death", 1.4f, 240f, 55f, 0.35f, 0.05f, 0.95f);

        public static AudioClip StoneStep() => Noise("sfx_stone_step", .16f, .12f, 72f);

        public static AudioClip DoorCreak()
        {
            var samples = new float[Mathf.RoundToInt(.41f * SampleRate)];
            uint seed = 0x1B6957A3u;
            float phase = 0f;
            float grain = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                float turn = i / (samples.Length - 1f);
                float strain = Mathf.Sin(Mathf.PI * turn);
                float pulse = Mathf.Sin(24f * turn + 1.4f * Mathf.Sin(9f * turn));
                float frequency = 180f + 154f * strain + 27f * pulse + 21f * grain;
                phase += 2f * Mathf.PI * frequency / SampleRate;
                grain += (NextNoise(ref seed) - grain) * .17f;

                // The hinge catches several times as it turns. Uneven harmonics give the scrape its texture.
                float chatter = .29f + .71f * Mathf.Pow(Mathf.Abs(pulse), 2.6f);
                float scrape = .41f * Mathf.Sin(phase + .44f * Mathf.Sin(phase * 2f))
                    + .14f * Mathf.Sin(phase * 3f + .6f) + .09f * Mathf.Sin(phase * 5f) + .36f * grain;
                float envelope = Mathf.SmoothStep(0f, 1f, turn / .09f)
                    * Mathf.SmoothStep(0f, 1f, (1f - turn) / .23f);
                samples[i] = scrape * (.3f + .7f * strain) * chatter * envelope;
            }
            return ClipFromSamples("sfx_door_creak", samples);
        }

        public static AudioClip DoorThud()
        {
            var samples = new float[Mathf.RoundToInt(.19f * SampleRate)];
            uint seed = 0x74622CA9u;
            float dust = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                float time = i / (float)SampleRate;
                dust += (NextNoise(ref seed) - dust) * .067f;
                float body = .58f * Mathf.Sin(2f * Mathf.PI * (91f - 94f * time) * time) * Mathf.Exp(-time * 30f);
                float weight = .20f * Mathf.Sin(2f * Mathf.PI * 169f * time) * Mathf.Exp(-time * 56f);
                float latchTime = Mathf.Max(0f, time - .023f);
                float latch = .16f * Mathf.Sin(2f * Mathf.PI * 247f * latchTime) * Mathf.Exp(-latchTime * 81f);
                float envelope = Mathf.Clamp01(time * 700f) * Mathf.Clamp01((.19f - time) * 180f);
                samples[i] = (body + weight + latch + dust * .58f * Mathf.Exp(-time * 46f)) * envelope;
            }
            return ClipFromSamples("sfx_door_thud", samples);
        }

        private static AudioClip Noise(string name, float duration, float smoothing, float bodyFrequency)
        {
            int count = Mathf.RoundToInt(duration * SampleRate);
            var samples = new float[count];
            uint seed = 0x31A7B29u;
            float filtered = 0f;
            for (int i = 0; i < count; i++)
            {
                float noise = NextNoise(ref seed);
                filtered += (noise - filtered) * smoothing;
                float t = i / (float)count;
                float envelope = Mathf.Min(1f, t * 45f) * Mathf.Pow(1f - t, 2f);
                float body = bodyFrequency > 0f ? Mathf.Sin(2f * Mathf.PI * bodyFrequency * i / SampleRate) * .22f : 0f;
                samples[i] = Mathf.Clamp((filtered * 2.2f + body) * envelope, -.9f, .9f);
            }
            return ClipFromSamples(name, samples);
        }

        private static AudioClip Build(string name, float duration, float startFrequency, float endFrequency,
            float noiseMix, float attackFraction, float sustainFraction)
        {
            int sampleCount = Mathf.Max(1, Mathf.RoundToInt(duration * SampleRate));
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

            return ClipFromSamples(name, samples);
        }

        private static float NextNoise(ref uint seed)
        {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            return (seed & 65535) / 32767.5f - 1f;
        }

        private static AudioClip ClipFromSamples(string name, float[] samples)
        {
            var clip = AudioClip.Create(name, samples.Length, 1, SampleRate, false);
            clip.SetData(samples, 0);
            clip.hideFlags = HideFlags.HideAndDontSave;
            return clip;
        }
    }
}
