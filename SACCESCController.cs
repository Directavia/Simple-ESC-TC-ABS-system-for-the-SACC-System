using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace SaccFlightAndVehicles
{
    [DefaultExecutionOrder(1450)] // after SaccGroundVehicle so wheel/engine overrides are applied last
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class SACCESCController : UdonSharpBehaviour
    {
        [Header("Required References")]
        public SaccGroundVehicle sgvControl;
        public VehicleLightController lightController;
        public Rigidbody vehicleRigidbody;
        public Transform vehicleRoot;

        [Header("Optional ABS Coordination")]
        [Tooltip("Optional. If assigned, ESC can avoid adding wheel brake while ABS is actively releasing brakes.")]
        public SACCABSController absController;
        public bool avoidEscWheelBrakeWhileAbsActive = true;

        [Header("ESC Enable / Ownership")]
        public bool escEnabled = true;
        public bool requireLocalPilot = true;
        public bool onlyRunWhenOwner = false;
        public bool sendEscToLightController = true;
        [Tooltip("When true, ownership is taken on pilot enter so correction is applied by the driver.")]
        public bool takeOwnershipOnPilotEnter = true;

        [Header("ESC Detection")]
        [Tooltip("Below this speed ESC is disabled.")]
        public float minEscSpeedKmh = 18f;
        [Tooltip("ESC begins intervention when the angle between vehicle forward and actual velocity exceeds this value.")]
        public float activationSlipAngleDeg = 8f;
        [Tooltip("Above this slip angle ESC stops trying to correct, allowing spin/drift instead of fighting it forever.")]
        public float maxRecoverableSlipAngleDeg = 65f;
        [Tooltip("Disable ESC while the car is moving backward relative to its forward direction.")]
        public bool disableWhenMovingBackward = true;
        [Tooltip("Extra allowed slip angle at full steering input, reducing false intervention during normal cornering.")]
        public float steeringAllowanceAtFullInputDeg = 6f;
        [Tooltip("Keeps ESC active briefly after slip drops below threshold to avoid flicker/chatter.")]
        public float escHoldAfterSlipLost = 0.16f;

        [Header("Intervention Strength / Presets")]
        [Tooltip("0 = no correction, 1 = strongest correction. Can be changed by world buttons.")]
        [Range(0f, 1f)] public float interventionLevel = 0.65f;
        public bool syncInterventionLevel = true;
        public float interventionStep = 0.1f;
        [Tooltip("Preset levels used by CycleInterventionPreset and selector buttons.")]
        [Range(0f, 1f)] public float weakPresetLevel = 0.35f;
        [Range(0f, 1f)] public float mediumPresetLevel = 0.65f;
        [Range(0f, 1f)] public float strongPresetLevel = 1.0f;
        [Tooltip("0=Off, 1=Weak, 2=Medium, 3=Strong. This is synced for UI/debug consistency.")]
        public int presetIndex = 2;

        [Header("Tire / Wheel Brake Intervention")]
        public bool applyDifferentialWheelBraking = true;
        [Tooltip("SaccWheel brake variable name. Usually 'Brake'.")]
        public string brakeVariableName = "Brake";
        [Tooltip("If true, driver brake input is preserved when ESC writes selected wheels.")]
        public bool preserveDriverBrakeInput = true;
        [Tooltip("Minimum ESC brake pressure at low severity.")]
        [Range(0f, 1f)] public float escBrakePressureAtLowSeverity = 0.12f;
        [Tooltip("Maximum ESC brake pressure at high severity.")]
        [Range(0f, 1f)] public float escBrakePressureAtHighSeverity = 0.55f;
        [Tooltip("If true, the selected side front wheel(s) receive ESC brake.")]
        public bool useFrontWheelsForEscBrake = true;
        [Tooltip("If true, the selected side rear wheel(s) receive ESC brake.")]
        public bool useRearWheelsForEscBrake = true;

        [Header("ESC Brake Wheels - Left / Right")]
        public UdonSharpBehaviour[] frontLeftBrakeWheels;
        public UdonSharpBehaviour[] frontRightBrakeWheels;
        public UdonSharpBehaviour[] rearLeftBrakeWheels;
        public UdonSharpBehaviour[] rearRightBrakeWheels;

        [Header("Brake Input Source")]
        [Tooltip("Optional. Used only to preserve driver brake input while ESC adds differential braking.")]
        public DFUNC_CarBrake carBrakeFunction;
        public bool readBrakeStrengthMultipliersFromCarBrake = true;
        [Range(0f, 2f)] public float frontBrakeStrengthMultiplier = 1f;
        [Range(0f, 2f)] public float rearBrakeStrengthMultiplier = 1f;

        [Header("Engine Power Intervention")]
        public bool applyEnginePowerCut = true;
        [Tooltip("Uses SaccGroundVehicle.DriveSpeed as the engine power output limiter. This is restored when ESC is inactive.")]
        public bool cutEngineByDriveSpeed = true;
        [Tooltip("Do not cut engine below this throttle input.")]
        [Range(0f, 1f)] public float engineCutMinThrottleInput = 0.05f;
        [Tooltip("Engine multiplier at low severity.")]
        [Range(0f, 1f)] public float engineMultiplierAtLowSeverity = 0.75f;
        [Tooltip("Engine multiplier at high severity.")]
        [Range(0f, 1f)] public float engineMultiplierAtHighSeverity = 0.20f;
        [Tooltip("How fast the engine cut multiplier moves toward the target.")]
        public float engineCutResponse = 12f;
        [Tooltip("How fast engine power recovers after ESC stops.")]
        public float engineRestoreResponse = 8f;

        [Header("Traction Control / Driven Wheel Slip")]
        [Tooltip("Integrates traction control into ESC. When driven wheels spin faster than vehicle speed, engine power is reduced.")]
        public bool enableTractionControl = true;
        [Tooltip("When true, ESC Off also disables TC. Recommended for a single ESC/TC controller.")]
        public bool tractionControlUsesEscEnabled = true;
        [Range(0f, 1f)] public float tcThrottleThreshold = 0.10f;
        [Tooltip("Below this speed TC is disabled to avoid false positives when starting from rest.")]
        public float minTcSpeedKmh = 5f;
        [Tooltip("TC is disabled when velocity direction deviates too far from vehicle forward direction.")]
        public float maxTcForwardVelocityAngleDeg = 55f;
        public bool disableTcWhenMovingBackward = true;
        public bool disableTcWhileBraking = true;
        [Range(0f, 1f)] public float tcBrakeInputDisableThreshold = 0.10f;
        [Tooltip("Driven wheel spin threshold when interventionLevel=1. Lower means earlier TC intervention.")]
        [Range(0.01f, 3f)] public float tcSlipThresholdAtFullIntervention = 0.12f;
        [Tooltip("Driven wheel spin threshold when interventionLevel=0. Higher means later/weaker TC intervention.")]
        [Range(0.01f, 3f)] public float tcSlipThresholdAtLowIntervention = 0.40f;
        [Tooltip("Slip ratio that maps to full TC severity. Example: 1.0 means wheel speed is 200% of vehicle speed.")]
        [Range(0.05f, 5f)] public float maxTcSlipRatioForFullSeverity = 1.0f;
        [Tooltip("Keeps TC active briefly after slip drops below threshold to avoid flicker/chatter.")]
        public float tcHoldAfterSlipLost = 0.12f;

        [Header("Gear Shift Power Suppression")]
        [Tooltip("When SGV shift torque management is active, force engine multiplier to this value.")]
        [Range(0f, 1f)] public float shiftSuppressMultiplier = 0.12f;
        [Tooltip("How fast the suppression engages.")]
        public float shiftSuppressAttackResponse = 25f;
        [Tooltip("How fast engine power recovers after the shift ends.")]
        public float shiftSuppressReleaseResponse = 8f;

        [Header("Traction Control Pulse Modulation")]
        [Tooltip("Enable rapid torque pulsing instead of constant power cut for a more natural feel.")]
        public bool tcUsePulseModulation = true;
        [Tooltip("Period of one On/Off cycle in seconds (e.g. 0.06 = ~16 Hz).")]
        public float tcPulsePeriod = 0.06f;
        [Tooltip("Minimum engine multiplier during pulse Off phase (0 = full cut, 0.3 = partial cut).")]
        [Range(0f, 1f)] public float tcPulseMinMultiplier = 0.25f;
        [Tooltip("Maximum engine multiplier during pulse On phase (usually 1).")]
        [Range(0.5f, 1f)] public float tcPulseMaxMultiplier = 1.0f;

        [Header("Traction Control Extreme Slip Failsafe")]
        [Tooltip("Keeps TC from dropping out when driven wheel speed is far higher than vehicle speed. This is especially useful when visual wheel rotation aliases at very high RPM.")]
        public bool enableTcExtremeSlipHold = true;
        [Tooltip("If slip ratio reaches this value, TC treats it as extreme wheelspin and holds strong intervention briefly. 1.5 means wheel surface speed is about 250% of vehicle reference speed.")]
        [Range(0.1f, 5f)] public float tcExtremeSlipRatio = 1.5f;
        [Tooltip("Minimum TC severity while the extreme-slip hold is active.")]
        [Range(0f, 1f)] public float tcExtremeSlipMinimumSeverity = 0.85f;
        [Tooltip("How long strong TC intervention is held after extreme slip is detected.")]
        public float tcExtremeSlipHoldTime = 0.55f;
        [Tooltip("Holds the last meaningful TC severity briefly after slip detection drops out, preventing visual-rotation aliasing from making TC pulse off.")]
        public bool holdTcSeverityAfterSlipLost = true;
        [Tooltip("Minimum held severity after normal TC slip detection. Used only while holdTcSeverityAfterSlipLost is enabled.")]
        [Range(0f, 1f)] public float tcHeldSeverityMinimum = 0.35f;
        [Tooltip("How long the held TC severity is kept after slip detection drops out.")]
        public float tcSeverityHoldTime = 0.25f;

        [Header("Traction Control Driven Wheel Speed Source")]
        public Transform[] drivenWheelVisuals;
        public UdonSharpBehaviour[] drivenWheelSources;
        public bool useDrivenVisualWheelRotation = true;
        [Tooltip("0=X, 1=Y, 2=Z. Most vehicle wheels rotate around local X, but this depends on the model.")]
        public int drivenVisualRotationAxis = 0;
        public float defaultDrivenWheelRadius = 0.34f;
        public float[] drivenWheelRadii;
        [Tooltip("Only enable if you know the driven wheel script exposes this variable.")]
        public bool useDrivenWheelProgramVariable = false;
        public string drivenWheelSpeedVariableName = "WheelRPS";
        [Tooltip("0=RPS, 1=RPM, 2=rad/s, 3=m/s, 4=km/h")]
        public int drivenWheelSpeedVariableUnits = 0;
        public bool useDrivenGroundedVariable = true;
        public string drivenGroundedVariableName = "Grounded";
        public bool assumeDrivenGroundedIfNoSource = true;

        [Header("Traction Control Engine / Optional Brake")]
        public bool tcCutEnginePower = true;
        [Range(0f, 1f)] public float tcEngineMultiplierAtLowSeverity = 0.65f;
        [Range(0f, 1f)] public float tcEngineMultiplierAtHighSeverity = 0.15f;
        public float tcEngineCutResponse = 16f;
        public float tcEngineRestoreResponse = 10f;
        [Tooltip("Optional. Applies a small brake to driven wheels during wheelspin. Leave off at first if power cut alone is enough.")]
        public bool tcBrakeSpinningDrivenWheels = false;
        public UdonSharpBehaviour[] drivenBrakeWheels;
        [Range(0f, 1f)] public float tcBrakePressureAtLowSeverity = 0.04f;
        [Range(0f, 1f)] public float tcBrakePressureAtHighSeverity = 0.22f;

        [Header("ESC / TC Dashboard Indicator Output")]
        [Tooltip("When true, the ESC active dashboard indicator flashes while ESC is correcting vehicle stability.")]
        public bool indicatorFlashesForEscIntervention = true;
        [Tooltip("When true, the ESC/TC active dashboard indicator can flash for traction control. TC must pass the strong-intervention thresholds below.")]
        public bool indicatorFlashesForTcIntervention = true;
        [Tooltip("TC indicator only flashes when TC severity is at or above this value. This prevents light flicker during weak TC trimming.")]
        [Range(0f, 1f)] public float tcIndicatorMinSeverity = 0.30f;
        [Tooltip("Optional additional condition: TC indicator only flashes when TC engine multiplier is at or below this value.")]
        public bool tcIndicatorUsesEngineCutThreshold = false;
        [Range(0f, 1f)] public float tcIndicatorMaxEngineMultiplier = 0.70f;
        [Tooltip("Keeps the dashboard indicator on briefly after TC drops below the threshold, preventing flicker.")]
        public float tcIndicatorHoldAfterStrongTcLost = 0.12f;

        [Header("Optional Rigidbody Fallback Correction")]
        [Tooltip("Normally OFF. Enable only if tire/engine intervention is too weak for your vehicle.")]
        public bool applyRigidbodyFallbackCorrection = false;
        public float yawCorrectionStrength = 2f;
        public float yawDampingStrength = 0.7f;
        public float maxYawAcceleration = 5f;
        public float lateralDampingStrength = 1.0f;
        public float maxLateralAcceleration = 4f;

        [Header("Debug Read Only")]
        public bool debugLocalPilot;
        public bool debugIsOwner;
        public bool debugEscAllowed;
        public bool debugEscActive;
        public bool debugSlipDetected;
        public bool debugDisabledLowSpeed;
        public bool debugDisabledTooSideways;
        public bool debugDisabledBackward;
        public bool debugAbsCurrentlyActive;
        public float debugSpeedKmh;
        public float debugForwardSpeedKmh;
        public float debugLateralSpeedKmh;
        public float debugSlipAngleDeg;
        public float debugActivationThresholdDeg;
        public float debugSteeringInput;
        public float debugThrottleInput;
        public float debugSeverity;
        public float debugEscBrakePressure;
        public float debugEngineMultiplier;
        public bool debugTcAllowed;
        public bool debugTcActive;
        public bool debugTcSlipDetected;
        public bool debugTcDisabledLowSpeed;
        public bool debugTcDisabledAngle;
        public bool debugTcDisabledBackward;
        public bool debugTcDisabledBraking;
        public float debugTcSlipThreshold;
        public float debugTcWorstSlip;
        public int debugTcWorstWheelIndex = -1;
        public float debugTcWorstWheelSurfaceSpeedKmh;
        public float debugTcSeverity;
        public bool debugTcExtremeSlipHoldActive;
        public bool debugTcSeverityHoldActive;
        public float debugTcHeldSeverity;
        public float debugTcEngineMultiplier;
        public float debugCombinedEngineMultiplier;
        public float debugTcBrakePressure;
        public bool debugTcStrongForIndicator;
        public bool debugStabilityIndicatorActive;
        public float debugYawRate;
        public float debugYawAccelerationCommand;
        public float debugLateralAccelerationCommand;
        public int debugCorrectionSide; // -1 left, 0 none, 1 right
        public float debugInterventionLevel;
        public int debugPresetIndex;
        public string debugStatus;

        [UdonSynced] private float syncedInterventionLevel = 0.65f;
        [UdonSynced] private bool syncedEscEnabled = true;
        [UdonSynced] private int syncedPresetIndex = 2;

        private bool localPilot;
        private bool isOwner;
        private bool escActive;
        private bool lastSentEscActive;
        private bool lastSentEscEnabled = true;
        private bool forceSendEscEnabledState;
        private bool wasEscActiveLastFrame;
        private bool tcActive;
        private bool wasTcActiveLastFrame;
        private float escHoldTimer;
        private float tcHoldTimer;
        private float tcIndicatorHoldTimer;
        private float tcExtremeSlipHoldTimer;
        private float tcSeverityHoldTimer;
        private float tcHeldSeverity;
        private float currentEngineMultiplier = 1f;
        private float currentEscEngineMultiplier = 1f;
        private float currentTcEngineMultiplier = 1f;
        private float originalDriveSpeed;
        private bool originalDriveSpeedCaptured;
        private float[] previousDrivenWheelAngle;
        private bool[] previousDrivenWheelAngleValid;
        private int drivenWheelCount;
        // 脉冲调制计时器
        private float tcPulseTimer;
        // 换挡期间强制发动机低输出
        private float currentShiftSuppressMultiplier = 1f;

        private void Start()
        {
            if (vehicleRoot == null && vehicleRigidbody != null)
            {
                vehicleRoot = vehicleRigidbody.transform;
            }

            isOwner = Networking.LocalPlayer != null && Networking.IsOwner(gameObject);
            debugIsOwner = isOwner;

            syncedInterventionLevel = interventionLevel;
            syncedEscEnabled = escEnabled;
            syncedPresetIndex = presetIndex;
            debugInterventionLevel = interventionLevel;
            debugPresetIndex = presetIndex;
            debugEngineMultiplier = 1f;
            debugTcEngineMultiplier = 1f;
            debugCombinedEngineMultiplier = 1f;

            InitializeDrivenWheelState();
            CaptureOriginalDriveSpeed();
            SendLightEnabledStateIfOwner();
        }

        private void InitializeDrivenWheelState()
        {
            drivenWheelCount = 0;
            if (drivenWheelVisuals != null && drivenWheelVisuals.Length > drivenWheelCount) drivenWheelCount = drivenWheelVisuals.Length;
            if (drivenWheelSources != null && drivenWheelSources.Length > drivenWheelCount) drivenWheelCount = drivenWheelSources.Length;
            if (drivenBrakeWheels != null && drivenBrakeWheels.Length > drivenWheelCount) drivenWheelCount = drivenBrakeWheels.Length;

            previousDrivenWheelAngle = new float[drivenWheelCount];
            previousDrivenWheelAngleValid = new bool[drivenWheelCount];
        }

        public override void OnDeserialization()
        {
            if (syncInterventionLevel)
            {
                interventionLevel = Mathf.Clamp01(syncedInterventionLevel);
                escEnabled = syncedEscEnabled;
                presetIndex = syncedPresetIndex;
                debugInterventionLevel = interventionLevel;
                debugPresetIndex = presetIndex;
            }
        }

        private void LateUpdate()
        {
            UpdateESC();
        }

        private void UpdateESC()
        {
            if (vehicleRigidbody == null)
            {
                SetESCInactive("No vehicleRigidbody");
                return;
            }

            if (vehicleRoot == null)
            {
                vehicleRoot = vehicleRigidbody.transform;
            }

            if (onlyRunWhenOwner && !isOwner)
            {
                SetESCInactive("Not owner");
                return;
            }

            if (requireLocalPilot && !localPilot)
            {
                SetESCInactive("No local pilot");
                return;
            }

            CaptureOriginalDriveSpeed();

            Vector3 velocity = vehicleRigidbody.velocity;
            Vector3 flatVelocity = velocity;
            flatVelocity.y = 0f;

            float speedKmh = flatVelocity.magnitude * 3.6f;
            debugSpeedKmh = speedKmh;

            Vector3 localVelocity = vehicleRoot.InverseTransformDirection(velocity);
            debugForwardSpeedKmh = localVelocity.z * 3.6f;
            debugLateralSpeedKmh = localVelocity.x * 3.6f;

            float signedSlipAngle = GetSignedForwardVelocityAngle(flatVelocity);
            float absSlipAngle = Mathf.Abs(signedSlipAngle);
            debugSlipAngleDeg = signedSlipAngle;

            float steeringInput = GetSteeringInput();
            debugSteeringInput = steeringInput;

            float threshold = activationSlipAngleDeg + Mathf.Abs(steeringInput) * steeringAllowanceAtFullInputDeg;
            debugActivationThresholdDeg = threshold;

            bool disabledLowSpeed = speedKmh < minEscSpeedKmh;
            bool disabledTooSideways = absSlipAngle > maxRecoverableSlipAngleDeg;
            bool disabledBackward = disableWhenMovingBackward && localVelocity.z < -0.5f;

            debugDisabledLowSpeed = disabledLowSpeed;
            debugDisabledTooSideways = disabledTooSideways;
            debugDisabledBackward = disabledBackward;

            bool allowed = escEnabled
                && interventionLevel > 0.001f
                && !disabledLowSpeed
                && !disabledTooSideways
                && !disabledBackward;

            debugEscAllowed = allowed;

            bool slipDetected = allowed && absSlipAngle >= threshold;
            debugSlipDetected = slipDetected;

            if (slipDetected)
            {
                escHoldTimer = escHoldAfterSlipLost;
            }
            else if (escHoldTimer > 0f)
            {
                escHoldTimer -= Time.deltaTime;
            }

            escActive = allowed && (slipDetected || escHoldTimer > 0f);
            debugEscActive = escActive;

            float severity = 0f;
            if (allowed)
            {
                float range = Mathf.Max(maxRecoverableSlipAngleDeg - threshold, 0.1f);
                severity = Mathf.Clamp01((absSlipAngle - threshold) / range);
                if (escActive && severity < 0.05f) severity = 0.05f;
                severity *= Mathf.Clamp01(interventionLevel);
            }
            debugSeverity = severity;

            debugThrottleInput = GetThrottleInput();
            debugAbsCurrentlyActive = absController != null && absController.debugAbsActive;

            // ---- 换挡感知功率抑制 ----
            bool isShifting = false;
            if (sgvControl != null)
            {
                // SGV_ShiftTorqueActive 在换挡扭矩管理期间为 true
                isShifting = (bool)sgvControl.GetProgramVariable("SGV_ShiftTorqueActive");
            }
            float shiftTarget = isShifting ? shiftSuppressMultiplier : 1f;
            float response = isShifting ? shiftSuppressAttackResponse : shiftSuppressReleaseResponse;
            currentShiftSuppressMultiplier = Mathf.MoveTowards(currentShiftSuppressMultiplier, shiftTarget, response * Time.deltaTime);

            if (escActive)
            {
                int correctionSide = signedSlipAngle > 0f ? 1 : -1;
                debugCorrectionSide = correctionSide;

                ApplyTireAndEngineIntervention(correctionSide, severity);

                if (applyRigidbodyFallbackCorrection)
                {
                    ApplyRigidbodyFallbackCorrection(signedSlipAngle, localVelocity, severity);
                }
                else
                {
                    debugYawAccelerationCommand = 0f;
                    debugLateralAccelerationCommand = 0f;
                }

                debugStatus = "ESC active: tire/engine intervention";
            }
            else
            {
                debugCorrectionSide = 0;
                debugEscBrakePressure = 0f;
                RestoreEnginePower();

                if (wasEscActiveLastFrame)
                {
                    RestoreEscWheelBrakesToDriverBrake();
                }

                debugYawAccelerationCommand = 0f;
                debugLateralAccelerationCommand = 0f;

                if (!allowed)
                {
                    if (!escEnabled) debugStatus = "ESC disabled";
                    else if (disabledLowSpeed) debugStatus = "ESC disabled: low speed";
                    else if (disabledTooSideways) debugStatus = "ESC disabled: too sideways";
                    else if (disabledBackward) debugStatus = "ESC disabled: moving backward";
                    else debugStatus = "ESC not allowed";
                }
                else
                {
                    debugStatus = "No ESC slip";
                }
            }

            wasEscActiveLastFrame = escActive;

            UpdateTractionControl(speedKmh, localVelocity, absSlipAngle, debugThrottleInput);

            bool stabilityActiveForIndicator = ShouldShowStabilityIndicator();
            debugStabilityIndicatorActive = stabilityActiveForIndicator;
            SendLightStateIfNeeded(speedKmh, stabilityActiveForIndicator);
        }


        private bool ShouldShowStabilityIndicator()
        {
            bool escForIndicator = indicatorFlashesForEscIntervention && escActive;

            bool tcStrongBySeverity = debugTcSeverity >= tcIndicatorMinSeverity;
            bool tcStrongByEngineCut = !tcIndicatorUsesEngineCutThreshold || debugTcEngineMultiplier <= tcIndicatorMaxEngineMultiplier;
            bool tcStrongNow = indicatorFlashesForTcIntervention && tcActive && tcStrongBySeverity && tcStrongByEngineCut;

            if (tcStrongNow)
            {
                tcIndicatorHoldTimer = tcIndicatorHoldAfterStrongTcLost;
            }
            else if (tcIndicatorHoldTimer > 0f)
            {
                tcIndicatorHoldTimer -= Time.deltaTime;
            }

            debugTcStrongForIndicator = indicatorFlashesForTcIntervention && tcActive && (tcStrongNow || tcIndicatorHoldTimer > 0f);

            return escForIndicator || debugTcStrongForIndicator;
        }

        private void UpdateTractionControl(float speedKmh, Vector3 localVelocity, float absSlipAngle, float throttleInput)
        {
            float brakeInput = GetBrakeInput();

            bool disabledLowSpeed = speedKmh < minTcSpeedKmh;
            bool disabledAngle = absSlipAngle > maxTcForwardVelocityAngleDeg;
            bool disabledBackward = disableTcWhenMovingBackward && localVelocity.z < -0.5f;
            bool disabledBraking = disableTcWhileBraking && brakeInput > tcBrakeInputDisableThreshold;

            debugTcDisabledLowSpeed = disabledLowSpeed;
            debugTcDisabledAngle = disabledAngle;
            debugTcDisabledBackward = disabledBackward;
            debugTcDisabledBraking = disabledBraking;

            bool tcSystemEnabled = enableTractionControl && (!tractionControlUsesEscEnabled || escEnabled);
            bool allowed = tcSystemEnabled
                && interventionLevel > 0.001f
                && throttleInput >= tcThrottleThreshold
                && !disabledLowSpeed
                && !disabledAngle
                && !disabledBackward
                && !disabledBraking;

            debugTcAllowed = allowed;

            float level = Mathf.Clamp01(interventionLevel);
            float slipThreshold = Mathf.Lerp(tcSlipThresholdAtLowIntervention, tcSlipThresholdAtFullIntervention, level);
            debugTcSlipThreshold = slipThreshold;

            bool slipDetected = false;
            float severity = 0f;

            if (allowed)
            {
                float referenceSpeedKmh = Mathf.Max(Mathf.Abs(localVelocity.z) * 3.6f, minTcSpeedKmh);
                slipDetected = DetectDrivenWheelSpin(referenceSpeedKmh, slipThreshold);

                if (debugTcWorstSlip > slipThreshold)
                {
                    float range = Mathf.Max(maxTcSlipRatioForFullSeverity - slipThreshold, 0.05f);
                    severity = Mathf.Clamp01((debugTcWorstSlip - slipThreshold) / range);
                    severity *= level;
                }

                if (slipDetected && severity < 0.05f * level)
                {
                    severity = 0.05f * level;
                }
            }
            else
            {
                debugTcWorstSlip = 0f;
                debugTcWorstWheelIndex = -1;
                debugTcWorstWheelSurfaceSpeedKmh = 0f;
                tcExtremeSlipHoldTimer = 0f;
                tcSeverityHoldTimer = 0f;
                tcHeldSeverity = 0f;
            }

            if (allowed)
            {
                if (enableTcExtremeSlipHold && debugTcWorstSlip >= tcExtremeSlipRatio)
                {
                    tcExtremeSlipHoldTimer = tcExtremeSlipHoldTime;
                    tcHeldSeverity = Mathf.Max(tcHeldSeverity, tcExtremeSlipMinimumSeverity * level);
                }
                else if (tcExtremeSlipHoldTimer > 0f)
                {
                    tcExtremeSlipHoldTimer -= Time.deltaTime;
                }

                if (holdTcSeverityAfterSlipLost)
                {
                    if (slipDetected)
                    {
                        tcSeverityHoldTimer = tcSeverityHoldTime;
                        float minimumHeld = tcHeldSeverityMinimum * level;
                        tcHeldSeverity = Mathf.Max(tcHeldSeverity, Mathf.Max(severity, minimumHeld));
                    }
                    else if (tcSeverityHoldTimer > 0f)
                    {
                        tcSeverityHoldTimer -= Time.deltaTime;
                    }
                }
                else
                {
                    tcSeverityHoldTimer = 0f;
                }
            }

            bool extremeHoldActive = allowed && enableTcExtremeSlipHold && tcExtremeSlipHoldTimer > 0f;
            bool severityHoldActive = allowed && holdTcSeverityAfterSlipLost && tcSeverityHoldTimer > 0f;

            if (extremeHoldActive)
            {
                slipDetected = true;
                severity = Mathf.Max(severity, tcExtremeSlipMinimumSeverity * level);
            }

            if (severityHoldActive)
            {
                slipDetected = true;
                severity = Mathf.Max(severity, tcHeldSeverity);
            }

            if (!extremeHoldActive && !severityHoldActive)
            {
                tcHeldSeverity = Mathf.MoveTowards(tcHeldSeverity, 0f, Time.deltaTime * 4f);
            }

            debugTcSlipDetected = slipDetected;
            debugTcExtremeSlipHoldActive = extremeHoldActive;
            debugTcSeverityHoldActive = severityHoldActive;
            debugTcHeldSeverity = tcHeldSeverity;

            if (slipDetected)
            {
                tcHoldTimer = tcHoldAfterSlipLost;
            }
            else if (tcHoldTimer > 0f)
            {
                tcHoldTimer -= Time.deltaTime;
            }

            tcActive = allowed && (slipDetected || tcHoldTimer > 0f);
            debugTcActive = tcActive;
            debugTcSeverity = tcActive ? Mathf.Max(severity, 0.05f * level) : 0f;

            if (tcActive)
            {
                ApplyTractionControlIntervention(debugTcSeverity);
                if (!escActive)
                {
                    if (debugTcExtremeSlipHoldActive) debugStatus = "TC active: extreme slip hold";
                    else if (debugTcSeverityHoldActive) debugStatus = "TC active: held severity";
                    else debugStatus = "TC active: engine power cut";
                }
                else
                {
                    debugStatus = "ESC + TC active";
                }
            }
            else
            {
                RestoreTcEnginePower();
                debugTcBrakePressure = 0f;
                if (wasTcActiveLastFrame)
                {
                    RestoreTcWheelBrakes();
                }
            }

            wasTcActiveLastFrame = tcActive;
        }

        private void ApplyTractionControlIntervention(float severity)
        {
            if (tcCutEnginePower)
            {
                float targetMultiplier = Mathf.Lerp(tcEngineMultiplierAtLowSeverity, tcEngineMultiplierAtHighSeverity, Mathf.Clamp01(severity));

                // ---- 脉冲调制 vs 平滑逼近 ----
                if (tcUsePulseModulation)
                {
                    tcPulseTimer += Time.deltaTime;
                    if (tcPulseTimer >= tcPulsePeriod)
                        tcPulseTimer -= tcPulsePeriod;

                    // 钳制高低电平范围
                    float minPulse = Mathf.Clamp(tcPulseMinMultiplier, 0f, tcPulseMaxMultiplier);
                    float maxPulse = Mathf.Clamp(tcPulseMaxMultiplier, minPulse, 1f);

                    // 计算使平均输出等于 targetMultiplier 所需的占空比
                    float dutyCycle = Mathf.InverseLerp(minPulse, maxPulse, targetMultiplier);
                    dutyCycle = Mathf.Clamp01(dutyCycle);

                    // 按占空比输出高/低电平
                    currentTcEngineMultiplier = (tcPulseTimer < tcPulsePeriod * dutyCycle) ? maxPulse : minPulse;
                }
                else
                {
                    currentTcEngineMultiplier = Mathf.MoveTowards(currentTcEngineMultiplier, targetMultiplier, tcEngineCutResponse * Time.deltaTime);
                }
            }
            else
            {
                // 如果 TC 不切发动机，则恢复至 1.0
                if (tcUsePulseModulation)
                {
                    currentTcEngineMultiplier = 1f;
                }
                else
                {
                    currentTcEngineMultiplier = Mathf.MoveTowards(currentTcEngineMultiplier, 1f, tcEngineRestoreResponse * Time.deltaTime);
                }
            }

            debugTcEngineMultiplier = currentTcEngineMultiplier;
            ApplyCombinedEngineLimit();

            if (tcBrakeSpinningDrivenWheels)
            {
                float pressure = Mathf.Lerp(tcBrakePressureAtLowSeverity, tcBrakePressureAtHighSeverity, Mathf.Clamp01(severity));
                debugTcBrakePressure = pressure;
                ApplyBrakeToWheels(drivenBrakeWheels, pressure);
            }
            else
            {
                debugTcBrakePressure = 0f;
            }
        }


        private void RestoreTcEnginePower()
        {
            // 平滑恢复至 1.0（无论是否脉冲模式，退出时都应平滑恢复）
            if (tcUsePulseModulation)
            {
                currentTcEngineMultiplier = Mathf.MoveTowards(currentTcEngineMultiplier, 1f, tcEngineRestoreResponse * Time.deltaTime);
            }
            else
            {
                currentTcEngineMultiplier = Mathf.MoveTowards(currentTcEngineMultiplier, 1f, tcEngineRestoreResponse * Time.deltaTime);
            }
            debugTcEngineMultiplier = currentTcEngineMultiplier;
            ApplyCombinedEngineLimit();
        }


        private void RestoreTcWheelBrakes()
        {
            if (!tcBrakeSpinningDrivenWheels) return;
            ApplyBrakeToWheels(drivenBrakeWheels, 0f);
        }

        private bool DetectDrivenWheelSpin(float vehicleReferenceSpeedKmh, float slipThreshold)
        {
            if (drivenWheelCount <= 0)
            {
                debugStatus = "No driven wheel speed inputs";
                return false;
            }

            bool anySlip = false;
            debugTcWorstSlip = 0f;
            debugTcWorstWheelIndex = -1;
            debugTcWorstWheelSurfaceSpeedKmh = 0f;

            for (int i = 0; i < drivenWheelCount; i++)
            {
                if (!IsDrivenWheelGrounded(i)) continue;

                float wheelSurfaceSpeedKmh = GetDrivenWheelSurfaceSpeedKmh(i);
                float slip = 0f;

                if (vehicleReferenceSpeedKmh > 0.01f)
                {
                    slip = Mathf.Max(0f, (wheelSurfaceSpeedKmh / vehicleReferenceSpeedKmh) - 1f);
                }

                if (slip > debugTcWorstSlip)
                {
                    debugTcWorstSlip = slip;
                    debugTcWorstWheelIndex = i;
                    debugTcWorstWheelSurfaceSpeedKmh = wheelSurfaceSpeedKmh;
                }

                if (slip >= slipThreshold)
                {
                    anySlip = true;
                }
            }

            return anySlip;
        }

        private bool IsDrivenWheelGrounded(int index)
        {
            UdonSharpBehaviour source = GetDrivenWheelSource(index);
            if (source == null) return assumeDrivenGroundedIfNoSource;
            if (!useDrivenGroundedVariable) return true;
            return (bool)source.GetProgramVariable(drivenGroundedVariableName);
        }

        private float GetDrivenWheelSurfaceSpeedKmh(int index)
        {
            if (useDrivenWheelProgramVariable)
            {
                UdonSharpBehaviour source = GetDrivenWheelSource(index);
                if (source != null)
                {
                    float raw = (float)source.GetProgramVariable(drivenWheelSpeedVariableName);
                    return ConvertDrivenWheelSpeedToKmh(raw, GetDrivenWheelRadius(index));
                }
            }

            if (useDrivenVisualWheelRotation)
            {
                Transform wheel = GetDrivenWheelVisual(index);
                if (wheel != null)
                {
                    float angle = GetDrivenWheelAxisAngle(wheel);
                    if (!previousDrivenWheelAngleValid[index])
                    {
                        previousDrivenWheelAngle[index] = angle;
                        previousDrivenWheelAngleValid[index] = true;
                        return Mathf.Abs(vehicleRoot.InverseTransformDirection(vehicleRigidbody.velocity).z) * 3.6f;
                    }

                    float delta = Mathf.Abs(Mathf.DeltaAngle(previousDrivenWheelAngle[index], angle));
                    previousDrivenWheelAngle[index] = angle;

                    if (Time.deltaTime <= 0.0001f) return 0f;
                    float rps = (delta / 360f) / Time.deltaTime;
                    float mps = rps * 2f * Mathf.PI * GetDrivenWheelRadius(index);
                    return mps * 3.6f;
                }
            }

            return Mathf.Abs(vehicleRoot.InverseTransformDirection(vehicleRigidbody.velocity).z) * 3.6f;
        }

        private float ConvertDrivenWheelSpeedToKmh(float raw, float radius)
        {
            if (drivenWheelSpeedVariableUnits == 1)
            {
                return Mathf.Abs(raw / 60f) * 2f * Mathf.PI * radius * 3.6f;
            }
            if (drivenWheelSpeedVariableUnits == 2)
            {
                return Mathf.Abs(raw) * radius * 3.6f;
            }
            if (drivenWheelSpeedVariableUnits == 3)
            {
                return Mathf.Abs(raw) * 3.6f;
            }
            if (drivenWheelSpeedVariableUnits == 4)
            {
                return Mathf.Abs(raw);
            }

            return Mathf.Abs(raw) * 2f * Mathf.PI * radius * 3.6f;
        }

        private float GetDrivenWheelAxisAngle(Transform wheel)
        {
            Vector3 euler = wheel.localEulerAngles;
            if (drivenVisualRotationAxis == 1) return euler.y;
            if (drivenVisualRotationAxis == 2) return euler.z;
            return euler.x;
        }

        private Transform GetDrivenWheelVisual(int index)
        {
            if (drivenWheelVisuals == null) return null;
            if (index < 0 || index >= drivenWheelVisuals.Length) return null;
            return drivenWheelVisuals[index];
        }

        private UdonSharpBehaviour GetDrivenWheelSource(int index)
        {
            if (drivenWheelSources == null) return null;
            if (index < 0 || index >= drivenWheelSources.Length) return null;
            return drivenWheelSources[index];
        }

        private float GetDrivenWheelRadius(int index)
        {
            if (drivenWheelRadii != null && index >= 0 && index < drivenWheelRadii.Length && drivenWheelRadii[index] > 0.01f)
            {
                return drivenWheelRadii[index];
            }
            return defaultDrivenWheelRadius;
        }

        private void ApplyTireAndEngineIntervention(int correctionSide, float severity)
        {
            if (applyDifferentialWheelBraking)
            {
                bool skipWheelBrake = avoidEscWheelBrakeWhileAbsActive && debugAbsCurrentlyActive;
                if (!skipWheelBrake)
                {
                    float pressure = Mathf.Lerp(escBrakePressureAtLowSeverity, escBrakePressureAtHighSeverity, Mathf.Clamp01(severity));
                    debugEscBrakePressure = pressure;
                    ApplyDifferentialBrake(correctionSide, pressure);
                }
                else
                {
                    debugEscBrakePressure = 0f;
                }
            }
            else
            {
                debugEscBrakePressure = 0f;
            }

            if (applyEnginePowerCut)
            {
                ApplyEngineCut(severity);
            }
            else
            {
                RestoreEnginePower();
            }
        }

        private void ApplyDifferentialBrake(int correctionSide, float escBrakePressure)
        {
            float brakeInput = GetBrakeInput();
            float frontBase = preserveDriverBrakeInput ? brakeInput * GetFrontBrakeMultiplier() : 0f;
            float rearBase = preserveDriverBrakeInput ? brakeInput * GetRearBrakeMultiplier() : 0f;

            bool brakeLeft = correctionSide < 0;
            bool brakeRight = correctionSide > 0;

            if (useFrontWheelsForEscBrake)
            {
                ApplyBrakeToWheels(frontLeftBrakeWheels, brakeLeft ? Mathf.Max(frontBase, escBrakePressure) : frontBase);
                ApplyBrakeToWheels(frontRightBrakeWheels, brakeRight ? Mathf.Max(frontBase, escBrakePressure) : frontBase);
            }

            if (useRearWheelsForEscBrake)
            {
                ApplyBrakeToWheels(rearLeftBrakeWheels, brakeLeft ? Mathf.Max(rearBase, escBrakePressure) : rearBase);
                ApplyBrakeToWheels(rearRightBrakeWheels, brakeRight ? Mathf.Max(rearBase, escBrakePressure) : rearBase);
            }
        }

        private void RestoreEscWheelBrakesToDriverBrake()
        {
            float brakeInput = GetBrakeInput();
            float frontBase = preserveDriverBrakeInput ? brakeInput * GetFrontBrakeMultiplier() : 0f;
            float rearBase = preserveDriverBrakeInput ? brakeInput * GetRearBrakeMultiplier() : 0f;

            ApplyBrakeToWheels(frontLeftBrakeWheels, frontBase);
            ApplyBrakeToWheels(frontRightBrakeWheels, frontBase);
            ApplyBrakeToWheels(rearLeftBrakeWheels, rearBase);
            ApplyBrakeToWheels(rearRightBrakeWheels, rearBase);
        }

        private void ApplyBrakeToWheels(UdonSharpBehaviour[] wheels, float value)
        {
            if (wheels == null) return;
            value = Mathf.Clamp01(value);

            for (int i = 0; i < wheels.Length; i++)
            {
                if (wheels[i] != null)
                {
                    wheels[i].SetProgramVariable(brakeVariableName, value);
                }
            }
        }

        private void ApplyEngineCut(float severity)
        {
            float throttleInput = GetThrottleInput();
            if (throttleInput < engineCutMinThrottleInput)
            {
                RestoreEscEnginePower();
                return;
            }

            float targetMultiplier = Mathf.Lerp(engineMultiplierAtLowSeverity, engineMultiplierAtHighSeverity, Mathf.Clamp01(severity));
            currentEscEngineMultiplier = Mathf.MoveTowards(currentEscEngineMultiplier, targetMultiplier, engineCutResponse * Time.deltaTime);
            ApplyCombinedEngineLimit();
        }

        private void RestoreEnginePower()
        {
            RestoreEscEnginePower();
            RestoreTcEnginePower();
        }

        private void RestoreEscEnginePower()
        {
            currentEscEngineMultiplier = Mathf.MoveTowards(currentEscEngineMultiplier, 1f, engineRestoreResponse * Time.deltaTime);
            ApplyCombinedEngineLimit();
        }

        private void ApplyCombinedEngineLimit()
        {
            // 原来的 ESC/TC 最小值保留
            float escTcMultiplier = Mathf.Min(Mathf.Clamp01(currentEscEngineMultiplier), Mathf.Clamp01(currentTcEngineMultiplier));
            // 叠加换挡抑制
            currentEngineMultiplier = Mathf.Min(escTcMultiplier, Mathf.Clamp01(currentShiftSuppressMultiplier));

            debugEngineMultiplier = currentEngineMultiplier;
            debugCombinedEngineMultiplier = currentEngineMultiplier;

            if (cutEngineByDriveSpeed && sgvControl != null && originalDriveSpeedCaptured)
            {
                sgvControl.DriveSpeed = originalDriveSpeed * currentEngineMultiplier;
                if (currentEngineMultiplier >= 0.999f)
                {
                    sgvControl.DriveSpeed = originalDriveSpeed;
                }
            }
        }


        private void ApplyRigidbodyFallbackCorrection(float signedSlipAngle, Vector3 localVelocity, float severity)
        {
            Vector3 localAngularVelocity = vehicleRoot.InverseTransformDirection(vehicleRigidbody.angularVelocity);
            debugYawRate = localAngularVelocity.y;

            float normalizedSlip = 0f;
            if (maxRecoverableSlipAngleDeg > 0.01f)
            {
                normalizedSlip = Mathf.Clamp(signedSlipAngle / maxRecoverableSlipAngleDeg, -1f, 1f);
            }

            float level = Mathf.Clamp01(severity);
            float correction = normalizedSlip * yawCorrectionStrength * level;
            float damping = -localAngularVelocity.y * yawDampingStrength * level;
            float yawCommand = Mathf.Clamp(correction + damping, -maxYawAcceleration, maxYawAcceleration);

            debugYawAccelerationCommand = yawCommand;
            vehicleRigidbody.AddTorque(vehicleRoot.up * yawCommand, ForceMode.Acceleration);

            float lateralAccel = Mathf.Clamp(-localVelocity.x * lateralDampingStrength * level, -maxLateralAcceleration, maxLateralAcceleration);
            debugLateralAccelerationCommand = lateralAccel;
            vehicleRigidbody.AddForce(vehicleRoot.right * lateralAccel, ForceMode.Acceleration);
        }

        private float GetSignedForwardVelocityAngle(Vector3 flatVelocity)
        {
            if (flatVelocity.sqrMagnitude < 0.01f) return 0f;

            Vector3 forward = vehicleRoot.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f) return 0f;

            return Vector3.SignedAngle(forward.normalized, flatVelocity.normalized, Vector3.up);
        }

        private float GetSteeringInput()
        {
            if (sgvControl == null) return 0f;
            return Mathf.Clamp(sgvControl.YawInput, -1f, 1f);
        }

        private float GetThrottleInput()
        {
            if (sgvControl == null) return 0f;
            return Mathf.Clamp01(Mathf.Abs(sgvControl.ThrottleInput));
        }

        private float GetBrakeInput()
        {
            if (carBrakeFunction != null) return Mathf.Clamp01(carBrakeFunction._BrakeInput);
            return 0f;
        }

        private float GetFrontBrakeMultiplier()
        {
            if (readBrakeStrengthMultipliersFromCarBrake && carBrakeFunction != null) return carBrakeFunction.Brake_FrontStrengthMulti;
            return frontBrakeStrengthMultiplier;
        }

        private float GetRearBrakeMultiplier()
        {
            if (readBrakeStrengthMultipliersFromCarBrake && carBrakeFunction != null) return carBrakeFunction.Brake_BackStrengthMulti;
            return rearBrakeStrengthMultiplier;
        }

        private void CaptureOriginalDriveSpeed()
        {
            if (originalDriveSpeedCaptured || sgvControl == null) return;
            originalDriveSpeed = sgvControl.DriveSpeed;
            originalDriveSpeedCaptured = true;
        }

        private void SendLightEnabledStateIfOwner()
        {
            if (lightController == null || !sendEscToLightController) return;
            if (Networking.LocalPlayer == null) return;
            if (!Networking.IsOwner(gameObject)) return;

            if (escEnabled != lastSentEscEnabled || forceSendEscEnabledState)
            {
                lightController.SetExternalESCEnabled(escEnabled);
                lastSentEscEnabled = escEnabled;
                forceSendEscEnabledState = false;
            }
        }

        private void ForceSendLightEnabledState()
        {
            if (lightController == null || !sendEscToLightController) return;

            lightController.SetExternalESCEnabled(escEnabled);
            lastSentEscEnabled = escEnabled;
            forceSendEscEnabledState = false;
        }

        private void SendLightStateIfNeeded(float speedKmh, bool stabilityActiveForIndicator)
        {
            if (lightController == null || !sendEscToLightController) return;

            SendLightEnabledStateIfOwner();

            if (stabilityActiveForIndicator != lastSentEscActive)
            {
                lightController.SetExternalESCActive(stabilityActiveForIndicator, speedKmh);
                lastSentEscActive = stabilityActiveForIndicator;
            }
        }

        private void SetESCInactive(string status)
        {
            escActive = false;
            debugEscActive = false;
            debugEscAllowed = false;
            debugSlipDetected = false;
            debugEscBrakePressure = 0f;
            debugCorrectionSide = 0;
            tcActive = false;
            debugTcActive = false;
            debugTcAllowed = false;
            debugTcSlipDetected = false;
            debugTcSeverity = 0f;
            debugTcBrakePressure = 0f;
            debugTcStrongForIndicator = false;
            debugStabilityIndicatorActive = false;
            tcIndicatorHoldTimer = 0f;
            debugStatus = status;

            RestoreEnginePower();

            if (wasEscActiveLastFrame)
            {
                RestoreEscWheelBrakesToDriverBrake();
                wasEscActiveLastFrame = false;
            }

            SendLightEnabledStateIfOwner();

            if (lastSentEscActive && lightController != null && sendEscToLightController)
            {
                lightController.SetExternalESCActive(false, debugSpeedKmh);
                lastSentEscActive = false;
            }
        }

        public void SetInterventionLevel(float value)
        {
            TakeOwnership();
            interventionLevel = Mathf.Clamp01(value);
            escEnabled = interventionLevel > 0.001f;
            presetIndex = GetClosestPresetIndex(interventionLevel, escEnabled);
            PushSyncedSettings();
        }

        public void IncreaseIntervention()
        {
            SetInterventionLevel(interventionLevel + interventionStep);
        }

        public void DecreaseIntervention()
        {
            SetInterventionLevel(interventionLevel - interventionStep);
        }

        public void CycleInterventionPreset()
        {
            int next = presetIndex + 1;
            if (next > 3) next = 0;
            SetPreset(next);
        }

        public void SetPreset(int index)
        {
            TakeOwnership();
            presetIndex = Mathf.Clamp(index, 0, 3);

            if (presetIndex == 0)
            {
                escEnabled = false;
                interventionLevel = 0f;
            }
            else if (presetIndex == 1)
            {
                escEnabled = true;
                interventionLevel = Mathf.Clamp01(weakPresetLevel);
            }
            else if (presetIndex == 2)
            {
                escEnabled = true;
                interventionLevel = Mathf.Clamp01(mediumPresetLevel);
            }
            else
            {
                escEnabled = true;
                interventionLevel = Mathf.Clamp01(strongPresetLevel);
            }

            PushSyncedSettings();
        }

        public void SetWeakPreset() { SetPreset(1); }
        public void SetMediumPreset() { SetPreset(2); }
        public void SetStrongPreset() { SetPreset(3); }

        public void ToggleESC()
        {
            TakeOwnership();
            if (escEnabled) SetPreset(0);
            else SetPreset(2);
        }

        public void SetESCOn()
        {
            if (interventionLevel <= 0.001f) SetPreset(2);
            else
            {
                escEnabled = true;
                presetIndex = GetClosestPresetIndex(interventionLevel, true);
                PushSyncedSettings();
            }
        }

        public void SetESCOff()
        {
            SetPreset(0);
            SetESCInactive("ESC disabled");
        }

        private int GetClosestPresetIndex(float level, bool enabled)
        {
            if (!enabled || level <= 0.001f) return 0;

            float weakDiff = Mathf.Abs(level - weakPresetLevel);
            float medDiff = Mathf.Abs(level - mediumPresetLevel);
            float strongDiff = Mathf.Abs(level - strongPresetLevel);

            if (weakDiff <= medDiff && weakDiff <= strongDiff) return 1;
            if (medDiff <= weakDiff && medDiff <= strongDiff) return 2;
            return 3;
        }

        private void PushSyncedSettings()
        {
            debugInterventionLevel = interventionLevel;
            debugPresetIndex = presetIndex;

            if (syncInterventionLevel)
            {
                syncedInterventionLevel = interventionLevel;
                syncedEscEnabled = escEnabled;
                syncedPresetIndex = presetIndex;
                RequestSerialization();
            }

            // Mode changes are local player actions from keyboard, in-world buttons, or SACC DFUNC.
            // Send the ESC enabled/off state immediately instead of waiting for an ownership check,
            // because SetOwner can take a frame to become visible and would otherwise leave ESC OFF dark.
            ForceSendLightEnabledState();
        }

        public void SFEXT_O_PilotEnter()
        {
            localPilot = true;
            debugLocalPilot = true;
            if (takeOwnershipOnPilotEnter) TakeOwnership();
            SendLightEnabledStateIfOwner();
        }

        public void SFEXT_O_PilotExit()
        {
            localPilot = false;
            debugLocalPilot = false;
            SetESCInactive("Pilot exit");
        }

        public void SFEXT_O_TakeOwnership()
        {
            isOwner = true;
            debugIsOwner = true;
        }

        public void SFEXT_O_LoseOwnership()
        {
            isOwner = false;
            debugIsOwner = false;
        }

        private void TakeOwnership()
        {
            if (Networking.LocalPlayer == null) return;
            if (!Networking.IsOwner(gameObject))
            {
                Networking.SetOwner(Networking.LocalPlayer, gameObject);
            }
            isOwner = true;
            debugIsOwner = true;
        }
    }
}
