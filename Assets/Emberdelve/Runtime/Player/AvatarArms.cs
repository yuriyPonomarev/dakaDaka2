namespace Emberdelve.Player
{
    /// <summary>
    /// The arm layer of a class body's animator (other players and rival bots): an override layer masked to the arms and fingers, so
    /// the legs keep their speed-matched walk or run while the arms hold a weapon stance or a guard. Built by the editor
    /// (EmberdelveAvatarArmLayer); a controller without the layer keeps the old whole-body states.
    /// </summary>
    public static class AvatarArms
    {
        public const string Layer = "Arms";
        /// <summary>No arm pose of its own: the layer is faded out and the base layer shows through.</summary>
        public const string Free = "Free";
        /// <summary>A melee weapon carried ready.</summary>
        public const string Armed = "Armed";
        /// <summary>Guard with the weapon alone.</summary>
        public const string Guard = "Guard";
        /// <summary>Guard behind a shield.</summary>
        public const string ShieldGuard = "ShieldGuard";
        /// <summary>Seconds of every cross-fade between arm states, and the rate (1/s) the layer fades in and out.</summary>
        public const float Fade = 0.15f, WeightRate = 8f;
    }
}
