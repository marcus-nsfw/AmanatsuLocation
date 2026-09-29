using BepInEx.Configuration;
using UnityEngine;

namespace AmanatsuVR.Config
{
    public static class PluginConfig
    {
        // Tracking & Viewport
        public static ConfigEntry<bool> ReflectHMDRotationX { get; private set; }
        public static ConfigEntry<bool> ReflectHMDRotationY { get; private set; }
        public static ConfigEntry<bool> ReflectHMDRotationZ { get; private set; }
        public static ConfigEntry<float> DoubleClickIntervalToUpdateViewport { get; private set; }
        public static ConfigEntry<KeyCode> RecenterKey { get; private set; }

        // First Person & CameraController Translation
        public static ConfigEntry<bool> SyncHeadRotationToCameraController { get; private set; }
        public static ConfigEntry<bool> EnableControllerSmoothTurn { get; private set; }
        public static ConfigEntry<float> SmoothTurnSpeed { get; private set; }
        public static ConfigEntry<float> TrackerPositionScale { get; private set; }
        public static ConfigEntry<float> WorldSize { get; private set; }

        // Stereo
        public static ConfigEntry<bool> UseSinglePassInstanced { get; private set; }
        public static ConfigEntry<bool> DisableHijackedCamera { get; private set; }
        public static ConfigEntry<bool> HideUIPanel { get; private set; }
        public static ConfigEntry<bool> ManualEyeRender { get; private set; }
        public static ConfigEntry<bool> FixStereoCulling { get; private set; }

        // UI Screen
        public static ConfigEntry<float> UIScreenDistance { get; private set; }
        public static ConfigEntry<float> UIScreenScale { get; private set; }
        public static ConfigEntry<float> LaserStabilization { get; private set; }

        public static ConfigEntry<bool> CharCreationPreview { get; private set; }
        public static ConfigEntry<bool> RestoreURPFeatures { get; private set; }

        // Water Gun Mode 6DoF
        public static ConfigEntry<bool> EnableWaterGunVR { get; private set; }
        public static ConfigEntry<string> WaterGunHand { get; private set; }
        public static ConfigEntry<bool> WaterGunHaptics { get; private set; }
        public static ConfigEntry<float> WaterGunForwardOffset { get; private set; }
        public static ConfigEntry<float> WaterGunUpOffset { get; private set; }
        public static ConfigEntry<float> WaterGunPitchOffset { get; private set; }

        // HScene
        public static ConfigEntry<float> MoveSpeed { get; private set; }
        public static ConfigEntry<float> TurnSpeed { get; private set; }
        public static ConfigEntry<float> HEyeOffsetUp { get; private set; }
        public static ConfigEntry<float> HEyeOffsetForward { get; private set; }
        public static ConfigEntry<float> HLookDownForward { get; private set; }
        public static ConfigEntry<float> HAimCameraForward { get; private set; }
        public static ConfigEntry<float> HAimManBackOff { get; private set; }
        public static ConfigEntry<bool> HFollowHeadTilt { get; private set; }
        public static ConfigEntry<float> HHeadSmoothing { get; private set; }
        public static ConfigEntry<float> HHoldTime { get; private set; }
        public static ConfigEntry<float> HAutoTimeMin { get; private set; }
        public static ConfigEntry<float> HAutoTimeMax { get; private set; }
        public static ConfigEntry<float> HAutoShotTime { get; private set; }

        // Optimizations & Controls
        public static ConfigEntry<bool> EnableMouseCameraDragInVR { get; private set; }
        public static ConfigEntry<bool> DisableLightShadows { get; private set; }
        public static ConfigEntry<bool> DisableParticleSystems { get; private set; }

        public static void Setup(ConfigFile config)
        {
            ReflectHMDRotationX = config.Bind("Viewport", "ReflectHMDRotationX", true, "Tracks vertical head rotation (pitch).");
            ReflectHMDRotationY = config.Bind("Viewport", "ReflectHMDRotationY", true, "Tracks horizontal head rotation (yaw).");
            ReflectHMDRotationZ = config.Bind("Viewport", "ReflectHMDRotationZ", true, "Tracks sideways head tilt (roll).");
            DoubleClickIntervalToUpdateViewport = config.Bind("Viewport", "DoubleClickIntervalToUpdateViewport", 0.35f, "Seconds allowed between the two right mouse clicks of a double click that recenters the view (0 disables).");
            RecenterKey = config.Bind("Viewport", "RecenterKey", KeyCode.R, "Keyboard key that recenters the headset/trackers.");

            SyncHeadRotationToCameraController = config.Bind("FirstPersonTracker", "SyncHeadRotationToCameraController", false, "If enabled, syncs head rotation to the game's CameraController without applying the HMD rotation twice.");
            EnableControllerSmoothTurn = config.Bind("FirstPersonTracker", "EnableControllerSmoothTurn", true, "Enables smooth turn with the keyboard arrow keys.");
            SmoothTurnSpeed = config.Bind("FirstPersonTracker", "SmoothTurnSpeed", 60.0f, "Smooth turn speed in degrees per second.");
            TrackerPositionScale = config.Bind("FirstPersonTracker", "TrackerPositionScale", 1.0f, "Scale of physical 6DoF movement in the virtual space.");
            // Chave nova (era WorldScale): o valor antigo era relativo a unidade do jogo, nao ao
            // tamanho real, e um 0.3 guardado com o sentido antigo sairia minusculo agora.
            WorldSize = config.Bind("FirstPersonTracker", "WorldSize", 1.8f,
                "Apparent size of the world. 1.0 = real life size (the game models everything at x10 scale, "
                + "Human.MODEL_SCALE, and that is already compensated). Above 1 makes everything bigger, below 1 "
                + "smaller. Adjust in steps of 0.05.");

            EnableMouseCameraDragInVR = config.Bind("FirstPersonTracker", "EnableMouseCameraDragInVR", false, "Lets mouse dragging rotate the camera in VR (off by default to avoid motion sickness when clicking menus).");

            UseSinglePassInstanced = config.Bind("Stereo", "UseSinglePassInstanced", true,
                "Single Pass Instanced. This game's scenery is drawn by Entities Graphics (BatchRendererGroup), "
                + "which does NOT support multipass: in multipass only the left eye gets the map. "
                + "If objects show up pink/black in single pass, set back to false (multipass).");

            DisableHijackedCamera = config.Bind("Stereo", "DisableHijackedCamera", true,
                "Disables the game's Camera component instead of only clearing its cullingMask. "
                + "If the map also disappears from the left eye, it was being drawn by the game's mono camera.");

            ManualEyeRender = config.Bind("Stereo", "ManualEyeRender", false,
                "Renders each eye as a mono camera and copies it to that eye's XR target, instead "
                + "of relying on the URP stereo pass (which loses opaque geometry in the right "
                + "eye). Costs one extra render per eye. false restores the previous behavior.");

            FixStereoCulling = config.Bind("Stereo", "FixStereoCulling", true,
                "The OpenVR plugin returns the second pass culling as a 2 m box around "
                + "the head instead of a frustum, so the right eye only gets sky and UI. "
                + "Reuses the valid culling from the first pass. "
                + "false restores the broken behavior.");

            HideUIPanel = config.Bind("Stereo", "HideUIPanel", false,
                "Diagnostic: hides the UI panel (the 'pink square'). If the right eye shows the world "
                + "with it hidden, the panel is the occluder. Hides the whole UI.");

            UIScreenDistance = config.Bind("UIScreen", "UIScreenDistance", 1.2f, "Distance in meters of the floating virtual UI panel.");
            UIScreenScale = config.Bind("UIScreen", "UIScreenScale", 1.0f, "Size/scale of the floating virtual UI panel.");
            LaserStabilization = config.Bind("UIScreen", "LaserStabilization", 1.0f,
                "Laser weight in menus: slow movement (hand tremor, fine aiming) is damped, fast movement goes straight through. " +
                "0 = off; 2 = twice the weight.");

            CharCreationPreview = config.Bind("CharCreation", "CharCreationPreview", true,
                "In character creation, draws the model inside the 2D UI itself (in front of the background, "
                + "behind the buttons) instead of leaving it in the 3D world behind the panel. Hiding the panel "
                + "(grip) brings the model back in stereo.");

            RestoreURPFeatures = config.Bind("Stereo", "RestoreURPFeatures", false,
                "Diagnostic: restores every URP render feature we disable in VR "
                + "(Beautify, outline, SSAO, fog, fullscreen pass, crossfade). Meant to find out whether "
                + "one of them draws the character's skin - with them back the right eye will "
                + "probably break again, so it is for measuring, not for playing.");

            EnableWaterGunVR = config.Bind("WaterGun", "EnableWaterGunVR", true, "Attaches the water gun to the VR controller in 6DoF, so you aim freely and fire with the trigger.");
            WaterGunHand = config.Bind("WaterGun", "WaterGunHand", "Right", "VR controller hand that holds the gun ('Right' or 'Left').");
            WaterGunHaptics = config.Bind("WaterGun", "WaterGunHaptics", true, "Vibrates the VR controller while shooting water.");
            WaterGunForwardOffset = config.Bind("WaterGun", "WaterGunForwardOffset", 0.08f, "Forward offset of the gun grip in meters.");
            WaterGunUpOffset = config.Bind("WaterGun", "WaterGunUpOffset", -0.02f, "Vertical offset of the gun grip in meters.");
            WaterGunPitchOffset = config.Bind("WaterGun", "WaterGunPitchOffset", -15.0f, "Vertical tilt of the gun in degrees for natural aiming.");

            MoveSpeed = config.Bind("HScene", "MoveSpeed", 1.0f, "Stick movement speed (H free camera, map and massage): meters per second with the stick fully pushed.");
            TurnSpeed = config.Bind("HScene", "TurnSpeed", 60f, "Turn speed (H free camera, map and massage): degrees per second for grip + stick.");
            HEyeOffsetUp = config.Bind("HScene", "EyeOffsetUp", 0f,
                "First person: fine offset (meters, in head space) added to the point between the eyes measured on the model. Positive = up.");
            HEyeOffsetForward = config.Bind("HScene", "EyeOffsetForward", 0f,
                "First person: fine offset (meters) added to the point between the eyes. Positive = in front of the face.");
            HLookDownForward = config.Bind("HScene", "LookDownForward", 0.08f,
                "First person: how far the camera moves forward (meters) when you look down in the headset, on a curve "
                + "(almost nothing looking straight, the full value looking down). Avoids seeing the neck cut. 0 disables.");
            HAimCameraForward = config.Bind("HScene", "AimCameraForward", 0.15f,
                "Aimed shot in the man's first person: how far the camera goes in front of his eyes (meters), to see the target better.");
            HAimManBackOff = config.Bind("HScene", "AimManBackOff", 0.75f,
                "Aimed shot in the man's first person: how far his whole body backs away from her (meters), to fit the target in view. 0 disables.");
            HFollowHeadTilt = config.Bind("HScene", "FollowHeadTilt", true,
                "First person: the view tilts with the actor's head (lying down, looking down). "
                + "false = horizontal turning only, horizon always level.");
            HHeadSmoothing = config.Bind("HScene", "HeadSmoothing", 6f,
                "First person: how fast the view follows the head, per second. Higher = tighter to the animation's "
                + "sway; lower = steadier (less motion sickness).");
            HHoldTime = config.Bind("HScene", "HoldTime", 1.5f, "Seconds holding the stick forward (first person) to switch actor.");
            HAutoTimeMin = config.Bind("HScene", "AutoTimeMin", 20f, "Automatic mode: minimum act time before finishing, in seconds.");
            HAutoTimeMax = config.Bind("HScene", "AutoTimeMax", 45f, "Automatic mode: maximum act time before finishing, in seconds.");
            HAutoShotTime = config.Bind("HScene", "AutoShotTime", 4f, "Automatic free shot (woman's first person or automatic mode): seconds shooting before ending.");

            DisableLightShadows = config.Bind("Optimization", "DisableLightShadows", false, "Disables dynamic point light shadows if they flicker between the eyes.");
            DisableParticleSystems = config.Bind("Optimization", "DisableParticleSystems", false, "Disables complex particle systems if they cause stereo discomfort.");

            // Rewrites the .cfg so existing files pick up the current descriptions (values are kept).
            config.Save();
        }
    }
}
