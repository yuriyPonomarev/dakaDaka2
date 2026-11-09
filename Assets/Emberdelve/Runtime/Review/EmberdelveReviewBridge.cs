#if DEVELOPMENT_BUILD || UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;

namespace Emberdelve.Review
{
    /// <summary>
    /// OPT-IN development review bridge. It is a harness, not player UX: nothing in the game screens
    /// changes and nothing at all is initialised unless the process was started with
    ///
    ///     --ember-review-dir <absolute directory>
    ///
    /// Without that flag this component never exists and normal gameplay is untouched.
    ///
    /// With the flag, one DontDestroyOnLoad host is created and the directory is used for
    /// command.json / ack.json / snapshots/. The reviewer replaces command.json with a single
    /// JsonUtility-friendly object whose "id" strictly increases; each id is executed exactly once and
    /// answered by one ack.json write. Commands drive the ORDINARY input path: queued KeyboardState and
    /// MouseState events on dedicated virtual devices, consumed by the normal InputSystem update, the
    /// game's InputActionAsset and the uGUI EventSystem. No gameplay method is called directly, no
    /// Button.onClick is invoked, no reflection, no network, no process control, no editor API.
    ///
    /// Supported actions: wait, key, mouse, snapshot, quit.
    /// See Docs/DEV_REVIEW.md for the exact schema and examples.
    /// </summary>
    public sealed class EmberdelveReviewBridge : MonoBehaviour
    {
        public const string DirectoryFlag = "--ember-review-dir";
        private const string Tag = "[Emberdelve][Review]";

        /// <summary>Hard caps so a mistyped command cannot leave a key or button stuck.</summary>
        public const int MaxWaitFrames = 900;
        public const int MaxHoldFrames = 120;
        public const int DefaultWaitFrames = 60;
        public const int DefaultHoldFrames = 3;
        /// <summary>Real-time cap for the optional key/mouse 'seconds' hold, independent of framerate.</summary>
        public const float MaxHoldSeconds = 10f;
        private const int MaxCaptureAttempts = 3;
        private const int MaxFileWaitFrames = 60;
        private const float CommandTimeoutSeconds = 45f;
        /// <summary>How many timeline samples one key hold may report. Never one per frame.</summary>
        private const int MaxTraceSamples = 6;

        private static EmberdelveReviewBridge _instance;

        private string _root;
        private string _snapshotDir;
        private string _commandPath;
        private string _ackPath;
        private string _readyPath;

        private bool _configured;
        private bool _busy;
        private int _lastProcessedId;
        private long _commandStamp;

        private Coroutine _running;
        private float _commandStartedAt;
        private int _commandId;
        private string _commandAction;

        private Keyboard _keyboard;
        private Mouse _mouse;
        private readonly List<Key> _heldKeys = new List<Key>();
        private ushort _heldButtons;
        private Vector2 _pointer;

        // ------------------------------------------------------------------ command / ack schema

        /// <summary>One command. JsonUtility only supports plain public fields, so everything is flat.</summary>
        [Serializable]
        public sealed class ReviewCommand
        {
            /// <summary>Strictly increasing positive id. Anything else is stale or duplicate.</summary>
            public int id;
            /// <summary>wait | key | mouse | snapshot | quit</summary>
            public string action;
            /// <summary>Frames for wait, or hold frames for key/mouse. Capped, never 0-length.
            /// For key/mouse it is ignored when 'seconds' asks for a real-time hold.</summary>
            public int frames;
            /// <summary>Optional hold duration for key/mouse in SECONDS of unscaled real time, so a hold
            /// lasts the requested wall-clock time whatever the framerate is. Positive and capped at
            /// MaxHoldSeconds; omitted or 0 keeps the exact 'frames' behaviour above. Negative, NaN or
            /// infinite values are rejected with a failed ack and no press.</summary>
            public float seconds;
            /// <summary>Key name(s), comma separated. Alternative: the keys array.</summary>
            public string key;
            public string[] keys;
            /// <summary>Screen pixel position, Unity origin at the BOTTOM-LEFT of the game window.</summary>
            public float x;
            public float y;
            /// <summary>Optional pointer delta, applied with the first (moving) state event.</summary>
            public float dx;
            public float dy;
            /// <summary>left | right (default left).</summary>
            public string button;
            /// <summary>Optional snapshot file name; must stay inside snapshots/.</summary>
            public string name;
        }

        /// <summary>Answer for one processed id, plus the live state that proves the game is running.</summary>
        [Serializable]
        public sealed class ReviewAck
        {
            public int id;
            public bool ok;
            public string action;
            public string message;
            public string error;
            public string screenshot;
            public string scene;
            public int frameCount;
            public float realtime;
            public float time;
            public int width;
            public int height;
            public string reviewDir;
            public string utc;
            /// <summary>Read-only input/movement evidence for a 'key' command, null for every other
            /// action. It describes only the frames of THIS command; nothing is carried across commands.</summary>
            public MovementTelemetry telemetry;
        }

        // ------------------------------------------------------------------ read-only hold telemetry

        /// <summary>
        /// What actually happened during one key hold, summarised into that command's ack. Every value is
        /// a plain read of state the game already keeps: the rig transform, the Move value the player's own
        /// EmberdelveInput reads, PlayerMovement.DesiredMoveIntent, the CharacterController's own
        /// velocity / collisionFlags / enabled / minMoveDistance, Time.deltaTime and the virtual review
        /// keyboard's own key controls. Nothing is ever written back to the game, and the sampler lives on
        /// the command that created it, so an ack cannot report stale state from an earlier hold.
        /// </summary>
        [Serializable]
        public sealed class MovementTelemetry
        {
            public bool rigFound;
            public string rigName;
            public string rigNote;
            public bool rigActiveInHierarchy;
            public bool rigBehaviourEnabled;
            public bool movementEnabled;
            public bool inputEnabled;
            public bool controllerFound;
            public bool controllerEnabled;
            public float controllerMinMoveDistance;

            public Vector3 positionStart;
            public Vector3 positionEnd;
            public float displacement;

            public float maxMoveMagnitude;
            public Vector2 moveSample;
            public float maxIntentMagnitude;
            public Vector3 intentSample;
            public float maxResolvedSpeed;
            public bool sprintObserved;
            public bool groundedObserved;
            public bool movingObserved;
            public bool dashObserved;

            public float maxVelocityMagnitude;
            public int collisionFlagsLast;
            public int collisionFlagsObserved;
            public string collisionFlagsSummary;

            public int framesSampled;
            public float minDeltaTime;
            public float maxDeltaTime;

            public string keys;
            public bool deviceKeyPressedAtSeed;
            public int deviceKeyFramesPressed;
            public int deviceKeyFirstSample = -1;
            public bool deviceIsCurrentKeyboard;

            /// <summary>At most MaxTraceSamples evenly spaced frames of this hold, oldest first.</summary>
            public string trace;
            public string note;
        }

        /// <summary>Per-command sampler. Created after the command validated, seeded before the press,
        /// sampled inside the ordinary hold loop, finished after the release.</summary>
        private sealed class MovementProbe
        {
            private readonly Emberdelve.Player.PlayerRig _rig;
            private readonly Emberdelve.Movement.PlayerMovement _movement;
            private readonly Emberdelve.InputLayer.EmberdelveInput _input;
            private readonly CharacterController _controller;
            private readonly Keyboard _device;
            private readonly UnityEngine.InputSystem.Controls.KeyControl[] _deviceKeys;

            private readonly MovementTelemetry _data = new MovementTelemetry();
            private readonly List<string> _trace = new List<string>();

            private readonly int _traceEveryFrames;
            private readonly float _traceEverySeconds;
            private float _nextTraceAt;
            private int _samples;

            public MovementProbe(List<Key> keys, Keyboard device, int traceEveryFrames, float traceEverySeconds)
            {
                _device = device;
                _traceEveryFrames = traceEveryFrames;
                _traceEverySeconds = traceEverySeconds;

                _rig = UnityEngine.Object.FindFirstObjectByType<Emberdelve.Player.PlayerRig>();
                _data.rigFound = _rig != null;
                _data.keys = DescribeKeys(keys);
                _data.collisionFlagsSummary = "none";
                _data.deviceKeyFirstSample = -1;

                if (_rig != null)
                {
                    _data.rigName = _rig.name;
                    _movement = _rig.Movement != null ? _rig.Movement : _rig.GetComponent<Emberdelve.Movement.PlayerMovement>();
                    _input = _rig.Input != null ? _rig.Input : _rig.GetComponent<Emberdelve.InputLayer.EmberdelveInput>();
                    _controller = _rig.GetComponent<CharacterController>();
                    _data.controllerFound = _controller != null;
                    if (_movement == null) AppendNote("PlayerRig has no PlayerMovement");
                    if (_input == null) AppendNote("PlayerRig has no EmberdelveInput");
                    if (_controller == null) AppendNote("PlayerRig has no CharacterController");
                }
                else
                {
                    _data.rigNote = "no PlayerRig in the active scene (a menu scene such as Lobby is a valid place for this)";
                }

                if (_device != null && keys != null && keys.Count > 0)
                {
                    _deviceKeys = new UnityEngine.InputSystem.Controls.KeyControl[keys.Count];
                    for (int i = 0; i < keys.Count; i++)
                    {
                        try { _deviceKeys[i] = _device[keys[i]]; }
                        catch (Exception exception)
                        {
                            AppendNote("review keyboard has no control for " + keys[i] + ": " + exception.Message);
                        }
                    }
                }
            }

            /// <summary>Baseline before the press: where the rig is, and proof the key was not already down.</summary>
            public void Seed()
            {
                _data.minDeltaTime = float.MaxValue;
                _data.deviceKeyPressedAtSeed = AnyDeviceKeyPressed();
                if (_rig != null) _data.positionStart = _rig.transform.position;
                if (_controller != null) _data.controllerMinMoveDistance = _controller.minMoveDistance;
            }

            /// <summary>One read-only sample of the frame that just ran its ordinary Update.</summary>
            public void Sample()
            {
                _samples++;
                _data.framesSampled = _samples;

                float dt = Time.deltaTime;
                if (dt < _data.minDeltaTime) _data.minDeltaTime = dt;
                if (dt > _data.maxDeltaTime) _data.maxDeltaTime = dt;

                try
                {
                    if (_rig == null) return; // already reported as unavailable

                    _data.rigActiveInHierarchy = _rig.gameObject.activeInHierarchy;
                    _data.rigBehaviourEnabled = _rig.enabled;
                    _data.movementEnabled = _movement != null && _movement.enabled;
                    _data.inputEnabled = _input != null && _input.enabled;
                    _data.controllerEnabled = _controller != null && _controller.enabled;

                    Vector2 move = _input != null ? _input.Move : Vector2.zero;
                    Vector3 intent = _movement != null ? _movement.DesiredMoveIntent : Vector3.zero;
                    Vector3 velocity = _controller != null ? _controller.velocity : Vector3.zero;

                    if (move.magnitude > _data.maxMoveMagnitude)
                    {
                        _data.maxMoveMagnitude = move.magnitude;
                        _data.moveSample = move;
                    }

                    if (intent.magnitude > _data.maxIntentMagnitude)
                    {
                        _data.maxIntentMagnitude = intent.magnitude;
                        _data.intentSample = intent;
                    }

                    if (velocity.magnitude > _data.maxVelocityMagnitude) _data.maxVelocityMagnitude = velocity.magnitude;

                    if (_movement != null)
                    {
                        if (_movement.IsGrounded) _data.groundedObserved = true;
                        if (_movement.IsMoving) _data.movingObserved = true;
                        if (_movement.IsDashing) _data.dashObserved = true;

                        // Side-effect free by design (see PlayerMovement.ResolveGroundSpeed): it proves whether
                        // a class was applied at all, which is the first thing PlayerMovement.Update checks.
                        bool sprint = _input != null && _input.SprintHeld;
                        if (sprint) _data.sprintObserved = true;
                        float speed = _movement.ResolveGroundSpeed(sprint, _movement.IsCrouched);
                        if (speed > _data.maxResolvedSpeed) _data.maxResolvedSpeed = speed;
                    }

                    if (_controller != null)
                    {
                        CollisionFlags flags = _controller.collisionFlags;
                        _data.collisionFlagsLast = (int)flags;
                        _data.collisionFlagsObserved |= (int)flags;
                    }

                    bool pressed = AnyDeviceKeyPressed();
                    if (pressed)
                    {
                        _data.deviceKeyFramesPressed++;
                        if (_data.deviceKeyFirstSample < 0) _data.deviceKeyFirstSample = _samples;
                    }

                    AddTrace(dt, move, intent, velocity, pressed);
                }
                catch (Exception exception)
                {
                    AppendNote("telemetry sample failed: " + exception.Message);
                }
            }

            /// <summary>End of the hold: the position the ordinary frames actually left the player at.</summary>
            public void FinishProbe()
            {
                try
                {
                    if (_rig != null) _data.positionEnd = _rig.transform.position;
                    _data.displacement = Vector3.Distance(_data.positionStart, _data.positionEnd);
                    _data.collisionFlagsSummary = DescribeFlags(_data.collisionFlagsObserved);
                    _data.deviceIsCurrentKeyboard = _device != null && Keyboard.current == _device;
                    _data.trace = _trace.Count == 0 ? string.Empty : string.Join(" | ", _trace);
                }
                catch (Exception exception)
                {
                    AppendNote("telemetry finish failed: " + exception.Message);
                }
            }

            public MovementTelemetry Build()
            {
                if (_data.minDeltaTime == float.MaxValue) _data.minDeltaTime = 0f;
                if (_data.trace == null) _data.trace = _trace.Count == 0 ? string.Empty : string.Join(" | ", _trace);
                return _data;
            }

            /// <summary>One-line summary so the console log and the ack message carry the evidence too.</summary>
            public string Summary()
            {
                if (!_data.rigFound) return "telemetry: rig unavailable (" + _data.rigNote + ")";

                return "telemetry moveMax " + N2(_data.maxMoveMagnitude) + "(" + N2(_data.moveSample.x) + "," +
                       N2(_data.moveSample.y) + ") intentMax " + N2(_data.maxIntentMagnitude) + "(" +
                       N2(_data.intentSample.x) + "," + N2(_data.intentSample.y) + "," + N2(_data.intentSample.z) +
                       ") velMax " + N2(_data.maxVelocityMagnitude) + " speedMax " + N2(_data.maxResolvedSpeed) +
                       " moved " + N4(_data.displacement) + " pos " + Pos(_data.positionStart) + "->" +
                       Pos(_data.positionEnd) + " dt " + N4(_data.minDeltaTime) + ".." + N4(_data.maxDeltaTime) +
                       " frames " + _data.framesSampled + " rig " + OnOff(_data.rigBehaviourEnabled) + "/" +
                       OnOff(_data.rigActiveInHierarchy) + " movement " + OnOff(_data.movementEnabled) +
                       " input " + OnOff(_data.inputEnabled) + " controller " + OnOff(_data.controllerEnabled) +
                       " minMove " + N4(_data.controllerMinMoveDistance) + " collisions " +
                       _data.collisionFlagsSummary + " grounded " + OnOff(_data.groundedObserved) +
                       " deviceKey " + _data.deviceKeyFramesPressed + "/" + _data.framesSampled +
                       " first " + _data.deviceKeyFirstSample + " seedPressed " + (_data.deviceKeyPressedAtSeed ? "yes" : "no") +
                       " current " + OnOff(_data.deviceIsCurrentKeyboard);
            }

            private void AddTrace(float dt, Vector2 move, Vector3 intent, Vector3 velocity, bool pressed)
            {
                if (_trace.Count >= MaxTraceSamples) return;

                if (_traceEveryFrames > 0)
                {
                    if (_samples % _traceEveryFrames != 0) return;
                }
                else if (_traceEverySeconds > 0f)
                {
                    if (Time.realtimeSinceStartup < _nextTraceAt) return;
                    _nextTraceAt = Time.realtimeSinceStartup + _traceEverySeconds;
                }
                else
                {
                    return;
                }

                _trace.Add("f" + Time.frameCount + " dt" + N4(dt) +
                           " mv" + N2(move.magnitude) + "(" + N2(move.x) + "," + N2(move.y) + ")" +
                           " it" + N2(intent.magnitude) + "(" + N2(intent.x) + "," + N2(intent.y) + "," +
                           N2(intent.z) + ")" +
                           " vel" + N2(velocity.magnitude) +
                           " pos" + Pos(_rig != null ? _rig.transform.position : Vector3.zero) +
                           " fl" + _data.collisionFlagsLast +
                           " key" + (pressed ? 1 : 0));
            }

            private bool AnyDeviceKeyPressed()
            {
                if (_deviceKeys == null) return false;

                try
                {
                    for (int i = 0; i < _deviceKeys.Length; i++)
                    {
                        UnityEngine.InputSystem.Controls.KeyControl control = _deviceKeys[i];
                        if (control != null && control.isPressed) return true;
                    }
                }
                catch (Exception exception)
                {
                    AppendNote("review keyboard read failed: " + exception.Message);
                }

                return false;
            }

            private void AppendNote(string text)
            {
                _data.note = string.IsNullOrEmpty(_data.note) ? text : _data.note + " | " + text;
            }

            private static string DescribeFlags(int flags)
            {
                if (flags == 0) return "none";

                StringBuilder builder = new StringBuilder();
                if ((flags & (int)CollisionFlags.CollidedAbove) != 0) builder.Append("above|");
                if ((flags & (int)CollisionFlags.CollidedBelow) != 0) builder.Append("below|");
                if ((flags & (int)CollisionFlags.CollidedSides) != 0) builder.Append("sides|");

                return builder.Length == 0 ? flags.ToString() : builder.ToString(0, builder.Length - 1);
            }

            private static string Pos(Vector3 value)
            {
                return "(" + N3(value.x) + "," + N3(value.y) + "," + N3(value.z) + ")";
            }

            private static string OnOff(bool value) => value ? "on" : "off";
            private static string N2(float value) => value.ToString("F2");
            private static string N3(float value) => value.ToString("F3");
            private static string N4(float value) => value.ToString("F4");
        }

        // ------------------------------------------------------------------ installation

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            if (_instance != null)
            {
                Debug.LogWarning(Tag + " a review bridge is already running; ignoring the repeated install.");
                return;
            }

            string directory = null;
            try
            {
                directory = ResolveReviewDirectory(Environment.GetCommandLineArgs());
            }
            catch (Exception exception)
            {
                Debug.LogError(Tag + " could not read the command line: " + exception.Message);
                return;
            }

            // No flag => no host, no runInBackground change, no devices, no file access. Normal game.
            if (string.IsNullOrEmpty(directory)) return;

            GameObject host = new GameObject("Emberdelve_ReviewBridge");
            UnityEngine.Object.DontDestroyOnLoad(host);
            EmberdelveReviewBridge bridge = host.AddComponent<EmberdelveReviewBridge>();
            bridge.Configure(directory);
        }

        private static string ResolveReviewDirectory(string[] args)
        {
            if (args == null) return null;

            string raw = null;
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.IsNullOrEmpty(arg)) continue;

                if (arg == DirectoryFlag || arg == "-ember-review-dir")
                {
                    if (i + 1 >= args.Length)
                    {
                        Debug.LogError(Tag + " FAIL: " + DirectoryFlag + " needs a directory argument.");
                        return null;
                    }
                    raw = args[i + 1];
                    break;
                }

                if (arg.StartsWith(DirectoryFlag + "=", StringComparison.OrdinalIgnoreCase) ||
                    arg.StartsWith("-ember-review-dir=", StringComparison.OrdinalIgnoreCase))
                {
                    raw = arg.Substring(arg.IndexOf('=') + 1);
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(raw)) return null;

            raw = raw.Trim().Trim('"');
            if (!Path.IsPathRooted(raw))
            {
                Debug.LogError(Tag + " FAIL: " + DirectoryFlag + " must be an absolute directory, got '" + raw +
                               "'. Review was NOT initialised.");
                return null;
            }

            return Path.GetFullPath(raw);
        }

        private void Configure(string directory)
        {
            _instance = this;
            _root = directory;
            _snapshotDir = Path.Combine(_root, "snapshots");
            _commandPath = Path.Combine(_root, "command.json");
            _ackPath = Path.Combine(_root, "ack.json");
            _readyPath = Path.Combine(_root, "ready.json");
            _pointer = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

            try
            {
                Directory.CreateDirectory(_root);
                Directory.CreateDirectory(_snapshotDir);
                _configured = true;
            }
            catch (Exception exception)
            {
                Debug.LogError(Tag + " FAIL: review directory '" + _root + "' is unusable: " + exception.Message);
                return;
            }

            // Only this review session runs in the background; a normal launch is untouched.
            Application.runInBackground = true;

            Debug.Log(Tag + " review session dir=" + _root + " command=" + _commandPath + " ack=" + _ackPath +
                      " snapshots=" + _snapshotDir + " scene=" + ActiveSceneName() +
                      " frame=" + Time.frameCount + " screen=" + Screen.width + "x" + Screen.height +
                      " runInBackground=true");

            _busy = true;
            _commandId = 0;
            _commandAction = "boot";
            _commandStartedAt = Time.realtimeSinceStartup;
            _running = StartCoroutine(BootSequence());
        }

        // ------------------------------------------------------------------ frame loop

        private void Update()
        {
            if (!_configured) return;

            // Watchdog: a command whose coroutine never finished (a stalled render, for example) must
            // not leave the session stuck with a held key or button.
            if (_busy)
            {
                if (Time.realtimeSinceStartup - _commandStartedAt > CommandTimeoutSeconds)
                {
                    Debug.LogError(Tag + " FAIL: id=" + _commandId + " action=" + _commandAction +
                                   " exceeded " + CommandTimeoutSeconds + "s of real time; aborting and releasing input");

                    if (_running != null) StopCoroutine(_running);
                    _running = null;

                    ReleaseHeldInput();
                    WriteAck(BuildAck(_commandId, _commandAction, false, null,
                        "command aborted after exceeding the " + CommandTimeoutSeconds + "s review timeout",
                        null, 0, 0));
                    _busy = false;
                }

                return;
            }

            PollCommand();
        }

        private IEnumerator BootSequence()
        {
            // Two real frames first: the ready diagnostic must not describe a half-built scene.
            yield return null;
            yield return null;

            ReviewAck ready = BuildAck(0, "ready", true,
                "review bridge online; commands are read from command.json and answered in ack.json",
                null, null, 0, 0);
            WriteJson(_readyPath, JsonUtility.ToJson(ready, true));

            Debug.Log(Tag + " ready id=0 scene=" + ready.scene + " frame=" + ready.frameCount +
                      " time=" + ready.realtime.ToString("F3") + " screen=" + ready.width + "x" + ready.height);

            ReviewCommand first = new ReviewCommand
            {
                id = 0,
                action = "snapshot",
                name = "000_first_" + SafeName(ready.scene) + ".png"
            };

            yield return RunSnapshot(first, 0);
        }

        private void PollCommand()
        {
            ReviewCommand command;
            string error;

            if (!TryReadNextCommand(out command, out error))
            {
                Debug.LogError(Tag + " FAIL: " + error);
                WriteAck(BuildAck(-1, "read", false, null, error, null, 0, 0));
                return;
            }

            if (command == null) return;

            Debug.Log(Tag + " command id=" + command.id + " action=" + (command.action ?? "") +
                      " frames=" + command.frames + " key=" + (command.key ?? "") +
                      " button=" + (command.button ?? "") + " pos=(" + command.x + "," + command.y + ")");

            _busy = true;
            _commandId = command.id;
            _commandAction = (command.action ?? string.Empty).Trim().ToLowerInvariant();
            _commandStartedAt = Time.realtimeSinceStartup;
            _running = StartCoroutine(ExecuteCommand(command));
        }

        private bool TryReadNextCommand(out ReviewCommand command, out string error)
        {
            command = null;
            error = null;

            try
            {
                if (!File.Exists(_commandPath)) return true;

                FileInfo info = new FileInfo(_commandPath);
                long stamp = info.LastWriteTimeUtc.Ticks ^ (info.Length * 31L);
                if (stamp == _commandStamp) return true; // unchanged since the last read

                string json;
                using (FileStream stream = new FileStream(_commandPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        json = reader.ReadToEnd();
                    }
                }

                _commandStamp = stamp; // consume only after a successful read; a writer may be replacing the file
                string trimmed = json == null ? string.Empty : json.Trim();
                if (trimmed.Length == 0 || trimmed[0] != '{')
                {
                    error = "command.json must contain exactly one JSON object (id, action, ...); got '" +
                            (trimmed.Length == 0 ? "<empty>" : trimmed.Substring(0, Math.Min(80, trimmed.Length))) + "'";
                    return false;
                }

                command = JsonUtility.FromJson<ReviewCommand>(trimmed);
                if (command == null)
                {
                    error = "command.json did not deserialise into the review command schema";
                    return false;
                }

                return true;
            }
            catch (IOException)
            {
                // Transient replacement/sharing races are retried on the next poll, with no failed acknowledgement.
                return true;
            }
            catch (Exception exception)
            {
                error = "could not read " + _commandPath + ": " + exception.Message;
                return false;
            }
        }

        // ------------------------------------------------------------------ command execution
        // No 'yield' is ever used inside try/catch or finally; every fallible step lives in a
        // non-iterator helper that returns a result instead.

        private IEnumerator ExecuteCommand(ReviewCommand command)
        {
            string action = (command.action ?? string.Empty).Trim().ToLowerInvariant();

            if (command.id <= 0)
            {
                Finish(command, false, "id must be a positive integer, got " + command.id, null, 0, 0);
                yield break;
            }

            if (command.id <= _lastProcessedId)
            {
                Finish(command, false,
                    "stale or duplicate id " + command.id + " (already processed up to " + _lastProcessedId + ")",
                    null, 0, 0);
                yield break;
            }

            _lastProcessedId = command.id;

            switch (action)
            {
                case "wait":
                    yield return RunWait(command);
                    break;
                case "key":
                    yield return RunKey(command);
                    break;
                case "mouse":
                    yield return RunMouse(command);
                    break;
                case "snapshot":
                    yield return RunSnapshot(command, command.id);
                    break;
                case "quit":
                    yield return RunQuit(command);
                    break;
                default:
                    Finish(command, false,
                        "unknown action '" + command.action + "' (wait|key|mouse|snapshot|quit)", null, 0, 0);
                    break;
            }
        }

        private IEnumerator RunWait(ReviewCommand command)
        {
            int frames = ClampFrames(command.frames, DefaultWaitFrames, MaxWaitFrames);

            for (int i = 0; i < frames; i++) yield return null;

            string note = command.frames > frames
                ? "advanced " + frames + " frames (requested " + command.frames + " capped at " + MaxWaitFrames + ")"
                : "advanced " + frames + " frames";

            Finish(command, true, note, null, 0, 0);
        }

        private IEnumerator RunKey(ReviewCommand command)
        {
            float requestedSeconds;
            string secondsError;
            if (!TryResolveHoldSeconds(command.seconds, out requestedSeconds, out secondsError))
            {
                Finish(command, false, secondsError, null, 0, 0);
                yield break;
            }

            List<Key> keys = new List<Key>();
            string parseError;
            if (!TryParseKeys(command, keys, out parseError))
            {
                Finish(command, false, parseError, null, 0, 0);
                yield break;
            }

            EnsureDevices();
            if (_keyboard == null)
            {
                Finish(command, false, "virtual review keyboard could not be created", null, 0, 0);
                yield break;
            }

            int hold = ClampFrames(command.frames, DefaultHoldFrames, MaxHoldFrames);

            // Read-only evidence for THIS command only: resolved once, seeded before the press, sampled in
            // the ordinary hold loop (both the frames and the seconds path) and finished after the release.
            // The trace is bounded to MaxTraceSamples evenly spaced frames, never one entry per frame.
            MovementProbe probe = new MovementProbe(keys, _keyboard,
                requestedSeconds > 0f ? 0 : Math.Max(1, hold / MaxTraceSamples),
                requestedSeconds > 0f ? requestedSeconds / MaxTraceSamples : 0f);
            probe.Seed();

            if (!QueueKeyboard(keys, true))
            {
                Finish(command, false, "queueing the keyboard press failed", null, 0, 0, probe.Build());
                yield break;
            }

            int frameAtPress = Time.frameCount;
            float startedAt = Time.realtimeSinceStartup; // unscaled real time: the same clock as the watchdog

            string duration;
            string capNote;

            if (requestedSeconds > 0f)
            {
                // Ordinary frames, bounded by wall clock rather than by a frame count, so the hold is
                // the same real duration at 30 FPS and at 1000 FPS. Nothing is simulated or fast-forwarded.
                // At least one frame is always held, so a sub-frame request cannot put press and release
                // in the same input update.
                do
                {
                    yield return null;
                    probe.Sample();
                }
                while (Time.realtimeSinceStartup - startedAt < requestedSeconds);

                duration = DescribeSecondsHold(command.seconds, requestedSeconds,
                    Time.realtimeSinceStartup - startedAt, Time.frameCount - frameAtPress);
                capNote = string.Empty;
            }
            else
            {
                for (int i = 0; i < hold; i++)
                {
                    yield return null;
                    probe.Sample();
                }

                duration = "for " + hold + " frames";
                capNote = command.frames > hold
                    ? " (requested " + command.frames + " capped at " + MaxHoldFrames + ")"
                    : string.Empty;
            }

            bool released = QueueKeyboard(keys, false);
            yield return null; // let the ordinary input update consume the release before answering
            probe.FinishProbe(); // the position the ordinary frames actually left the player at

            Finish(command, released,
                "held " + DescribeKeys(keys) + " " + duration + " then released" + capNote + " | " + probe.Summary(),
                null, 0, 0, probe.Build());
        }

        private IEnumerator RunMouse(ReviewCommand command)
        {
            float requestedSeconds;
            string secondsError;
            if (!TryResolveHoldSeconds(command.seconds, out requestedSeconds, out secondsError))
            {
                Finish(command, false, secondsError, null, 0, 0);
                yield break;
            }

            EnsureDevices();
            if (_mouse == null)
            {
                Finish(command, false, "virtual review mouse could not be created", null, 0, 0);
                yield break;
            }

            string buttonError;
            ushort bit = ResolveButton(command.button, out buttonError);
            if (bit == 0)
            {
                Finish(command, false, buttonError, null, 0, 0);
                yield break;
            }

            int width = Screen.width;
            int height = Screen.height;
            if (command.x < 0f || command.x > width || command.y < 0f || command.y > height)
            {
                Finish(command, false,
                    "pointer (" + command.x + "," + command.y + ") is outside the " + width + "x" + height +
                    " screen; x/y are pixels from the BOTTOM-LEFT of the window", null, 0, 0);
                yield break;
            }

            int hold = ClampFrames(command.frames, DefaultHoldFrames, MaxHoldFrames);
            Vector2 position = new Vector2(command.x, command.y);
            Vector2 delta = new Vector2(command.dx, command.dy);

            // Move first, then press: the same order a real pointer produces, so hover/press handlers
            // run through the uGUI EventSystem exactly as they do for a player.
            if (!QueueMouse(position, delta, 0))
            {
                Finish(command, false, "queueing the pointer move failed", null, 0, 0);
                yield break;
            }
            yield return null;

            if (!QueueMouse(position, Vector2.zero, bit))
            {
                Finish(command, false, "queueing the button press failed", null, 0, 0);
                yield break;
            }

            int frameAtPress = Time.frameCount;
            float startedAt = Time.realtimeSinceStartup; // unscaled real time: the same clock as the watchdog

            string duration;
            string capNote;

            if (requestedSeconds > 0f)
            {
                do
                {
                    yield return null;
                }
                while (Time.realtimeSinceStartup - startedAt < requestedSeconds);

                duration = DescribeSecondsHold(command.seconds, requestedSeconds,
                    Time.realtimeSinceStartup - startedAt, Time.frameCount - frameAtPress);
                capNote = string.Empty;
            }
            else
            {
                for (int i = 0; i < hold; i++) yield return null;

                duration = "for " + hold + " frames";
                capNote = command.frames > hold
                    ? " (requested " + command.frames + " capped at " + MaxHoldFrames + ")"
                    : string.Empty;
            }

            // A named mouse command captures the held pose before releasing the button.
            // RunSnapshot finishes through Finish, which releases input even on capture failure.
            if (!string.IsNullOrWhiteSpace(command.name))
            {
                yield return RunSnapshot(command, command.id);
                yield break;
            }

            bool released = QueueMouse(position, Vector2.zero, 0);
            yield return null;

            Finish(command, released,
                "pointer moved to (" + position.x + "," + position.y + ") delta(" + delta.x + "," + delta.y +
                ") and held " + (bit == 2 ? "right" : "left") + " " + duration + " then released" + capNote,
                null, 0, 0);
        }

        private IEnumerator RunSnapshot(ReviewCommand command, int ackId)
        {
            string path = ResolveSnapshotPath(command.name, ackId);
            if (path == null)
            {
                Finish(command, false, "snapshot name is not a safe file name inside " + _snapshotDir, null, 0, 0);
                yield break;
            }

            CaptureResult capture = null;
            for (int attempt = 0; attempt < MaxCaptureAttempts; attempt++)
            {
                yield return new WaitForEndOfFrame();
                capture = TryCapture(path);
                if (capture.ok) break;
            }

            if (capture == null || !capture.ok)
            {
                Finish(command, false, "no rendered frame could be captured: " +
                                       (capture != null ? capture.error : "no attempt ran"), null, 0, 0);
                yield break;
            }

            int waited = 0;
            while (!FileExistsSafe(path) && waited < MaxFileWaitFrames)
            {
                waited++;
                yield return null;
            }

            if (!FileExistsSafe(path))
            {
                Finish(command, false, "capture reported success but " + Path.GetFileName(path) +
                                       " never appeared on disk", null, 0, 0);
                yield break;
            }

            int width = capture.width;
            int height = capture.height;
            int pngWidth;
            int pngHeight;
            if (TryReadPngSize(path, out pngWidth, out pngHeight))
            {
                width = pngWidth;
                height = pngHeight;
            }

            Finish(command, true,
                "captured " + Path.GetFileName(path) + " " + width + "x" + height + " " + FileLengthSafe(path) +
                " bytes at frame " + Time.frameCount + " using " +
                (capture.async ? "ScreenCapture.CaptureScreenshot" : "ScreenCapture.CaptureScreenshotAsTexture"),
                path, width, height);
        }

        private IEnumerator RunQuit(ReviewCommand command)
        {
            ReleaseHeldInput();
            Finish(command, true, "quit requested; held input released and ack flushed", null, 0, 0);

            yield return null; // one frame so the ack is definitely on disk before shutdown

            Debug.Log(Tag + " quitting owned Player id=" + command.id);
#if UNITY_EDITOR
            Debug.Log(Tag + " note: in Editor play mode Application.Quit does not exit play mode; stop play mode manually.");
#endif
            Application.Quit();
        }

        // ------------------------------------------------------------------ input plumbing

        private void EnsureDevices()
        {
            try
            {
                // Review commands target this player even while another client has focus.
                // Only our synthetic devices opt in; native devices retain the game's focus policy.
                if (_keyboard == null)
                {
                    InputSystem.RegisterLayout("{\"name\":\"EmberdelveReviewKeyboard\",\"extend\":\"Keyboard\",\"runInBackground\":\"enabled\"}");
                    _keyboard = (Keyboard)InputSystem.AddDevice("EmberdelveReviewKeyboard");
                }
                if (_mouse == null)
                {
                    InputSystem.RegisterLayout("{\"name\":\"EmberdelveReviewMouse\",\"extend\":\"Mouse\",\"runInBackground\":\"enabled\"}");
                    _mouse = (Mouse)InputSystem.AddDevice("EmberdelveReviewMouse");
                }

                // The review devices are the ones the reviewer drives, so they must be the .current
                // devices that uGUI and the inventory pointer read. The OS devices stay untouched.
                if (_keyboard != null) _keyboard.MakeCurrent();
                if (_mouse != null) _mouse.MakeCurrent();
            }
            catch (Exception exception)
            {
                Debug.LogError(Tag + " could not create the virtual review devices: " + exception.Message);
                _keyboard = null;
                _mouse = null;
            }
        }

        private bool QueueKeyboard(List<Key> keys, bool down)
        {
            try
            {
                if (_keyboard == null) return false;

                KeyboardState state = new KeyboardState();
                for (int i = 0; i < keys.Count; i++) state.Set(keys[i], down);

                InputSystem.QueueStateEvent(_keyboard, state);

                if (down)
                {
                    _heldKeys.Clear();
                    _heldKeys.AddRange(keys);
                }
                else
                {
                    _heldKeys.Clear();
                }

                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError(Tag + " keyboard state event failed: " + exception.Message);
                return false;
            }
        }

        private bool QueueMouse(Vector2 position, Vector2 delta, ushort buttons)
        {
            try
            {
                if (_mouse == null) return false;

                MouseState state = new MouseState
                {
                    displayIndex = 0,
                    position = position,
                    delta = delta,
                    scroll = Vector2.zero,
                    buttons = buttons
                };

                InputSystem.QueueStateEvent(_mouse, state);

                _pointer = position;
                _heldButtons = buttons;
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError(Tag + " mouse state event failed: " + exception.Message);
                return false;
            }
        }

        private void ReleaseHeldInput()
        {
            if (_heldKeys.Count > 0) QueueKeyboard(_heldKeys, false);
            if (_heldButtons != 0) QueueMouse(_pointer, Vector2.zero, 0);
            _heldKeys.Clear();
            _heldButtons = 0;
        }

        private static bool TryParseKeys(ReviewCommand command, List<Key> keys, out string error)
        {
            error = null;
            keys.Clear();

            string[] tokens = null;
            if (command.keys != null && command.keys.Length > 0) tokens = command.keys;
            else if (!string.IsNullOrEmpty(command.key))
            {
                tokens = command.key.Split(new char[] { ',', ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            }

            if (tokens == null || tokens.Length == 0)
            {
                error = "key command needs 'key' (\"w,leftShift\") or 'keys' ([\"w\",\"leftShift\"])";
                return false;
            }

            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i] == null ? string.Empty : tokens[i].Trim();
                if (token.Length == 0) continue;

                Key key;
                if (!TryResolveKey(token, out key))
                {
                    error = "unknown key '" + token + "' (UnityEngine.InputSystem.Key names such as w, a, s, d, e, f, g, q, r, i, tab, space, enter, escape, leftShift, leftCtrl, upArrow, digit1)";
                    return false;
                }

                if (!keys.Contains(key)) keys.Add(key);
            }

            if (keys.Count == 0)
            {
                error = "key command resolved no keys";
                return false;
            }

            return true;
        }

        private static bool TryResolveKey(string token, out Key key)
        {
            switch (token.ToLowerInvariant())
            {
                case "shift": key = Key.LeftShift; return true;
                case "ctrl":
                case "control": key = Key.LeftCtrl; return true;
                case "alt": key = Key.LeftAlt; return true;
                case "esc": key = Key.Escape; return true;
                case "enter": key = Key.Enter; return true;   // Key.Enter is the main-block enter/return key
                case "space": key = Key.Space; return true;
                case "tab": key = Key.Tab; return true;
                case "up": key = Key.UpArrow; return true;
                case "down": key = Key.DownArrow; return true;
                case "left": key = Key.LeftArrow; return true;
                case "right": key = Key.RightArrow; return true;
                case "0": key = Key.Digit0; return true;
                case "1": key = Key.Digit1; return true;
                case "2": key = Key.Digit2; return true;
                case "3": key = Key.Digit3; return true;
                case "4": key = Key.Digit4; return true;
                case "5": key = Key.Digit5; return true;
                case "6": key = Key.Digit6; return true;
                case "7": key = Key.Digit7; return true;
                case "8": key = Key.Digit8; return true;
                case "9": key = Key.Digit9; return true;
            }

            // Enum.TryParse would happily accept "42" as a numeric enum value; that is not a key name.
            if (IsAllDigits(token))
            {
                key = default(Key);
                return false;
            }

            if (Enum.TryParse(token, true, out key)) return true;

            key = default(Key);
            return false;
        }

        private static bool IsAllDigits(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] < '0' || text[i] > '9') return false;
            }
            return true;
        }

        private static ushort ResolveButton(string button, out string error)
        {
            error = null;
            string name = (button ?? string.Empty).Trim().ToLowerInvariant();

            if (name.Length == 0 || name == "left") return 1;   // MouseState bit 0
            if (name == "right") return 2;                       // MouseState bit 1
            if (name == "middle")
            {
                error = "middle button is not part of the review schema (left or right only)";
                return 0;
            }

            error = "unknown mouse button '" + button + "' (left or right)";
            return 0;
        }

        private static string DescribeKeys(List<Key> keys)
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0) builder.Append('+');
                builder.Append(keys[i]);
            }
            return builder.ToString();
        }

        // ------------------------------------------------------------------ snapshot

        private sealed class CaptureResult
        {
            public bool ok;
            public bool async;
            public string error;
            public int width;
            public int height;
        }

        private string ResolveSnapshotPath(string requested, int id)
        {
            try
            {
                string leaf = string.IsNullOrWhiteSpace(requested)
                    ? "snap" + id + "_" + SafeName(ActiveSceneName()) + "_" + Time.frameCount + ".png"
                    : requested.Trim();

                if (leaf.Length == 0 || leaf.Contains("..") ||
                    leaf.IndexOf('/') >= 0 || leaf.IndexOf('\\') >= 0 ||
                    leaf.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    Debug.LogError(Tag + " unsafe snapshot name '" + requested + "'");
                    return null;
                }

                if (!leaf.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) leaf += ".png";

                string directory = Path.GetFullPath(_snapshotDir);
                string full = Path.GetFullPath(Path.Combine(directory, leaf));

                if (!full.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    !full.StartsWith(directory + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.LogError(Tag + " snapshot path escaped the review directory: " + full);
                    return null;
                }

                return full;
            }
            catch (Exception exception)
            {
                Debug.LogError(Tag + " snapshot name validation failed: " + exception.Message);
                return null;
            }
        }

        private CaptureResult TryCapture(string path)
        {
            CaptureResult result = new CaptureResult();

            try
            {
                Texture2D texture = ScreenCapture.CaptureScreenshotAsTexture();
                if (texture != null)
                {
                    int width = texture.width;
                    int height = texture.height;
                    byte[] bytes = texture.EncodeToPNG();
                    UnityEngine.Object.Destroy(texture);

                    if (bytes == null || bytes.Length == 0)
                    {
                        result.error = "EncodeToPNG produced no bytes";
                        return result;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllBytes(path, bytes);

                    result.ok = true;
                    result.width = width;
                    result.height = height;
                    return result;
                }
            }
            catch (Exception exception)
            {
                result.error = "CaptureScreenshotAsTexture: " + exception.Message;
            }

            // Fallback: the asynchronous screenshot, which also captures the rendered frame plus
            // overlay UI. The caller verifies the file actually exists before reporting success.
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                ScreenCapture.CaptureScreenshot(path);
                result.ok = true;
                result.async = true;
                result.width = Screen.width;
                result.height = Screen.height;
                result.error = null;
                return result;
            }
            catch (Exception exception)
            {
                result.ok = false;
                result.async = false;
                result.error = (string.IsNullOrEmpty(result.error) ? string.Empty : result.error + " | ") +
                               "CaptureScreenshot: " + exception.Message;
                return result;
            }
        }

        private static bool TryReadPngSize(string path, out int width, out int height)
        {
            width = 0;
            height = 0;

            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] header = new byte[24];
                    int read = 0;
                    while (read < header.Length)
                    {
                        int got = stream.Read(header, read, header.Length - read);
                        if (got <= 0) break;
                        read += got;
                    }

                    if (read < header.Length) return false;
                    if (header[0] != 0x89 || header[1] != 0x50 || header[2] != 0x4E || header[3] != 0x47) return false;
                    if (header[12] != (byte)'I' || header[13] != (byte)'H' ||
                        header[14] != (byte)'D' || header[15] != (byte)'R') return false;

                    width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
                    height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
                    return width > 0 && height > 0;
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning(Tag + " could not read the PNG header of " + path + ": " + exception.Message);
                return false;
            }
        }

        // ------------------------------------------------------------------ ack

        private ReviewAck BuildAck(int id, string action, bool ok, string message, string error,
                                   string screenshot, int width, int height, MovementTelemetry telemetry = null)
        {
            ReviewAck ack = new ReviewAck
            {
                id = id,
                ok = ok,
                action = action,
                message = message ?? string.Empty,
                error = error ?? string.Empty,
                screenshot = screenshot ?? string.Empty,
                scene = ActiveSceneName(),
                frameCount = Time.frameCount,
                realtime = Time.realtimeSinceStartup,
                time = Time.time,
                width = width > 0 ? width : Screen.width,
                height = height > 0 ? height : Screen.height,
                reviewDir = _root ?? string.Empty,
                utc = DateTime.UtcNow.ToString("o"),
                telemetry = telemetry
            };
            return ack;
        }

        private void Finish(ReviewCommand command, bool ok, string message, string screenshot, int width, int height,
                            MovementTelemetry telemetry = null)
        {
            ReleaseHeldInput();

            string action = (command.action ?? string.Empty).Trim().ToLowerInvariant();
            ReviewAck ack = BuildAck(command.id, action, ok, message, ok ? null : message, screenshot, width, height,
                telemetry);
            WriteAck(ack);

            _busy = false;
            _running = null;

            Debug.Log(Tag + " ack id=" + ack.id + " action=" + ack.action + " ok=" + (ack.ok ? "true" : "false") +
                      " scene=" + ack.scene + " frame=" + ack.frameCount + " time=" + ack.realtime.ToString("F3") +
                      " screen=" + ack.width + "x" + ack.height +
                      (string.IsNullOrEmpty(ack.screenshot) ? string.Empty : " png=" + ack.screenshot) +
                      " msg=" + ack.message);
        }

        private void WriteAck(ReviewAck ack)
        {
            WriteJson(_ackPath, JsonUtility.ToJson(ack, true));
        }

        private static bool WriteJson(string path, string payload)
        {
            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(path, payload);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError(Tag + " could not write " + path + ": " + exception.Message);
                return false;
            }
        }

        // ------------------------------------------------------------------ helpers / lifecycle

        private static int ClampFrames(int requested, int fallback, int maximum)
        {
            if (requested <= 0) return fallback;
            return requested > maximum ? maximum : requested;
        }

        /// <summary>
        /// Validates the optional real-time hold. 0 / omitted keeps the frames behaviour; a positive value
        /// is capped at MaxHoldSeconds; NaN, infinity and negatives are refused before anything is pressed.
        /// </summary>
        private static bool TryResolveHoldSeconds(float requested, out float applied, out string error)
        {
            applied = 0f;
            error = null;

            if (float.IsNaN(requested))
            {
                error = "seconds must be a finite positive number of seconds, got NaN";
                return false;
            }

            if (float.IsInfinity(requested))
            {
                error = "seconds must be a finite positive number of seconds, got " + requested;
                return false;
            }

            if (requested < 0f)
            {
                error = "seconds must be positive (omit it, or use 0 to hold by frames), got " + requested;
                return false;
            }

            applied = requested > MaxHoldSeconds ? MaxHoldSeconds : requested;
            return true;
        }

        /// <summary>Ack wording for a real-time hold: what was asked for and what actually elapsed.</summary>
        private static string DescribeSecondsHold(float requested, float applied, float heldSeconds, int framesAdvanced)
        {
            string cap = requested > applied
                ? " (requested " + requested.ToString("F3") + " s capped at " + MaxHoldSeconds.ToString("F3") + " s)"
                : string.Empty;

            return "for " + heldSeconds.ToString("F3") + " s of real time (" + framesAdvanced + " frames)" + cap;
        }

        private static string ActiveSceneName()
        {
            Scene scene = SceneManager.GetActiveScene();
            return string.IsNullOrEmpty(scene.name) ? "<none>" : scene.name;
        }

        private static string SafeName(string text)
        {
            if (string.IsNullOrEmpty(text)) return "scene";
            StringBuilder builder = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_' || c == '-') builder.Append(c);
                else builder.Append('_');
            }
            return builder.ToString();
        }

        private static bool FileExistsSafe(string path)
        {
            try { return File.Exists(path); }
            catch (Exception exception)
            {
                Debug.LogWarning(Tag + " File.Exists failed for " + path + ": " + exception.Message);
                return false;
            }
        }

        private static long FileLengthSafe(string path)
        {
            try { return new FileInfo(path).Length; }
            catch (Exception exception)
            {
                Debug.LogWarning(Tag + " could not stat " + path + ": " + exception.Message);
                return 0;
            }
        }

        private void OnDisable()
        {
            ReleaseHeldInput();
        }

        private void OnApplicationQuit()
        {
            ReleaseHeldInput();
        }

        private void OnDestroy()
        {
            ReleaseHeldInput();

            // Only the devices this bridge created are removed; OS devices are never touched.
            try
            {
                if (_keyboard != null) { InputSystem.RemoveDevice(_keyboard); _keyboard = null; }
                if (_mouse != null) { InputSystem.RemoveDevice(_mouse); _mouse = null; }
                InputSystem.RemoveLayout("EmberdelveReviewKeyboard");
                InputSystem.RemoveLayout("EmberdelveReviewMouse");
            }
            catch (Exception exception)
            {
                Debug.LogWarning(Tag + " could not remove the virtual review devices: " + exception.Message);
            }

            if (_instance == this) _instance = null;
        }
    }
}
#endif
