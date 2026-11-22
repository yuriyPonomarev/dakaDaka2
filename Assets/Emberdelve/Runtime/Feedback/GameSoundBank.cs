using System;
using UnityEngine;

namespace Emberdelve.Feedback
{
    /// <summary>Shared imported Foley. Selection never consumes the gameplay random stream.</summary>
    public static class GameSoundBank
    {
        public const string ResourceRoot = "Emberdelve/Audio/Sfx/";
        private static readonly AudioClip[][] Clips = new AudioClip[Enum.GetValues(typeof(WorldSound)).Length][];
        private static readonly int[] Last = new int[Clips.Length];
        private static uint _random = 0x658ac193;

        public static int VariantCount(WorldSound cue) => cue == WorldSound.Step || cue == WorldSound.StepWood || cue == WorldSound.StepDirt
            ? 6 : cue >= WorldSound.UiHover && cue <= WorldSound.UiError ? 2 : 3;

        public static AudioClip Get(WorldSound cue, int variant)
        {
            int index = (int)cue;
            if (index < 0 || index >= Clips.Length) return null;
            if (Clips[index] == null) Clips[index] = new AudioClip[VariantCount(cue)];
            variant = Mathf.Clamp(variant, 0, Clips[index].Length - 1);
            if (Clips[index][variant] == null)
                Clips[index][variant] = Resources.Load<AudioClip>(ResourceRoot + cue + "_" + (variant + 1).ToString("00"));
            return Clips[index][variant];
        }

        public static AudioClip Next(WorldSound cue)
        {
            int index = (int)cue;
            if (index < 0 || index >= Clips.Length) return null;
            _random ^= _random << 13; _random ^= _random >> 17; _random ^= _random << 5;
            int next = (Last[index] + 1 + (int)(_random % (uint)(VariantCount(cue) - 1))) % VariantCount(cue);
            Last[index] = next;
            return Get(cue, next);
        }
    }
}
