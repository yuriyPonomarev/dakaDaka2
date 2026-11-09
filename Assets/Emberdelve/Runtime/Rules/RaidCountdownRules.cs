namespace Emberdelve.Rules
{
    /// <summary>Pure rules for the configured raid deadline.</summary>
    public static class RaidCountdownRules
    {
        public const int DefaultDurationSeconds = 10 * 60;

        public static int NormalizeDurationSeconds(int durationSeconds) =>
            durationSeconds > 0 ? durationSeconds : DefaultDurationSeconds;

        /// <summary>Elapsed raid time excluding offline pause intervals.</summary>
        public static float ElapsedSeconds(float startedAt, float now, float pausedDurationSeconds,
            float pauseStartedAt, bool isPaused)
        {
            if (!IsFinite(startedAt) || !IsFinite(now) || now < startedAt) return 0f;

            float paused = IsFinite(pausedDurationSeconds) ? System.Math.Max(0f, pausedDurationSeconds) : 0f;
            if (isPaused && IsFinite(pauseStartedAt) && now > pauseStartedAt)
                paused += now - pauseStartedAt;

            return (float)System.Math.Max(0f, now - startedAt - paused);
        }

        public static float RemainingSeconds(int durationSeconds, float elapsedSeconds)
        {
            int duration = NormalizeDurationSeconds(durationSeconds);
            if (float.IsNaN(elapsedSeconds) || elapsedSeconds < 0f) elapsedSeconds = 0f;
            return elapsedSeconds >= duration ? 0f : duration - elapsedSeconds;
        }

        public static bool HasExpired(int durationSeconds, float elapsedSeconds) =>
            RemainingSeconds(durationSeconds, elapsedSeconds) <= 0f;

        public static bool IsExtractionWindowOpen(int durationSeconds, float elapsedSeconds) =>
            !HasExpired(durationSeconds, elapsedSeconds);

        /// <summary>
        /// A locally estimated clock may inform presentation, but only the authority may close extraction.
        /// Clients still send the intent; the server repeats this check against its own raid clock.
        /// </summary>
        public static bool IsExtractionWindowOpen(int durationSeconds, float elapsedSeconds, bool isAuthority) =>
            !isAuthority || IsExtractionWindowOpen(durationSeconds, elapsedSeconds);

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
