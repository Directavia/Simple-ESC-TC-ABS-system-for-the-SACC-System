using UdonSharp;
using UnityEngine;

namespace SaccFlightAndVehicles
{
    /// <summary>
    /// PC keyboard shortcuts for ESC/TC mode control.
    /// This is intentionally separate from VehicleLightKeyboardInput so the light shortcuts and ESC/TC shortcuts can be enabled/disabled independently.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class VehicleESCTCKeyboardInput : UdonSharpBehaviour
    {
        [Header("References")]
        public SACCESCController escController;

        [Header("Access")]
        [Tooltip("When true, only the local pilot can use these keyboard shortcuts. Requires SACCESCController to be added to the SACC Entity Extension list so debugLocalPilot becomes true.")]
        public bool requireLocalPilot = true;

        [Header("Keyboard Mapping - Main")]
        [Tooltip("Cycles ESC/TC mode: Off -> Weak -> Medium -> Strong -> Off.")]
        public KeyCode cycleModeKey = KeyCode.B;

        [Tooltip("Toggle ESC/TC On/Off. When turning on from Off, Medium is selected.")]
        public KeyCode toggleKey = KeyCode.None;

        [Tooltip("Increase intervention level by SACCESCController.interventionStep.")]
        public KeyCode increaseKey = KeyCode.RightBracket;

        [Tooltip("Decrease intervention level by SACCESCController.interventionStep.")]
        public KeyCode decreaseKey = KeyCode.LeftBracket;

        [Header("Keyboard Mapping - Direct Presets")]
        public KeyCode offKey = KeyCode.None;
        public KeyCode weakKey = KeyCode.None;
        public KeyCode mediumKey = KeyCode.None;
        public KeyCode strongKey = KeyCode.None;

        [Header("Behavior")]
        [Tooltip("If true, pressing a direct preset key also takes ownership through SACCESCController before syncing the new setting.")]
        public bool allowDirectPresetKeys = true;

        [Header("Debug Read Only")]
        public bool debugInputAllowed;
        public int debugPresetIndex;
        public bool debugEscEnabled;
        public float debugInterventionLevel;
        public string debugLastAction;

        private void Update()
        {
            debugInputAllowed = false;

            if (escController == null)
            {
                debugLastAction = "No ESC controller";
                return;
            }

            if (requireLocalPilot && !escController.debugLocalPilot)
            {
                debugPresetIndex = escController.debugPresetIndex;
                debugEscEnabled = escController.escEnabled;
                debugInterventionLevel = escController.interventionLevel;
                debugLastAction = "Blocked: not local pilot";
                return;
            }

            debugInputAllowed = true;

            if (IsKeyDown(cycleModeKey))
            {
                escController.CycleInterventionPreset();
                debugLastAction = "Cycle preset";
            }
            else if (IsKeyDown(toggleKey))
            {
                escController.ToggleESC();
                debugLastAction = "Toggle ESC/TC";
            }
            else if (IsKeyDown(increaseKey))
            {
                escController.IncreaseIntervention();
                debugLastAction = "Increase intervention";
            }
            else if (IsKeyDown(decreaseKey))
            {
                escController.DecreaseIntervention();
                debugLastAction = "Decrease intervention";
            }
            else if (allowDirectPresetKeys && IsKeyDown(offKey))
            {
                escController.SetESCOff();
                debugLastAction = "Set Off";
            }
            else if (allowDirectPresetKeys && IsKeyDown(weakKey))
            {
                escController.SetWeakPreset();
                debugLastAction = "Set Weak";
            }
            else if (allowDirectPresetKeys && IsKeyDown(mediumKey))
            {
                escController.SetMediumPreset();
                debugLastAction = "Set Medium";
            }
            else if (allowDirectPresetKeys && IsKeyDown(strongKey))
            {
                escController.SetStrongPreset();
                debugLastAction = "Set Strong";
            }

            debugPresetIndex = escController.debugPresetIndex;
            debugEscEnabled = escController.escEnabled;
            debugInterventionLevel = escController.interventionLevel;
        }

        private bool IsKeyDown(KeyCode key)
        {
            if (key == KeyCode.None) return false;
            return Input.GetKeyDown(key);
        }
    }
}
