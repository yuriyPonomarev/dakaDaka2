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
                    EditorApplication.Exit(1);
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
                    EditorApplication.Exit(1);
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

                    EditorApplication.Exit(0);
                    return;
                }

                Debug.LogError(Tag + " FAIL result=" + summary.result +
                               " outputExists=" + outputExists +
                               " requested=" + location +
                               " totalSize=" + summary.totalSize +
                               " totalTime=" + summary.totalTime +
                               " - see the engine build errors above this line");

                EditorApplication.Exit(1);
            }
            catch (Exception exception)
            {
                Debug.LogError(Tag + " FAIL exception while building to '" + (location ?? "unresolved") + "': " + exception);
                EditorApplication.Exit(1);
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
