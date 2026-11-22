using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Rules;

namespace Emberdelve.UI
{
    /// <summary>
    /// Class-select screen for the lobby. Pure presentation: it reports clicks and lets
    /// LobbyController decide what a selection means.
    /// </summary>
    public sealed class LobbyScreenView
    {
        private readonly Dictionary<CharacterClassData, Image> _cardBackgrounds = new Dictionary<CharacterClassData, Image>();
        private readonly Dictionary<CharacterClassData, Text> _statLabels = new Dictionary<CharacterClassData, Text>();
        private CharacterClassData _selected;
        private Text _lastResultLabel;
        private readonly Dictionary<RoyalePartyMode, UnityEngine.UI.Button> _modes = new Dictionary<RoyalePartyMode, UnityEngine.UI.Button>();
        private UnityEngine.UI.Text _modeDescription;
        public void SetMode(RoyalePartyMode mode)
        {
            foreach (var pair in _modes) pair.Value.targetGraphic.color = pair.Key == mode
                ? new Color(.55f,.28f,.1f,1) : new Color(.15f,.13f,.1f,1);
            if (_modeDescription != null) _modeDescription.text = (mode == RoyalePartyMode.Trio ? "15 players / 5 teams" : mode == RoyalePartyMode.Duo ? "16 players / 8 teams" : "16 players / every player for themselves")
                + "\nOffline: bots fill the match";
        }

        public void Build(Canvas canvas, IReadOnlyList<CharacterClassData> classes,
            System.Action<CharacterClassData> onSelected, System.Action onStart, RunResult lastResult, bool hasResult, System.Action<RoyalePartyMode> onModeSelected = null, System.Action onSkills = null)
        {
            RectTransform root = UiFactory.CreateGroup(canvas.transform, "LobbyRoot");
            root.sizeDelta = new Vector2(1440,800);
            root.gameObject.AddComponent<LobbyLayoutScale>();

            UiFactory.CreateImage(root, "Backdrop", new Color(0.03f, 0.022f, 0.016f, .45f),
                new Vector2(2400f, 1400f), Vector2.zero, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));

            Text title = UiFactory.CreateText(root, "Title", "DAKADAKA", 44, new Color(0.95f, 0.78f, 0.4f, 1f), TextAnchor.MiddleCenter);
            UiFactory.SetRect(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(1200f, 80f), new Vector2(0f, -48f));
            DungeonUiStyle.Heading(title);

            Text subtitle = UiFactory.CreateText(root, "Subtitle", "SQUIRE ROYALE  /  Three floors. Base gear. One final escape.",
                16, new Color(0.8f, 0.75f, 0.66f, 1f), TextAnchor.MiddleCenter);
            UiFactory.SetRect(subtitle.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(680f, 40f), new Vector2(0f, -99f));

            var modePanel = UiFactory.CreateGroup(root, "RoyaleModePanel");
            UiFactory.SetRect(modePanel, new Vector2(0,1), new Vector2(0,1), new Vector2(340,176), new Vector2(40,-444), new Vector2(0,1));
            var modeBackground = UiFactory.CreateImage(modePanel,"Background",new Color(.05f,.04f,.03f,.95f),new Vector2(340,176),new Vector2(170,-88),new Vector2(0,1),new Vector2(0,1));
            DungeonUiStyle.Surface(modeBackground);
            var modeTitle = UiFactory.CreateText(modePanel,"Heading","TEAM SIZE",17,new Color(.95f,.78f,.4f),TextAnchor.MiddleCenter);
            UiFactory.SetRect(modeTitle.rectTransform,new Vector2(.5f,1),new Vector2(.5f,1),new Vector2(320,26),new Vector2(0,-22));
            for (int i = 1; i <= 3; i++)
            {
                var mode = (RoyalePartyMode)i;
                var button = UiFactory.CreateButton(modePanel,"Mode" + mode,mode.ToString().ToUpperInvariant(),()=>onModeSelected?.Invoke(mode),
                    new Vector2(100,38),new Color(.15f,.13f,.1f),Color.white,17);
                UiFactory.SetRect(button.GetComponent<RectTransform>(),new Vector2(.5f,1),new Vector2(.5f,1),new Vector2(100,38),new Vector2((i-2)*108,-66));
                _modes[mode] = button;
            }
            _modeDescription = UiFactory.CreateText(modePanel,"Description","",14,new Color(.8f,.75f,.66f),TextAnchor.MiddleCenter);
            UiFactory.SetRect(_modeDescription.rectTransform,new Vector2(.5f,1),new Vector2(.5f,1),new Vector2(320,60),new Vector2(0,-125));
            SetMode(RunContext.SelectedRoyaleMode ?? RoyalePartyMode.Solo);

            if (hasResult)
            {
                _lastResultLabel = UiFactory.CreateText(root, "LastRun",
                    $"Last run: {lastResult.Outcome}   {lastResult.ClassName}   value {Mathf.RoundToInt(lastResult.LootValue)}   slain {lastResult.EnemiesSlain}   gear score {lastResult.GearScore}",
                    18, new Color(0.65f, 0.72f, 0.62f, 1f), TextAnchor.MiddleCenter);
                UiFactory.SetRect(_lastResultLabel.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                    new Vector2(1200f, 34f), new Vector2(180f, -655f));
            }

            int count = Mathf.Max(1, classes.Count);
            const float cardWidth = 400f;
            // Shared 1440x800 reference layout leaves separate space for connection settings and departure.
            const float cardHeight = 430f;
            float gap = 24f;
            float totalWidth = count * cardWidth + (count - 1) * gap;

            for (int i = 0; i < classes.Count; i++)
            {
                CharacterClassData classData = classes[i];
                if (classData == null) continue;

                float x = 180f -totalWidth * 0.5f + cardWidth * (i + 0.5f) + gap * i;

                Image card = UiFactory.CreateImage(root, "Card_" + classData.classId, new Color(0.09f, 0.075f, 0.06f, 0.95f),
                    new Vector2(cardWidth, cardHeight), new Vector2(x, -15f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));

                DungeonUiStyle.Surface(card);
                Button button = card.gameObject.AddComponent<Button>();
                button.targetGraphic = card;
                ColorBlock colors = button.colors;
                colors.normalColor = Color.white;
                colors.highlightedColor = new Color(1.15f, 1.1f, 1.0f, 1f);
                colors.selectedColor = Color.white;
                button.colors = colors;
                button.onClick.AddListener(() => onSelected?.Invoke(classData));

                UiFactory.CreateImage(card.transform, "Accent", classData.accentColor, new Vector2(cardWidth - 24f, 2f),
                    new Vector2(0f, cardHeight * 0.5f - 10f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));

                Text name = UiFactory.CreateText(card.transform, "Name", classData.displayName, 32,
                    new Color(0.96f, 0.9f, 0.8f, 1f), TextAnchor.MiddleCenter);
                UiFactory.SetRect(name.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                    new Vector2(cardWidth - 40f, 44f), new Vector2(0f, -48f));
                DungeonUiStyle.Heading(name);

                Text description = UiFactory.CreateText(card.transform, "Description", classData.description, 16,
                    new Color(0.78f, 0.74f, 0.66f, 1f), TextAnchor.UpperCenter);
                description.horizontalOverflow = HorizontalWrapMode.Wrap;
                description.verticalOverflow = VerticalWrapMode.Truncate;
                UiFactory.SetRect(description.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                    new Vector2(cardWidth - 48f, 96f), new Vector2(0f, -124f));

                var portrait = UiFactory.CreateGroup(card.transform, "ClassPortrait");
                portrait.sizeDelta = new Vector2(150,225);
                portrait.anchoredPosition = new Vector2(-112,-65);
                portrait.gameObject.AddComponent<LobbyClassPortrait>().Build(classData, i);
                Text stats = UiFactory.CreateText(card.transform, "Stats", BuildStatsText(classData), 14,
                    new Color(0.9f, 0.85f, 0.72f, 1f), TextAnchor.MiddleLeft);
                stats.horizontalOverflow = HorizontalWrapMode.Wrap;
                UiFactory.SetRect(stats.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    new Vector2(212f, 210f), new Vector2(85f, -65f));

                _cardBackgrounds[classData] = card;
                _statLabels[classData] = stats;
            }

            Button descend = UiFactory.CreateButton(root, "DescendButton", "PREPARE RAID", () => onStart?.Invoke(),
                new Vector2(300f, 62f), new Color(0.32f, 0.12f, 0.075f, 1f), new Color(1f, 0.95f, 0.85f, 1f), 24);
            UiFactory.SetRect(((Image)descend.targetGraphic).rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(300f, 62f), new Vector2(180f, 82f));

            if (onSkills != null)
            {
                Button skills = UiFactory.CreateButton(root, "SkillsButton", "PERKS & SKILLS", () => onSkills(),
                    new Vector2(300f, 62f), new Color(0.15f, 0.13f, 0.1f, 1f), new Color(1f, 0.95f, 0.85f, 1f), 24);
                UiFactory.SetRect(((Image)skills.targetGraphic).rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                    new Vector2(300f, 62f), new Vector2(-150f, 82f));
            }

            Text controls = UiFactory.CreateText(root, "Controls",
                "WASD move  |  Mouse look  |  Shift sprint  |  Ctrl crouch  |  Space jump\n" +
                "Left mouse attack / draw  |  Right mouse block (Warrior)  |  F interact  |  Q/E abilities  |  1/2 weapons  |  Hold 3/4 quick wheels  |  LMB use held item  |  Esc pause",
                13, new Color(0.62f, 0.6f, 0.55f, 1f), TextAnchor.MiddleCenter);
            UiFactory.SetRect(controls.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(1400f, 60f), new Vector2(0f, 30f));
        }

        private static string BuildStatsText(CharacterClassData data)
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.AppendLine($"Health  {data.maxHealth}");
            builder.AppendLine($"Move  {data.moveSpeed:F1} m/s   Sprint {data.sprintSpeed:F1}");
            builder.AppendLine($"Weapon  {(data.primaryWeapon != null ? data.primaryWeapon.displayName : "None")}");

            if (data.primaryWeapon != null)
            {
                if (data.primaryWeapon.IsRanged)
                {
                    builder.AppendLine($"Damage  {data.primaryWeapon.baseDamage:F0} (drawn)   Mag {data.primaryWeapon.magazineSize}");
                    builder.AppendLine($"Reserve capacity  {data.maxReserveAmmo}");
                }
                else
                {
                    builder.AppendLine($"Damage  {data.primaryWeapon.baseDamage:F0}   Reach {data.primaryWeapon.reach:F1} m");
                }
            }

            builder.Append(data.CanGuard ? $"Guard  -{Mathf.RoundToInt(data.blockReduction * 100f)}% damage" : "No guard");
            return builder.ToString();
        }

        public void SetSelected(CharacterClassData selected)
        {
            _selected = selected;

            foreach (KeyValuePair<CharacterClassData, Image> pair in _cardBackgrounds)
            {
                bool active = pair.Key == _selected;
                Color accent = pair.Key.accentColor;
                pair.Value.color = active
                    ? new Color(Mathf.Lerp(0.09f, accent.r, 0.14f), Mathf.Lerp(0.075f, accent.g, 0.14f), Mathf.Lerp(0.06f, accent.b, 0.12f), 1f)
                    : new Color(0.09f, 0.075f, 0.06f, 0.95f);
            }
        }

        public CharacterClassData Selected => _selected;
    }
}
