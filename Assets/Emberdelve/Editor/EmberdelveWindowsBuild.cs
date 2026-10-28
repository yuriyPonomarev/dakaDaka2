using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// Headless Windows development-build entrypoint for the U1C2 review loop.
    ///
    ///   Unity.exe -batchmode -nographics -quit -projectPath . -logFile Logs/u1c2_build.log
    ///             -executeMethod Emberdelve.EditorTools.EmberdelveWindowsBuild.BuildReview
    ///             --ember-build-output C:\absolute\path\EmberdelveReview.exe
    ///
    /// It builds the two saved Emberdelve scenes (lobby first, then the dungeon) with
    /// BuildOptions.Development. It does not launch the Player, does not regenerate data, scenes or
    /// packages, does not touch the template Cockpit scenes, shows no dialog and changes no project
    /// setting. The caller supplies a fresh output directory, so nothing here deletes anything.
    /// </summary>
    public static class EmberdelveWindowsBuild
    {
        public const string OutputFlag = "--ember-build-output";
        private const string Tag = "[Emberdelve][BuildWindows]";

        /// <summary>Builds the review Player and exits the Editor with 0 only on a truthful success.</summary>
        public static void BuildReview()
        {
            string location = null;

            try
            {
                location = ResolveOutputPath(Environment.GetCommandLineArgs());

                if (string.IsNullOrEmpty(location))
                {
                    Debug.LogError(Tag + " FAIL: missing or invalid " + OutputFlag +
                                   " <absolute path to the output .exe>. Usage: -executeMethod " +
                                   typeof(EmberdelveWindowsBuild).FullName + ".BuildReview " + OutputFlag +
                                   " C:\\review\\EmberdelveReview.exe");
                    EmberdelveGateExit.Exit(1);
                    return;
                }

                string lobby = EmberdelvePaths.LobbyScenePath;
                string dungeon = EmberdelvePaths.DungeonScenePath;

                // Saved scenes only. Missing scenes are a blocker, not something to regenerate here.
                if (!File.Exists(lobby) || !File.Exists(dungeon))
                {
                    Debug.LogError(Tag + " FAIL: saved scene(s) missing - lobby='" + lobby + "' exists=" + File.Exists(lobby) +
                                   " dungeon='" + dungeon + "' exists=" + File.Exists(dungeon) +
                                   ". Run the Emberdelve editor builders first; this task does not rebuild scenes.");
                    EmberdelveGateExit.Exit(1);
                    return;
                }

                string parent = Path.GetDirectoryName(location);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                BuildPlayerOptions options = new BuildPlayerOptions
                {
                    scenes = new string[] { lobby, dungeon },
                    locationPathName = location,
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.Development
                };

                Debug.Log(Tag + " start target=StandaloneWindows64 options=Development scenes=[" +
                          lobby + ", " + dungeon + "] output=" + location);

                BuildReport report = BuildPipeline.BuildPlayer(options);
                BuildSummary summary = report.summary;

                // Report diagnostics are logged as-is; a failure is never downgraded to a warning.
                Debug.Log(Tag + " report result=" + summary.result +
                          " platform=" + summary.platform +
                          " options=" + summary.options +
                          " totalSize=" + summary.totalSize.ToString() + " bytes" +
                          " totalTime=" + summary.totalTime);

                bool outputExists = File.Exists(location);
                string dataFolder = Path.Combine(parent ?? string.Empty,
                    Path.GetFileNameWithoutExtension(location) + "_Data");

                if (summary.result == BuildResult.Succeeded && outputExists)
                {
                    long bytes = 0;
                    try { bytes = new FileInfo(location).Length; }
                    catch (Exception e) { Debug.LogWarning(Tag + " could not stat the output: " + e.Message); }

                    Debug.Log(Tag + " PASS result=" + summary.result +
                              " output=" + location +
                              " exeBytes=" + bytes +
                              " dataFolder=" + dataFolder + (Directory.Exists(dataFolder) ? " present" : " MISSING") +
                              " totalSize=" + summary.totalSize + " bytes" +
                              " totalTime=" + summary.totalTime +
                              " (any build warnings above are the engine's own and were not suppressed)");

                    EmberdelveGateExit.Exit(0);
                    return;
                }

                Debug.LogError(Tag + " FAIL result=" + summary.result +
                               " outputExists=" + outputExists +
                               " requested=" + location +
                               " totalSize=" + summary.totalSize +
                               " totalTime=" + summary.totalTime +
                               " - see the engine build errors above this line");

                EmberdelveGateExit.Exit(1);
            }
            catch (Exception exception)
            {
                Debug.LogError(Tag + " FAIL exception while building to '" + (location ?? "unresolved") + "': " + exception);
                EmberdelveGateExit.Exit(1);
            }
        }

        /// <summary>
        /// U3A1: builds the network proof Player (Development, Windows x64) from the NetBoot scene (index 0)
        /// and, since U3A3, the dungeon scene (index 1, loaded by the server through NGO scene management), without
        /// the lobby. Does not touch the
        /// editor build settings, other scenes or the existing BuildReview flow, and never exits the
        /// Editor. Returns true only for BuildResult.Succeeded with the .exe present.
        /// </summary>
        internal static bool BuildNetProofPlayer(string exePath, out string error)
        {
            error = null;
            try
            {
                if (!EmberdelveNetBuilder.EnsureNetBootScene(out string sceneError))
                {
                    error = "NetBoot scene unavailable: " + sceneError;
                    return false;
                }

                // U3A3: the server loads the dungeon through NGO scene management, so the dungeon scene ships in the proof
                // player after the NetBoot scene, and the network player prefab + its catalog (Resources) must exist.
                if (!File.Exists(EmberdelvePaths.DungeonScenePath))
                {
                    error = "dungeon scene missing: " + EmberdelvePaths.DungeonScenePath + " (build the vertical slice first)";
                    return false;
                }

                if (!EmberdelveNetBuilder.EnsureNetPlayerPrefab(out string prefabError))
                {
                    error = "net player prefab unavailable: " + prefabError;
                    return false;
                }

                string parent = Path.GetDirectoryName(exePath);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                BuildPlayerOptions options = new BuildPlayerOptions
                {
                    scenes = new string[] { EmberdelveNetBuilder.NetBootScenePath, EmberdelvePaths.DungeonScenePath },
                    locationPathName = exePath,
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.Development
                };

                Debug.Log(Tag + " net-proof start scenes=[" + EmberdelveNetBuilder.NetBootScenePath + ", " + EmberdelvePaths.DungeonScenePath + "] output=" + exePath);
                BuildReport report = BuildPipeline.BuildPlayer(options);
                BuildSummary summary = report.summary;
                Debug.Log(Tag + " net-proof report result=" + summary.result + " totalSize=" + summary.totalSize +
                          " totalTime=" + summary.totalTime);

                if (summary.result != BuildResult.Succeeded || !File.Exists(exePath))
                {
                    error = "build result=" + summary.result + " exeExists=" + File.Exists(exePath);
                    return false;
                }

                return true;
            }
            catch (Exception exception)
            {
                error = "exception: " + exception.Message;
                return false;
            }
        }

        /// <summary>
        /// U3A5: batch entry for the playable network build (lobby first, then the dungeon, Development, with the network player
        /// prefab and its catalog generated first when missing). The same exe is the offline game (no arguments), the dedicated
        /// server (-ember-net server), a command-line client, and a client/host started from the lobby's Online panel.
        ///
        ///   Unity.exe -batchmode -nographics -quit -projectPath . -logFile Logs/net_playable_build.log
        ///             -executeMethod Emberdelve.EditorTools.EmberdelveWindowsBuild.BuildNetPlayable
        ///             --ember-build-output C:\absolute\path\EmberdelveNet.exe
        /// </summary>
        public static void BuildNetPlayable()
        {
            string location = null;
            try
            {
                location = ResolveOutputPath(Environment.GetCommandLineArgs());
                if (string.IsNullOrEmpty(location))
                {
                    Debug.LogError(Tag + " net-playable FAIL: missing or invalid " + OutputFlag + " <absolute path to the output .exe>.");
                    EmberdelveGateExit.Exit(1);
                    return;
                }

                bool ok = BuildNetFinalPlayer(location, out string error);
                if (ok) Debug.Log(Tag + " net-playable PASS output=" + location);
                else Debug.LogError(Tag + " net-playable FAIL " + error);
                EmberdelveGateExit.Exit(ok ? 0 : 1);
            }
            catch (Exception exception)
            {
                Debug.LogError(Tag + " net-playable FAIL exception while building to '" + (location ?? "unresolved") + "': " + exception);
                EmberdelveGateExit.Exit(1);
            }
        }

        /// <summary>
        /// U3A5: builds the Development Windows x64 player with scenes [Lobby, Level01] (the lobby-first game) plus everything the
        /// network layer needs at run time (the generated player prefab and its Resources catalog; the Net assembly is auto-referenced).
        /// Used by the final proof gate and by BuildNetPlayable. Does not touch the editor build settings and never exits the Editor.
        /// </summary>
        internal static bool BuildNetFinalPlayer(string exePath, out string error)
        {
            error = null;
            try
            {
                string lobby = EmberdelvePaths.LobbyScenePath;
                string dungeon = EmberdelvePaths.DungeonScenePath;
                if (!File.Exists(lobby) || !File.Exists(dungeon))
                {
                    error = "saved scene(s) missing - lobby exists=" + File.Exists(lobby) + " dungeon exists=" + File.Exists(dungeon);
                    return false;
                }

                if (!EmberdelveNetBuilder.EnsureNetPlayerPrefab(out string prefabError))
                {
                    error = "net player prefab unavailable: " + prefabError;
                    return false;
                }

                string parent = Path.GetDirectoryName(exePath);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                BuildPlayerOptions options = new BuildPlayerOptions
                {
                    scenes = new string[] { lobby, dungeon },
                    locationPathName = exePath,
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.Development
                };

                Debug.Log(Tag + " net-final start scenes=[" + lobby + ", " + dungeon + "] output=" + exePath);
                BuildReport report = BuildPipeline.BuildPlayer(options);
                BuildSummary summary = report.summary;
                Debug.Log(Tag + " net-final report result=" + summary.result + " totalSize=" + summary.totalSize +
                          " totalTime=" + summary.totalTime);

                if (summary.result != BuildResult.Succeeded || !File.Exists(exePath))
                {
                    error = "build result=" + summary.result + " exeExists=" + File.Exists(exePath);
                    return false;
                }

                return true;
            }
            catch (Exception exception)
            {
                error = "exception: " + exception.Message;
                return false;
            }
        }

        /// <summary>
        /// Reads and validates --ember-build-output. Returns null when the flag is absent or the path is
        /// unusable (relative, not an .exe, or inside Assets where a build output would be imported).
        /// </summary>
        private static string ResolveOutputPath(string[] args)
        {
            if (args == null) return null;

            string raw = null;
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.IsNullOrEmpty(arg)) continue;

                if (arg == OutputFlag || arg == "-ember-build-output")
                {
                    if (i + 1 >= args.Length) return null;
                    raw = args[i + 1];
                    break;
                }

                if (arg.StartsWith(OutputFlag + "=", StringComparison.OrdinalIgnoreCase) ||
                    arg.StartsWith("-ember-build-output=", StringComparison.OrdinalIgnoreCase))
                {
                    raw = arg.Substring(arg.IndexOf('=') + 1);
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(raw)) return null;

            raw = raw.Trim().Trim('"');
            if (!Path.IsPathRooted(raw))
            {
                Debug.LogError(Tag + " FAIL: " + OutputFlag + " must be an absolute path, got '" + raw + "'.");
                return null;
            }

            string full = Path.GetFullPath(raw);

            if (!string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogError(Tag + " FAIL: " + OutputFlag + " must end in .exe, got '" + full + "'.");
                return null;
            }

            string assetsRoot = Path.GetFullPath("Assets").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.StartsWith(assetsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(assetsRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogError(Tag + " FAIL: the build output must live outside Assets/ (" + full + ").");
                return null;
            }

            return full;
        }
    }
}
