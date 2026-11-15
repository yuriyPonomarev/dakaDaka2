using System.Collections.Generic;
using System.Reflection;
using Emberdelve.Core;
using Emberdelve.Flow;
using Emberdelve.InputLayer;
using Emberdelve.Player;
using Emberdelve.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.Combat.Tests
{
    public sealed class RunDirectorTerminalInputTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [TestCase(RunState.Dead)]
        [TestCase(RunState.Extracted)]
        public void TerminalLocalPlayerCannotReopenControlWhileOtherParticipantsRemain(RunState outcome)
        {
            var priorRole = NetRole.Current;
            var actor = new GameObject("TerminalLocal");
            var host = new GameObject("TerminalDirector");
            var view = new GameObject("TerminalHud");
            try
            {
                NetRole.ResetToOffline();
                var rig = actor.AddComponent<PlayerRig>();
                rig.WireRig();
                var input = rig.Input;
                typeof(EmberdelveInput).GetField("actions", Private).SetValue(input,
                    AssetDatabase.LoadAssetAtPath<Object>("Assets/Emberdelve/Data/EmberdelveInputActions.inputactions"));
                typeof(EmberdelveInput).GetMethod("Awake", Private).Invoke(input, null);
                rig.SetInputEnabled(true);
                object map = typeof(EmberdelveInput).GetField("_playerMap", Private).GetValue(input);
                bool InputEnabled() => (bool)map.GetType().GetProperty("enabled").GetValue(map);
                Assert.True(InputEnabled());

                var director = host.AddComponent<RunDirector>();
                var local = new PlayerSession { PlayerId = rig.PlayerId, Rig = rig, IsLocal = true };
                var sessions = (List<PlayerSession>)typeof(RunDirector).GetField("_sessions", Private).GetValue(director);
                sessions.Add(local);
                sessions.Add(new PlayerSession { PlayerId = rig.PlayerId + 1 });
                typeof(RunDirector).GetField("_local", Private).SetValue(director, local);
                typeof(RunDirector).GetField("_state", Private).SetValue(director, RunState.Running);
                director.SetInventoryOpen(true);
                typeof(RunDirector).GetMethod("CompletePlayer", Private).Invoke(director, new object[] { local, outcome });
                Assert.That(director.State, Is.EqualTo(RunState.Running), "other participants still own a running match");
                Assert.That(local.State, Is.EqualTo(outcome));
                Assert.False(director.InventoryOpen);

                var hud = view.AddComponent<HudController>();
                var root = new GameObject("HudRoot", typeof(RectTransform)).GetComponent<RectTransform>();
                root.SetParent(view.transform);
                typeof(HudController).GetField("_root", Private).SetValue(hud, root);
                typeof(HudController).GetField("_director", Private).SetValue(hud, director);
                foreach (var global in new[] { RunState.Running, RunState.Paused, RunState.Briefing })
                {
                    typeof(HudController).GetMethod("OnRunState", Private).Invoke(hud, new object[] { global });
                    Assert.False(root.gameObject.activeSelf, "global state must not resurrect terminal local HUD");
                }
                typeof(RunDirector).GetMethod("TogglePause", Private).Invoke(director, null);
                Assert.That(director.State, Is.EqualTo(RunState.Running));
                Assert.That(Time.timeScale, Is.EqualTo(1f));
                foreach (bool open in new[] { false, true, false })
                {
                    director.SetInventoryOpen(open);
                    Assert.False(director.InventoryOpen);
                    Assert.False(InputEnabled(), "closing a terminal player's bag must not enable gameplay");
                    Assert.That(Cursor.lockState, Is.EqualTo(CursorLockMode.None));
                    Assert.True(Cursor.visible);
                }
            }
            finally
            {
                Object.DestroyImmediate(view); Object.DestroyImmediate(host); Object.DestroyImmediate(actor);
                NetRole.Set(priorRole); Time.timeScale = 1f; RunContext.ResetHandoff();
            }
        }
    }
}
