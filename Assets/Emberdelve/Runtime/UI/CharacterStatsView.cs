using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Emberdelve.Combat;
using Emberdelve.Data;
using Emberdelve.Movement;
using Emberdelve.Player;
using Emberdelve.Stats;

namespace Emberdelve.UI
{
    /// <summary>
    /// The character pane's read model: the six live ability scores with their signed modifiers, the
    /// live pools with the regeneration rates actually configured on them, the guard the kit in hand
    /// really resolves, the ordinary walking speed, the equipped weapon's reference damage before any
    /// target defence, and the charisma scale the skill path applies.
    ///
    /// It is presentation only and deliberately not a component: it owns Text widgets, reads the rig
    /// through the same public resolvers gameplay uses, and writes nothing anywhere. Every number here
    /// is a query - <see cref="StatsComponent"/> for the scores and the ability scale, the pool
    /// components for current/ceiling/rate, <see cref="PlayerMovement.ResolveWalkSpeed"/> for speed,
    /// <see cref="PlayerRig.GetOutgoingDamageMultiplier"/> for the weapon line and
    /// <see cref="PlayerRig.Defense"/> for the guard - so the pane cannot disagree with what the run is
    /// actually doing, and it cannot invent a stat the rig does not have.
    ///
    /// The rows are built once by <see cref="Build"/> and afterwards only re-labelled by
    /// <see cref="Refresh"/>, which walks the identical sequence of lines. Opening the pane again, or
    /// refreshing it every frame while it is open, therefore never creates a second copy of a line.
    /// </summary>
    public sealed class CharacterStatsView
    {
        public const int SectionCount = 4;
        public const int AttributeRowCount = AttributeRules.AttributeCount;
        public const int VitalRowCount = 2;   // D1b: Health and Mana (the deprecated Stamina row was removed)
        public const int GuardRowCount = 2;
        public const int SpeedRowCount = 3;
        public const int LineCount = SectionCount + AttributeRowCount + VitalRowCount + GuardRowCount + SpeedRowCount;

        public const float SectionHeight = 21f;
        public const float RowHeight = 18f;
        public const float LabelWidth = 168f;

        /// <summary>The vertical extent the lines need. The host sizes its pane from this, not from a guess.</summary>
        public const float PreferredHeight = SectionCount * SectionHeight
                                             + (AttributeRowCount + VitalRowCount + GuardRowCount + SpeedRowCount) * RowHeight;

        private const int SectionFontSize = 15;
        private const int RowFontSize = 14;

        private static readonly Color LabelColor = new Color(0.62f, 0.58f, 0.52f, 1f);
        private static readonly Color ValueColor = new Color(0.9f, 0.86f, 0.8f, 1f);
        private static readonly Color SectionColor = new Color(0.84f, 0.72f, 0.5f, 1f);

        private readonly RectTransform _root;
        private readonly List<Line> _sections = new List<Line>(SectionCount);
        private readonly List<Line> _rows = new List<Line>(LineCount);
        private readonly float _width;

        private PlayerRig _rig;

        /// <summary>
        /// Which already-built section / row the current line of the sequence labels. Build appends in
        /// order and Refresh walks the identical order against the widgets that already exist, so each
        /// pass needs its own cursor per list - counting the list instead would aim every refresh at one
        /// index past the end and leave every line showing whatever the first pass wrote.
        /// </summary>
        private int _sectionCursor;
        private int _rowCursor;
        private float _y;
        private bool _building;

        /// <summary>Lines the last pass produced. A caller can assert a reopened pane did not grow.</summary>
        public int RowCount => _rows.Count;
        public int SectionLineCount => _sections.Count;

        public CharacterStatsView(Transform parent, float width)
        {
            _root = UiFactory.CreateGroup(parent, "CharacterRows");
            UiFactory.SetRect(_root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            _width = Mathf.Max(1f, width);
        }

        /// <summary>Re-points the pane at the rig the run is playing. Reads only; touches no item or pool state.</summary>
        public void Configure(PlayerRig rig)
        {
            _rig = rig;
        }

        public void Build()
        {
            _building = true;
            _sectionCursor = 0;
            _rowCursor = 0;
            _y = 0f;
            Populate();
            _building = false;
        }

        /// <summary>Re-labels the lines that already exist from the live rig. Safe to call every frame.</summary>
        public void Refresh()
        {
            if (_rows.Count == 0 && _sections.Count == 0)
            {
                Build();
                return;
            }

            _building = false;
            _sectionCursor = 0;
            _rowCursor = 0;
            _y = 0f;
            Populate();
        }

        /// <summary>
        /// The one line sequence. Build and Refresh run the same calls in the same order, which is what
        /// keeps a reopened or re-refreshed pane from ever holding two lines for the same number.
        /// </summary>
        private void Populate()
        {
            StatsComponent attributes = _rig != null ? _rig.AttributeStats : null;
            bool ready = attributes != null && attributes.IsReady;

            Section("ABILITY SCORES   effective score  (modifier)");

            // Always the same six lines, ready or not: the sequence Build and Refresh walk has to be one
            // sequence, or a pane built before a class was applied would label its next pass onto the
            // wrong lines.
            AttributeBonuses bonuses = ready ? attributes.EquippedBonuses : AttributeBonuses.None;

            for (int i = 0; i < AttributeRules.AttributeCount; i++)
            {
                SixAttribute attribute = (SixAttribute)i;
                string name = AttributeRules.Key(attribute).ToUpperInvariant();

                if (!ready)
                {
                    Row(name, "no class applied yet");
                    continue;
                }

                int score = attributes.GetAttribute(attribute);
                int modifier = attributes.GetModifier(attribute);
                int bonus = bonuses.Total(attribute);

                string value = score + "  (" + Signed(modifier) + ")";
                if (bonus != 0) value += "   worn " + Signed(bonus);

                Row(name, value);
            }

            Section("VITALS   current / maximum");
            Row("Health", ready ? Pool(_rig.Health) : "-");
            Row("Mana", ready ? Pool(_rig.Mana) : "-");
            // D1b: the Stamina row is gone (stamina no longer gates or drains anything; the pool stays as deprecated data).

            Section("GUARD");
            Row("Guard", ready ? DescribeGuard(_rig.Defense) : "nothing held");
            Row("Armour", ready ? DescribeArmor(_rig.GetArmorRating()) : "-");

            Section("SPEED, WEAPON AND SKILLS");
            Row("Walk speed", ready && _rig.Movement != null ? _rig.Movement.ResolveWalkSpeed().ToString("0.00") + " m/s" : "-");
            Row("Weapon damage", ready ? DescribeWeapon() : "-");
            Row("Skill power", ready ? DescribeSkillPower(attributes) : "-");
        }

        // ------------------------------------------------------------ lines

        private void Section(string title)
        {
            if (_building)
            {
                Text text = CreateText("Section" + _sections.Count, title, SectionFontSize, SectionColor, FontStyle.Bold);
                Place(text, 0f, _width, SectionHeight);
                _sections.Add(new Line { Text = text });
            }
            else if (_sectionCursor < _sections.Count)
            {
                _sections[_sectionCursor].Text.text = title;
            }

            _sectionCursor++;
            _y += SectionHeight;
        }

        private void Row(string label, string value)
        {
            if (_building)
            {
                Line line = new Line
                {
                    Label = CreateText("StatLabel" + _rows.Count, label, RowFontSize, LabelColor, FontStyle.Normal),
                    Text = CreateText("StatValue" + _rows.Count, value, RowFontSize, ValueColor, FontStyle.Normal),
                };

                Place(line.Label, 0f, LabelWidth, RowHeight);
                Place(line.Text, LabelWidth, _width - LabelWidth, RowHeight);
                _rows.Add(line);
            }
            else if (_rowCursor < _rows.Count)
            {
                _rows[_rowCursor].Label.text = label;
                _rows[_rowCursor].Text.text = value;
            }

            _rowCursor++;
            _y += RowHeight;
        }

        /// <summary>
        /// Lines descend from the group's origin, which the host parks at the pane's top-left corner. The
        /// running <see cref="_y"/> is the only vertical bookkeeping, so a line can never land on the one
        /// before it and the pane's own height is the only thing that can clip one.
        /// </summary>
        private void Place(Text text, float x, float width, float height)
        {
            RectTransform rect = text.rectTransform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(Mathf.Max(1f, width), height);
            rect.anchoredPosition = new Vector2(x, -_y);
        }

        private Text CreateText(string name, string content, int fontSize, Color color, FontStyle style)
        {
            Text text = UiFactory.CreateText(_root, name, content, fontSize, color, TextAnchor.UpperLeft, style);
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false;
            return text;
        }

        // ------------------------------------------------------------ wording

        private string DescribeWeapon()
        {
            WeaponData weapon = _rig.Loadout != null ? _rig.Loadout.ActiveWeapon : null;
            if (weapon == null) return "nothing in hand";

            // The two numbers the authority combines before any target defence: the weapon asset's own
            // base damage, and the single outgoing multiplier it reads from this rig (attribute factor,
            // passive/gear factor and any temporary buff - each exactly once).
            float outgoing = Mathf.Max(0f, _rig.GetOutgoingDamageMultiplier(weapon));
            float reference = weapon.baseDamage * outgoing;

            return $"{ShortName(weapon.displayName)}  {weapon.baseDamage:0.#} -> {reference:0.#} before armour";
        }

        /// <summary>A name long enough to push the numbers out of the row is cut, the way the hotbar cuts it.</summary>
        private static string ShortName(string name)
        {
            name = name ?? "Weapon";
            if (name.Length <= 22) return name;
            return name.Substring(0, 22).TrimEnd() + "...";
        }

        private static string DescribeSkillPower(StatsComponent attributes)
        {
            return $"{attributes.GetAbilityEffectScale():0.##}x  Charisma scales skill effects";
        }

        private static string Signed(int value)
        {
            return value >= 0 ? "+" + value : value.ToString();
        }

        private static string Pool(HealthComponent health)
        {
            return health == null ? "-" : Pool(health.CurrentHealth, health.MaxHealth, health.RegenPerSecond);
        }

        private static string Pool(ManaComponent mana)
        {
            return mana == null ? "-" : Pool(mana.CurrentMana, mana.MaxMana, mana.RegenPerSecond);
        }

        private static string Pool(StaminaComponent stamina)
        {
            return stamina == null ? "-" : Pool(stamina.CurrentStamina, stamina.MaxStamina, stamina.RegenPerSecond);
        }

        private static string Pool(float current, float max, float regen)
        {
            return Mathf.CeilToInt(current) + " / " + Mathf.CeilToInt(max) + "   regen " + Rate(regen);
        }

        private static string Rate(float regen)
        {
            return regen > 0f ? regen.ToString("0.##") + "/s" : "none";
        }

        /// <summary>
        /// The armour rating the combat authority reads from this rig (fixed + rolled points of everything worn,
        /// cloak included) and the share of Physical damage it removes, 100 / (100 + rating) per ARMOR_RULES.md.
        /// </summary>
        private static string DescribeArmor(float rating)
        {
            if (!(rating > 0f)) return "0";
            float reduction = rating / (100f + rating) * 100f;
            return rating.ToString("0.##") + "   -" + reduction.ToString("0.#") + "% physical";
        }

        private static string DescribeGuard(DefenseProfile defense)
        {
            if (!defense.CanBlock) return "no block with the kit in hand";

            string text = "blocks " + Mathf.RoundToInt(defense.BlockReduction * 100f) + "% within "
                          + Mathf.RoundToInt(defense.BlockArcDegrees) + " degrees";

            return defense.HasShield ? text + "   shield" : text;
        }

        private sealed class Line
        {
            public Text Label;
            public Text Text;
        }
    }
}
