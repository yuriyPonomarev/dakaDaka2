using UnityEngine.InputSystem;

namespace Emberdelve.InputLayer
{
    /// <summary>
    /// Single source of truth for the control scheme. The editor bootstrap writes
    /// Assets/Emberdelve/Data/EmberdelveInputActions.inputactions from BuildFileJson(), and this class
    /// also builds the same asset at runtime when that asset is missing or failed to import.
    ///
    /// Two maps: "Player" (gameplay, enabled only while playing) and "System" (pause, always enabled
    /// so the pause key still works while gameplay input is switched off).
    /// </summary>
    public static class EmberdelveInputFallback
    {
        public const string PlayerMapName = "Player";
        public const string SystemMapName = "System";
        public const string AssetName = "EmberdelveInputActions";

        public static InputActionAsset Build() => InputActionAsset.FromJson(BuildFileJson());

        public static string BuildFileJson()
        {
            InputActionMap player = BuildPlayerMap();
            InputActionMap system = BuildSystemMap();

            string maps = MergeMapsArrays(player.ToJson(), system.ToJson());
            player.Dispose();
            system.Dispose();

            return "{\n    \"version\": 1,\n    \"name\": \"" + AssetName + "\",\n    \"maps\": " + maps + "\n}\n";
        }

        private static string MergeMapsArrays(string firstJson, string secondJson)
        {
            string first = ExtractMapsArray(firstJson);
            string second = ExtractMapsArray(secondJson);

            if (string.IsNullOrEmpty(first)) return second;
            if (string.IsNullOrEmpty(second)) return first;

            return first.Substring(0, first.Length - 1) + "," + second.Substring(1);
        }

        private static string ExtractMapsArray(string mapJson)
        {
            int open = mapJson.IndexOf('[');
            int close = mapJson.LastIndexOf(']');
            if (open < 0 || close <= open) return string.Empty;
            return mapJson.Substring(open, close - open + 1);
        }

        private static InputActionMap BuildPlayerMap()
        {
            InputActionMap map = new InputActionMap(PlayerMapName);

            InputAction move = map.AddAction("Move", type: InputActionType.Value, expectedControlLayout: "Vector2");
            move.AddBinding("<Gamepad>/leftStick", groups: "Gamepad");
            move.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/w", groups: "Keyboard&Mouse")
                .With("Down", "<Keyboard>/s", groups: "Keyboard&Mouse")
                .With("Left", "<Keyboard>/a", groups: "Keyboard&Mouse")
                .With("Right", "<Keyboard>/d", groups: "Keyboard&Mouse");

            InputAction look = map.AddAction("Look", type: InputActionType.Value, expectedControlLayout: "Vector2");
            look.AddBinding("<Mouse>/delta", groups: "Keyboard&Mouse");
            look.AddBinding("<Gamepad>/rightStick", processors: "Scale2(x=2.5,y=2.5)", groups: "Gamepad");

            InputAction jump = map.AddAction("Jump", type: InputActionType.Button, expectedControlLayout: "Button");
            jump.AddBinding("<Keyboard>/space", groups: "Keyboard&Mouse");
            jump.AddBinding("<Gamepad>/buttonSouth", groups: "Gamepad");

            InputAction sprint = map.AddAction("Sprint", type: InputActionType.Button, expectedControlLayout: "Button");
            sprint.AddBinding("<Keyboard>/leftShift", groups: "Keyboard&Mouse");
            sprint.AddBinding("<Gamepad>/leftStickPress", groups: "Gamepad");

            InputAction crouch = map.AddAction("Crouch", type: InputActionType.Button, expectedControlLayout: "Button");
            crouch.AddBinding("<Keyboard>/ctrl", groups: "Keyboard&Mouse");
            crouch.AddBinding("<Gamepad>/buttonEast", groups: "Gamepad");

            InputAction primary = map.AddAction("PrimaryAttack", type: InputActionType.Button, expectedControlLayout: "Button");
            primary.AddBinding("<Mouse>/leftButton", groups: "Keyboard&Mouse");
            primary.AddBinding("<Gamepad>/rightTrigger", groups: "Gamepad");

            InputAction secondary = map.AddAction("SecondaryAction", type: InputActionType.Button, expectedControlLayout: "Button");
            secondary.AddBinding("<Mouse>/rightButton", groups: "Keyboard&Mouse");
            secondary.AddBinding("<Gamepad>/leftTrigger", groups: "Gamepad");

            InputAction interact = map.AddAction("Interact", type: InputActionType.Button, expectedControlLayout: "Button");
            interact.AddBinding("<Keyboard>/e", groups: "Keyboard&Mouse");
            interact.AddBinding("<Gamepad>/buttonNorth", groups: "Gamepad");

            InputAction reload = map.AddAction("Reload", type: InputActionType.Button, expectedControlLayout: "Button");
            reload.AddBinding("<Keyboard>/r", groups: "Keyboard&Mouse");

            InputAction swap = map.AddAction("SwapWeapon", type: InputActionType.Button, expectedControlLayout: "Button");
            swap.AddBinding("<Keyboard>/q", groups: "Keyboard&Mouse");
            swap.AddBinding("<Gamepad>/rightShoulder", groups: "Gamepad");

            // Skill slots are deliberately not 1-4: those are the item hotbar. F and G sit under the left
            // hand next to the movement keys and are free of every existing binding.
            InputAction skill1 = map.AddAction("Skill1", type: InputActionType.Button, expectedControlLayout: "Button");
            skill1.AddBinding("<Keyboard>/f", groups: "Keyboard&Mouse");
            skill1.AddBinding("<Gamepad>/leftShoulder", groups: "Gamepad");

            InputAction skill2 = map.AddAction("Skill2", type: InputActionType.Button, expectedControlLayout: "Button");
            skill2.AddBinding("<Keyboard>/g", groups: "Keyboard&Mouse");
            skill2.AddBinding("<Gamepad>/buttonWest", groups: "Gamepad");

            InputAction inventory = map.AddAction("Inventory", type: InputActionType.Button, expectedControlLayout: "Button");
            inventory.AddBinding("<Keyboard>/i", groups: "Keyboard&Mouse");
            inventory.AddBinding("<Keyboard>/tab", groups: "Keyboard&Mouse");

            for (int i = 0; i < 4; i++)
            {
                InputAction slot = map.AddAction("UseItem" + (i + 1), type: InputActionType.Button, expectedControlLayout: "Button");
                slot.AddBinding("<Keyboard>/" + (i + 1), groups: "Keyboard&Mouse");
            }

            return map;
        }

        private static InputActionMap BuildSystemMap()
        {
            InputActionMap map = new InputActionMap(SystemMapName);

            InputAction pause = map.AddAction("Pause", type: InputActionType.Button, expectedControlLayout: "Button");
            pause.AddBinding("<Keyboard>/escape", groups: "Keyboard&Mouse");
            pause.AddBinding("<Gamepad>/start", groups: "Gamepad");

            InputAction inventory = map.AddAction("Inventory", type: InputActionType.Button, expectedControlLayout: "Button");
            inventory.AddBinding("<Keyboard>/i", groups: "Keyboard&Mouse");
            inventory.AddBinding("<Keyboard>/tab", groups: "Keyboard&Mouse");

            return map;
        }
    }
}
