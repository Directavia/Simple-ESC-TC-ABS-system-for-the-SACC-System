using UdonSharp;
using UnityEngine;

namespace SaccFlightAndVehicles
{
    /// <summary>
    /// SACC DFUNC entry for controlling ESC/TC strength from the SACC function menu.
    /// Add this UdonBehaviour to the SACC Entity function list / left dial list like any other DFUNC.
    /// Default mode cycles Off -> Weak -> Medium -> Strong -> Off when the VR trigger is pressed.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class DFUNC_ESCTCModeControl : UdonSharpBehaviour
    {
        [Header("ESC / TC Controller")]
        public SACCESCController escController;

        [Tooltip("0 Cycle Off/Weak/Medium/Strong, 1 Increase, 2 Decrease, 3 Toggle, 4 On, 5 Off, 6 Weak, 7 Medium, 8 Strong")]
        public int mode = 0;

        [Header("SACC Dial Indicator")]
        [Tooltip("Optional SACC dial indicator object. It is enabled when ESC/TC is not Off.")]
        public GameObject Dial_funcon;

        [Header("Optional Mode Indicator Objects")]
        public GameObject offIndicatorObject;
        public GameObject weakIndicatorObject;
        public GameObject mediumIndicatorObject;
        public GameObject strongIndicatorObject;

        [Header("VR Trigger")]
        public float triggerThreshold = 0.75f;

        [Header("Debug Read Only")]
        public int debugPresetIndex;
        public bool debugEscEnabled;
        public string debugStatus;

        [System.NonSerialized] public bool LeftDial = false;
        [System.NonSerialized] public int DialPosition = -999;
        [System.NonSerialized] public SaccEntity EntityControl;

        private bool triggerLastFrame;

        public void SFEXT_L_EntityStart()
        {
            gameObject.SetActive(false);
            RefreshIndicators();
        }

        public void DFUNC_Selected()
        {
            gameObject.SetActive(true);
            RefreshIndicators();
        }

        public void DFUNC_Deselected()
        {
            gameObject.SetActive(false);
            RefreshIndicators();
        }

        public void SFEXT_O_PilotEnter()
        {
            RefreshIndicators();
        }

        public void SFEXT_O_PilotExit()
        {
            gameObject.SetActive(false);
            RefreshIndicators();
        }

        private void Update()
        {
            float trigger;
            if (LeftDial) trigger = Input.GetAxisRaw("Oculus_CrossPlatform_PrimaryIndexTrigger");
            else trigger = Input.GetAxisRaw("Oculus_CrossPlatform_SecondaryIndexTrigger");

            if (trigger > triggerThreshold)
            {
                if (!triggerLastFrame)
                {
                    Press();
                }
                triggerLastFrame = true;
            }
            else
            {
                triggerLastFrame = false;
            }

            // Keep the small dial indicator current if the mode is changed by keyboard/in-world buttons.
            RefreshIndicators();
        }

        public void KeyboardInput()
        {
            Press();
        }

        public void Press()
        {
            if (escController == null)
            {
                debugStatus = "No ESC controller";
                return;
            }

            if (mode == 0) escController.CycleInterventionPreset();
            else if (mode == 1) escController.IncreaseIntervention();
            else if (mode == 2) escController.DecreaseIntervention();
            else if (mode == 3) escController.ToggleESC();
            else if (mode == 4) escController.SetESCOn();
            else if (mode == 5) escController.SetESCOff();
            else if (mode == 6) escController.SetWeakPreset();
            else if (mode == 7) escController.SetMediumPreset();
            else if (mode == 8) escController.SetStrongPreset();

            RefreshIndicators();
        }

        public void RefreshIndicators()
        {
            if (escController == null)
            {
                debugPresetIndex = -1;
                debugEscEnabled = false;
                if (Dial_funcon != null) Dial_funcon.SetActive(false);
                SetObject(offIndicatorObject, false);
                SetObject(weakIndicatorObject, false);
                SetObject(mediumIndicatorObject, false);
                SetObject(strongIndicatorObject, false);
                return;
            }

            int preset = escController.presetIndex;
            bool enabled = escController.escEnabled;

            debugPresetIndex = preset;
            debugEscEnabled = enabled;
            debugStatus = enabled ? "ESC/TC enabled" : "ESC/TC off";

            if (Dial_funcon != null)
            {
                Dial_funcon.SetActive(enabled);
            }

            SetObject(offIndicatorObject, preset == 0 || !enabled);
            SetObject(weakIndicatorObject, enabled && preset == 1);
            SetObject(mediumIndicatorObject, enabled && preset == 2);
            SetObject(strongIndicatorObject, enabled && preset == 3);
        }

        private void SetObject(GameObject obj, bool active)
        {
            if (obj != null && obj.activeSelf != active)
            {
                obj.SetActive(active);
            }
        }
    }
}
