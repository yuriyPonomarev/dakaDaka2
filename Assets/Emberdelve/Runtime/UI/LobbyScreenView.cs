using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Emberdelve.Core;
using Emberdelve.Data;

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
        private Text _gearScoreLabel;

        public void Build(Canvas canvas, IReadOnlyList<CharacterClassData> classes,
            System.Action<CharacterClassData> onSelected, System.Action onStart, RunResult lastResult, bool hasResult)
        {
            RectTransform root = UiFactory.CreateGroup(canvas.transform, "LobbyRoot");
            UiFactory.Stretch(root);

            UiFactory.CreateImage(root, "Backdrop", new Color(0.03f, 0.022f, 0.016f, 1f),
                new Vector2(2400f, 1400f), Vector2.zero, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));

            Text title = UiFactory.CreateText(root, "Title", "EMBERDELVE", 58, new Color(0.95f, 0.78f, 0.4f, 1f), TextAnchor.MiddleCenter);
            UiFactory.SetRect(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(1200f, 80f), new Vector2(0f, -80f));

            Text subtitle = UiFactory.CreateText(root, "Subtitle", "Choose a class and descend into the Modular Crypts.",
                20, new Color(0.8f, 0.75f, 0.66f, 1f), TextAnchor.MiddleCenter);
            UiFactory.SetRect(subtitle.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(1200f, 40f), new Vector2(0f, -150f));

            if (hasResult)
            {
                _lastResultLabel = UiFactory.CreateText(root, "LastRun",
                    $"Last run: {lastResult.Outcome}   {lastResult.ClassName}   value {Mathf.RoundToInt(lastResult.LootValue)}   slain {lastResult.EnemiesSlain}",
                    18, new Color(0.65f, 0.72f, 0.62f, 1f), TextAnchor.MiddleCenter);
                UiFactory.SetRect(_lastResultLabel.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                    new Vector2(1200f, 34f), new Vector2(0f, -190f));
            }

            // D3a: gear score, display only. The lobby has no persistent stash yet, so the number is the score of
            // what the player wore when the last run ended (RunResult.GearScore); before any run it is 0.
            int gearScore = hasResult ? lastResult.GearScore : 0;
            _gearScoreLabel = UiFactory.CreateText(root, "GearScore", Emberdelve.Items.GearScore.Label(gearScore), 22,
                new Color(0.95f, 0.78f, 0.4f, 1f), TextAnchor.MiddleRight);
            UiFactory.SetRect(_gearScoreLabel.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(360f, 36f), new Vector2(-200f, -40f));

            int count = Mathf.Max(1, classes.Count);
            const float cardWidth = 420f;
            // 380 tall and 36px below centre: at 1280x720 the cards span y 206..586 and stop 13px above the DESCEND
            // button (y 599..661); the previous 420px cards ran to y 630 and sat under it.
            const float cardHeight = 380f;
            float gap = 40f;
            float totalWidth = count * cardWidth + (count - 1) * gap;

            for (int i = 0; i < classes.Count; i++)
            {
                CharacterClassData classData = classes[i];
                if (classData == null) continue;

                float x = -totalWidth * 0.5f + cardWidth * (i + 0.5f) + gap * i;

                Image card = UiFactory.CreateImage(root, "Card_" + classData.classId, new Color(0.09f, 0.075f, 0.06f, 0.95f),
                    new Vector2(cardWidth, cardHeight), new Vector2(x, -36f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));

                Button button = card.gameObject.AddComponent<Button>();
                button.targetGraphic = card;
                ColorBlock colors = button.colors;
                colors.normalColor = Color.white;
                colors.highlightedColor = new Color(1.15f, 1.1f, 1.0f, 1f);
                colors.selectedColor = Color.white;
                button.colors = colors;
                button.onClick.AddListener(() => onSelected?.Invoke(classData));

                UiFactory.CreateImage(card.transform, "Accent", classData.accentColor, new Vector2(cardWidth, 8f),
                    new Vector2(0f, cardHeight * 0.5f - 4f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));

                Text name = UiFactory.CreateText(card.transform, "Name", classData.displayName, 32,
                    new Color(0.96f, 0.9f, 0.8f, 1f), TextAnchor.MiddleCenter);
                UiFactory.SetRect(name.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                    new Vector2(cardWidth - 40f, 44f), new Vector2(0f, -34f));

                Text description = UiFactory.CreateText(card.transform, "Description", classData.description, 16,
                    new Color(0.78f, 0.74f, 0.66f, 1f), TextAnchor.UpperCenter);
                description.horizontalOverflow = HorizontalWrapMode.Wrap;
                description.verticalOverflow = VerticalWrapMode.Truncate;
                UiFactory.SetRect(description.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                    new Vector2(cardWidth - 48f, 96f), new Vector2(0f, -110f));

                Text stats = UiFactory.CreateText(card.transform, "Stats", BuildStatsText(classData), 17,
                    new Color(0.9f, 0.85f, 0.72f, 1f), TextAnchor.UpperCenter);
                UiFactory.SetRect(stats.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    new Vector2(cardWidth - 40f, 200f), new Vector2(0f, -60f));

                _cardBackgrounds[classData] = card;
                _statLabels[classData] = stats;
            }

            Button descend = UiFactory.CreateButton(root, "DescendButton", "DESCEND", () => onStart?.Invoke(),
                new Vector2(300f, 62f), new Color(0.55f, 0.28f, 0.1f, 1f), new Color(1f, 0.95f, 0.85f, 1f), 24);
            UiFactory.SetRect(((Image)descend.targetGraphic).rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(300f, 62f), new Vector2(0f, 90f));

            Text controls = UiFactory.CreateText(root, "Controls",
                "WASD move  |  Mouse look  |  Shift sprint  |  Ctrl crouch  |  Space jump\n" +
                "Left mouse attack / draw  |  Right mouse block (Warrior)  |  E interact (hold for the gate)  |  R reload  |  1-4 use item  |  Esc pause",
                15, new Color(0.62f, 0.6f, 0.55f, 1f), TextAnchor.MiddleCenter);
            UiFactory.SetRect(controls.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(1400f, 60f), new Vector2(0f, 34f));
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
                    builder.AppendLine($"Reserve arrows  {data.startingReserveAmmo}");
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
                    ? new Color(Mathf.Lerp(0.09f, accent.r, 0.45f), Mathf.Lerp(0.075f, accent.g, 0.45f), Mathf.Lerp(0.06f, accent.b, 0.35f), 1f)
                    : new Color(0.09f, 0.075f, 0.06f, 0.95f);
            }
        }

        public CharacterClassData Selected => _selected;
    }
}
