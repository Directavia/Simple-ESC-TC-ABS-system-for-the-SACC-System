using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace SaccFlightAndVehicles
{
    [DefaultExecutionOrder(1300)] // run after DFUNC_CarBrake so ABS can override the wheel Brake variables
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class SACCABSController : UdonSharpBehaviour
    {
        [Header("Required References")]
        public DFUNC_CarBrake carBrakeFunction;
        public VehicleLightController lightController;
        public Rigidbody vehicleRigidbody;
        public Transform vehicleRoot;

        [Header("Brake Wheels To Override")]
        [Tooltip("Drag the same front brake SaccWheel behaviours used by DFUNC_CarBrake here.")]
        public UdonSharpBehaviour[] frontBrakeWheels;
        [Tooltip("Drag the same rear brake SaccWheel behaviours used by DFUNC_CarBrake here.")]
        public UdonSharpBehaviour[] rearBrakeWheels;
        public string brakeVariableName = "Brake";
        public bool readBrakeStrengthMultipliersFromCarBrake = true;
        [Range(0f, 2f)] public float frontBrakeStrengthMultiplier = 1f;
        [Range(0f, 2f)] public float rearBrakeStrengthMultiplier = 1f;

        [Header("Wheel Speed Source")]
        [Tooltip("Visual wheel transforms, one per wheel. The script measures rotation delta to estimate wheel speed.")]
        public Transform[] wheelVisuals;
        [Tooltip("Optional SaccWheel/Udon behaviours used for Grounded and/or wheel speed variable reads. Order should match wheelVisuals.")]
        public UdonSharpBehaviour[] wheelSpeedSources;
        public bool useVisualWheelRotation = true;
        [Tooltip("0=X, 1=Y, 2=Z. Most vehicle wheels rotate around local X, but this depends on the model.")]
        public int visualRotationAxis = 0;
        public float defaultWheelRadius = 0.34f;
        [Tooltip("Optional per-wheel radii. If empty or index missing, defaultWheelRadius is used.")]
        public float[] wheelRadii;

        [Header("Optional Program Variable Wheel Speed")]
        [Tooltip("Only enable this if you know the wheel script exposes this variable. Wrong names can cause Udon runtime errors.")]
        public bool useWheelProgramVariable = false;
        public string wheelSpeedVariableName = "WheelRPS";
        [Tooltip("0=RPS, 1=RPM, 2=rad/s, 3=m/s, 4=km/h")]
        public int wheelSpeedVariableUnits = 0;

        [Header("Ground Contact")]
        [Tooltip("SaccWheel normally exposes a Grounded bool. Disable if your wheel source does not have it.")]
        public bool useGroundedVariable = true;
        public string groundedVariableName = "Grounded";
        public bool assumeGroundedIfNoSource = true;

        [Header("ABS Enable / Ownership")]
        public bool absEnabled = true;
        public bool requireLocalPilot = true;
        public bool onlyRunWhenOwner = false;
        public bool sendAbsToLightController = true;
        public bool alsoSendBrakeInputToLightController = true;

        [Header("ABS Activation Conditions")]
        [Range(0f, 1f)] public float brakeInputThreshold = 0.20f;
        [Tooltip("Below this speed ABS is disabled, so wheels are allowed to lock like real low-speed braking.")]
        public float minAbsSpeedKmh = 12f;
        [Tooltip("If vehicle forward direction and actual velocity direction differ more than this angle, ABS is disabled and wheels are allowed to lock. This avoids ABS fighting drift/spin/reverse motion.")]
        public float maxForwardVelocityAngleDeg = 45f;
        [Tooltip("A wheel surface speed below this while the vehicle is still moving is treated as a locked wheel.")]
        public float lockedWheelSpeedKmh = 2f;
        [Tooltip("Slip threshold when interventionLevel=1. Lower means earlier ABS intervention.")]
        [Range(0.01f, 0.95f)] public float slipThresholdAtFullIntervention = 0.18f;
        [Tooltip("Slip threshold when interventionLevel=0. Higher means later/weaker intervention.")]
        [Range(0.01f, 0.95f)] public float slipThresholdAtLowIntervention = 0.45f;
        [Tooltip("Keeps ABS active briefly after slip disappears to prevent rapid on/off chatter.")]
        public float absHoldAfterSlipLost = 0.12f;

        [Header("ABS Intervention Level")]
        [Tooltip("0 = almost no ABS, 1 = early and strong ABS. This can be adjusted by world buttons.")]
        [Range(0f, 1f)] public float interventionLevel = 0.65f;
        public bool syncInterventionLevel = true;
        public float interventionStep = 0.1f;
        [Tooltip("Brake multiplier during release phase at interventionLevel=1.")]
        [Range(0f, 1f)] public float minBrakeMultiplierAtFullIntervention = 0.25f;
        [Tooltip("Brake multiplier during release phase at interventionLevel=0.")]
        [Range(0f, 1f)] public float brakeMultiplierAtLowIntervention = 0.85f;
        [Tooltip("Brake multiplier during reapply phase.")]
        [Range(0f, 1.2f)] public float reapplyBrakeMultiplier = 1.0f;

        [Header("ABS Pulse")]
        public float absCycleTime = 0.10f;
        [Range(0f, 1f)] public float releaseDutyAtLowIntervention = 0.25f;
        [Range(0f, 1f)] public float releaseDutyAtFullIntervention = 0.65f;

        [Header("Debug Read Only")]
        public bool debugLocalPilot;
        public bool debugIsOwner;
        public bool debugAbsAllowed;
        public bool debugAbsActive;
        public bool debugSlipDetected;
        public bool debugDisabledLowSpeed;
        public bool debugDisabledAngle;
        public float debugBrakeInput;
        public float debugSpeedKmh;
        public float debugForwardVelocityAngle;
        public float debugSlipThreshold;
        public float debugWorstSlip;
        public int debugWorstWheelIndex = -1;
        public float debugWorstWheelSurfaceSpeedKmh;
        public float debugCurrentBrakeMultiplier = 1f;
        public float debugInterventionLevel;
        public string debugStatus;

        [UdonSynced] private float syncedInterventionLevel = 0.65f;
        [UdonSynced] private bool syncedAbsEnabled = true;

        private bool localPilot;
        private bool isOwner;
        private bool absActive;
        private bool wasAbsActiveLastFrame;
        private float absHoldTimer;
        private float absCycleTimer;
        private float[] previousWheelAngle;
        private bool[] previousWheelAngleValid;
        private int wheelCount;

        private void Start()
        {
            isOwner = Networking.LocalPlayer != null && Networking.IsOwner(gameObject);
            debugIsOwner = isOwner;

            wheelCount = 0;
            if (wheelVisuals != null && wheelVisuals.Length > wheelCount) wheelCount = wheelVisuals.Length;
            if (wheelSpeedSources != null && wheelSpeedSources.Length > wheelCount) wheelCount = wheelSpeedSources.Length;

            previousWheelAngle = new float[wheelCount];
            previousWheelAngleValid = new bool[wheelCount];

            syncedInterventionLevel = interventionLevel;
            syncedAbsEnabled = absEnabled;
            debugInterventionLevel = interventionLevel;
        }

        public override void OnDeserialization()
        {
            if (syncInterventionLevel)
            {
                interventionLevel = Mathf.Clamp01(syncedInterventionLevel);
                absEnabled = syncedAbsEnabled;
                debugInterventionLevel = interventionLevel;
            }
        }

        private void LateUpdate()
        {
            UpdateABS();
        }

        private void UpdateABS()
        {
            if (vehicleRigidbody == null)
            {
                SetABSInactive("No vehicleRigidbody");
                return;
            }

            if (vehicleRoot == null)
            {
                vehicleRoot = vehicleRigidbody.transform;
            }

            if (onlyRunWhenOwner && !isOwner)
            {
                SetABSInactive("Not owner");
                return;
            }

            if (requireLocalPilot && !localPilot)
            {
                SetABSInactive("No local pilot");
                return;
            }

            float brakeInput = GetBrakeInput();
            float speedKmh = vehicleRigidbody.velocity.magnitude * 3.6f;
            debugBrakeInput = brakeInput;
            debugSpeedKmh = speedKmh;

            bool disabledLowSpeed = speedKmh < minAbsSpeedKmh;
            float angle = GetForwardVelocityAngle();
            bool disabledAngle = angle > maxForwardVelocityAngleDeg;

            debugDisabledLowSpeed = disabledLowSpeed;
            debugDisabledAngle = disabledAngle;
            debugForwardVelocityAngle = angle;

            bool allowed = absEnabled
                && interventionLevel > 0.001f
                && brakeInput > brakeInputThreshold
                && !disabledLowSpeed
                && !disabledAngle;

            debugAbsAllowed = allowed;

            float slipThreshold = Mathf.Lerp(slipThresholdAtLowIntervention, slipThresholdAtFullIntervention, Mathf.Clamp01(interventionLevel));
            debugSlipThreshold = slipThreshold;

            bool slipDetected = false;
            if (allowed)
            {
                slipDetected = DetectWheelSlip(speedKmh, slipThreshold);
            }
            else
            {
                debugWorstSlip = 0f;
                debugWorstWheelIndex = -1;
                debugWorstWheelSurfaceSpeedKmh = 0f;
            }

            debugSlipDetected = slipDetected;

            if (slipDetected)
            {
                absHoldTimer = absHoldAfterSlipLost;
            }
            else if (absHoldTimer > 0f)
            {
                absHoldTimer -= Time.deltaTime;
            }

            absActive = allowed && (slipDetected || absHoldTimer > 0f);
            debugAbsActive = absActive;

            if (absActive)
            {
                float brakeMultiplier = GetAbsBrakeMultiplier();
                debugCurrentBrakeMultiplier = brakeMultiplier;
                ApplyBrakeOverride(brakeInput, brakeMultiplier);
                debugStatus = "ABS active";
            }
            else
            {
                absCycleTimer = 0f;
                debugCurrentBrakeMultiplier = 1f;

                if (wasAbsActiveLastFrame && brakeInput > brakeInputThreshold)
                {
                    ApplyBrakeOverride(brakeInput, 1f);
                }

                if (!allowed)
                {
                    if (disabledLowSpeed) debugStatus = "ABS disabled: low speed";
                    else if (disabledAngle) debugStatus = "ABS disabled: velocity angle";
                    else if (!absEnabled) debugStatus = "ABS disabled";
                    else if (brakeInput <= brakeInputThreshold) debugStatus = "Waiting for brake input";
                    else debugStatus = "ABS not allowed";
                }
                else
                {
                    debugStatus = "No wheel lock";
                }
            }

            wasAbsActiveLastFrame = absActive;

            if (lightController != null && sendAbsToLightController)
            {
                lightController.SetExternalABSActive(absActive, speedKmh);
                if (alsoSendBrakeInputToLightController)
                {
                    lightController.SetBrakeInput(brakeInput, speedKmh);
                }
            }
        }

        private void SetABSInactive(string status)
        {
            absActive = false;
            debugAbsActive = false;
            debugAbsAllowed = false;
            debugSlipDetected = false;
            debugCurrentBrakeMultiplier = 1f;
            debugStatus = status;

            if (lightController != null && sendAbsToLightController)
            {
                lightController.SetExternalABSActive(false, debugSpeedKmh);
            }
        }

        private float GetBrakeInput()
        {
            if (carBrakeFunction != null)
            {
                return Mathf.Clamp01(carBrakeFunction._BrakeInput);
            }
            return 0f;
        }

        private float GetForwardVelocityAngle()
        {
            Vector3 velocity = vehicleRigidbody.velocity;
            velocity.y = 0f;
            if (velocity.sqrMagnitude < 0.01f) return 0f;

            Vector3 forward = vehicleRoot.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f) return 0f;

            return Vector3.Angle(forward.normalized, velocity.normalized);
        }

        private bool DetectWheelSlip(float vehicleSpeedKmh, float slipThreshold)
        {
            if (wheelCount <= 0)
            {
                debugStatus = "No wheel speed inputs";
                return false;
            }

            bool anySlip = false;
            debugWorstSlip = 0f;
            debugWorstWheelIndex = -1;
            debugWorstWheelSurfaceSpeedKmh = 0f;

            for (int i = 0; i < wheelCount; i++)
            {
                if (!IsWheelGrounded(i)) continue;

                float wheelSurfaceSpeedKmh = GetWheelSurfaceSpeedKmh(i);
                float slip = 0f;

                if (vehicleSpeedKmh > 0.01f)
                {
                    slip = 1f - Mathf.Clamp01(wheelSurfaceSpeedKmh / vehicleSpeedKmh);
                }

                if (wheelSurfaceSpeedKmh <= lockedWheelSpeedKmh && vehicleSpeedKmh >= minAbsSpeedKmh)
                {
                    slip = 1f;
                }

                if (slip > debugWorstSlip)
                {
                    debugWorstSlip = slip;
                    debugWorstWheelIndex = i;
                    debugWorstWheelSurfaceSpeedKmh = wheelSurfaceSpeedKmh;
                }

                if (slip >= slipThreshold)
                {
                    anySlip = true;
                }
            }

            return anySlip;
        }

        private bool IsWheelGrounded(int index)
        {
            UdonSharpBehaviour source = GetWheelSource(index);
            if (source == null) return assumeGroundedIfNoSource;
            if (!useGroundedVariable) return true;

            // SaccWheel normally has a public bool called Grounded.
            // Disable useGroundedVariable if your wheel behaviour does not expose it.
            return (bool)source.GetProgramVariable(groundedVariableName);
        }

        private float GetWheelSurfaceSpeedKmh(int index)
        {
            if (useWheelProgramVariable)
            {
                UdonSharpBehaviour source = GetWheelSource(index);
                if (source != null)
                {
                    float raw = (float)source.GetProgramVariable(wheelSpeedVariableName);
                    return ConvertWheelSpeedToKmh(raw, GetWheelRadius(index));
                }
            }

            if (useVisualWheelRotation)
            {
                Transform wheel = GetWheelVisual(index);
                if (wheel != null)
                {
                    float angle = GetWheelAxisAngle(wheel);
                    if (!previousWheelAngleValid[index])
                    {
                        previousWheelAngle[index] = angle;
                        previousWheelAngleValid[index] = true;
                        return vehicleRigidbody.velocity.magnitude * 3.6f;
                    }

                    float delta = Mathf.Abs(Mathf.DeltaAngle(previousWheelAngle[index], angle));
                    previousWheelAngle[index] = angle;

                    if (Time.deltaTime <= 0.0001f) return 0f;
                    float rps = (delta / 360f) / Time.deltaTime;
                    float mps = rps * 2f * Mathf.PI * GetWheelRadius(index);
                    return mps * 3.6f;
                }
            }

            // If no source exists, avoid false ABS activation.
            return vehicleRigidbody.velocity.magnitude * 3.6f;
        }

        private float ConvertWheelSpeedToKmh(float raw, float radius)
        {
            if (wheelSpeedVariableUnits == 1) // RPM
            {
                return (raw / 60f) * 2f * Mathf.PI * radius * 3.6f;
            }
            if (wheelSpeedVariableUnits == 2) // rad/s
            {
                return raw * radius * 3.6f;
            }
            if (wheelSpeedVariableUnits == 3) // m/s
            {
                return Mathf.Abs(raw) * 3.6f;
            }
            if (wheelSpeedVariableUnits == 4) // km/h
            {
                return Mathf.Abs(raw);
            }

            // Default: RPS
            return Mathf.Abs(raw) * 2f * Mathf.PI * radius * 3.6f;
        }

        private float GetWheelAxisAngle(Transform wheel)
        {
            Vector3 euler = wheel.localEulerAngles;
            if (visualRotationAxis == 1) return euler.y;
            if (visualRotationAxis == 2) return euler.z;
            return euler.x;
        }

        private Transform GetWheelVisual(int index)
        {
            if (wheelVisuals == null) return null;
            if (index < 0 || index >= wheelVisuals.Length) return null;
            return wheelVisuals[index];
        }

        private UdonSharpBehaviour GetWheelSource(int index)
        {
            if (wheelSpeedSources == null) return null;
            if (index < 0 || index >= wheelSpeedSources.Length) return null;
            return wheelSpeedSources[index];
        }

        private float GetWheelRadius(int index)
        {
            if (wheelRadii != null && index >= 0 && index < wheelRadii.Length && wheelRadii[index] > 0.01f)
            {
                return wheelRadii[index];
            }
            return defaultWheelRadius;
        }

        private float GetAbsBrakeMultiplier()
        {
            float level = Mathf.Clamp01(interventionLevel);
            float releaseMultiplier = Mathf.Lerp(brakeMultiplierAtLowIntervention, minBrakeMultiplierAtFullIntervention, level);
            float releaseDuty = Mathf.Lerp(releaseDutyAtLowIntervention, releaseDutyAtFullIntervention, level);

            if (absCycleTime <= 0.001f) return releaseMultiplier;

            absCycleTimer += Time.deltaTime;
            while (absCycleTimer >= absCycleTime)
            {
                absCycleTimer -= absCycleTime;
            }

            float phase = absCycleTimer / absCycleTime;
            if (phase < releaseDuty)
            {
                return releaseMultiplier;
            }

            return reapplyBrakeMultiplier;
        }

        private void ApplyBrakeOverride(float brakeInput, float brakeMultiplier)
        {
            float frontMul = frontBrakeStrengthMultiplier;
            float rearMul = rearBrakeStrengthMultiplier;

            if (readBrakeStrengthMultipliersFromCarBrake && carBrakeFunction != null)
            {
                frontMul = carBrakeFunction.Brake_FrontStrengthMulti;
                rearMul = carBrakeFunction.Brake_BackStrengthMulti;
            }

            ApplyBrakeToWheels(frontBrakeWheels, brakeInput * frontMul * brakeMultiplier);
            ApplyBrakeToWheels(rearBrakeWheels, brakeInput * rearMul * brakeMultiplier);
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

        public void SetInterventionLevel(float value)
        {
            TakeOwnership();
            interventionLevel = Mathf.Clamp01(value);
            debugInterventionLevel = interventionLevel;
            if (syncInterventionLevel)
            {
                syncedInterventionLevel = interventionLevel;
                syncedAbsEnabled = absEnabled;
                RequestSerialization();
            }
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
            if (interventionLevel < 0.25f) SetInterventionLevel(0.5f);
            else if (interventionLevel < 0.75f) SetInterventionLevel(1.0f);
            else SetInterventionLevel(0.0f);
        }

        public void ToggleABS()
        {
            TakeOwnership();
            absEnabled = !absEnabled;
            syncedAbsEnabled = absEnabled;
            syncedInterventionLevel = interventionLevel;
            if (syncInterventionLevel) RequestSerialization();
        }

        public void SetABSOn()
        {
            TakeOwnership();
            absEnabled = true;
            syncedAbsEnabled = true;
            syncedInterventionLevel = interventionLevel;
            if (syncInterventionLevel) RequestSerialization();
        }

        public void SetABSOff()
        {
            TakeOwnership();
            absEnabled = false;
            syncedAbsEnabled = false;
            syncedInterventionLevel = interventionLevel;
            if (syncInterventionLevel) RequestSerialization();
        }

        public void SFEXT_O_PilotEnter()
        {
            localPilot = true;
            debugLocalPilot = true;
        }

        public void SFEXT_O_PilotExit()
        {
            localPilot = false;
            debugLocalPilot = false;
            absActive = false;
            wasAbsActiveLastFrame = false;
            if (lightController != null) lightController.SetExternalABSActive(false, debugSpeedKmh);
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
        }
    }
}
