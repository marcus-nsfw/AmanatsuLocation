# Amanatsu Location (甘夏ろけーしょん) mods — source

Source code of four BepInEx 6 (IL2CPP) plugins for **Amanatsu Location** (ILLGAMES, Unity 6):

| Plugin | What it does |
| :--- | :--- |
| **AmanatsuVR** | Full virtual reality (SteamVR/OpenVR), with controls designed for one hand |
| **AmanatsuUncensor** | Removes the mosaic, replaces the censored genitals with 3D genitals with physics, and adds **Freemode** |
| **AmanatsuTranslation** | Translates the UI and dialogue (English and Portuguese) |

---

## Building

You need .NET SDK 6+, Python 3 and the game with BepInEx 6 IL2CPP installed and run once. That first run generates `BepInEx\interop`, which the projects reference through relative paths.

1. Put each project folder in `<game>\UserData\tools\` (the `.csproj` files reference `..\..\..\BepInEx\...`).
2. Run `dotnet build -c Release` in each project. The output is `bin\Release\net6.0\<Project>.dll`.
3. Copy the DLL to `BepInEx\plugins\<Project>\`.
   - AmanatsuVR also needs the files from its `lib\` folder next to it (SteamVR libraries and `openvr_api.dll`).
   - AmanatsuVR also needs `openvr_api.dll` and `XRSDKOpenVR.dll` in `AmanatsuLocation_Data\Plugins\x86_64\`.
   - AmanatsuVR also needs the OpenVR `StreamingAssets` and `UnitySubsystems` folders. These folders do not come with the game.
4. `build_dist.py` does steps 2 and 3 for all projects. It also builds the `dist` folder, which mirrors the game root (drop-in install), and this `dist_src` folder.

Build configurations:

| | Logging |
| :--- | :--- |
| **Debug** | Verbose `Info`/`Debug` logs, which help with diagnostics |
| **Release** | Warnings and errors only; the verbose calls are compiled out with `[Conditional("DEBUG")]` |

Diagnostics: in Debug builds, **F9** during H dumps the deformed body meshes as OBJ to `BepInEx\genital_dump\`. It also logs the penis/vulva state frame by frame for 4 seconds (`[GEN][TRACE]`). The VR plugin also appends its log to `AmanatsuVR_diag\vr_historico.log`.

Code comments are mostly in Portuguese; identifiers, config keys and log tags are in English.

---

## AmanatsuVR

Renders the whole game in stereo with 6DoF tracking: title, menus, map, massage, water gun, character creation and H. The game's 2D UI is shown on a **floating panel** in front of you and used through a **laser** from the controller.

Main pieces:

| File | Role |
| :--- | :--- |
| `VRCamera` | The VR rig. It follows the game camera, or a forced pose in H. It scales the world so that `WorldSize` 1.0 is real life size; the game models at x10 (`Human.MODEL_SCALE`). |
| `CameraHijacker` | Takes over the game camera and keeps it only as the render reference |
| `UIScreen` / `UGUICapture` | Floating UI panel |
| `VRControllerLaser` | Laser, virtual mouse and physical click injection |
| `VRController` | Stick locomotion on the map and in massage, panel toggle, scene-change handling |
| `VRHScene` | Free camera, first person, H shortcuts, aimed shot, automatic mode |
| `VRCharCreation` | Draws the model inside the UI in character creation |
| `VRWaterGunHandler` | Water gun on the controller |
| `PauseWatch` / `TimeScaleGuard` | Keeps the world from freezing when a pause screen is destroyed without restoring `Time.timeScale` |

### Control philosophy: one hand

Every command exists **on both hands, identically**, and none of them needs both hands at once. Each combination uses only the buttons of that same hand: trigger, grip and stick (including the stick click, L3/R3). The hand you used last (trigger or grip) becomes the "active hand" for the laser and the aim. You can play with either hand, switch whenever you like, or keep one hand free.

### Everywhere

| Action | How |
| :--- | :--- |
| Point and click on the UI | Aim the laser and press the **trigger**; that hand becomes the laser hand |
| Scroll lists | With the laser over a list, use the **stick** of the same hand |
| Recenter the view | Controller **menu button** (or the `R` key, or double right click) |
| Look around | Move your head; head position is tracked too |

### Map and massage

| Action | How |
| :--- | :--- |
| Walk | **Stick**: moves in the direction you are looking |
| Turn | **Grip + stick** left/right |
| Up / down | **Grip + stick** up/down |
| Show / hide the panel | **Press and release the grip** without moving the stick |
| Click a character on the map | Laser + **trigger** |

Changing the viewpoint in the game, changing scenes or recentering resets the stick offset. After each scene change the panel reappears in front of your head.

### Water gun

| Action | How |
| :--- | :--- |
| Aim | The gun is attached to the controller; move your hand |
| Shoot | **Trigger** (with vibration) |

The gun hand is set by `WaterGunHand`.

### Character creation

The model is drawn inside the panel, in front of the background and behind the buttons. **Hiding the panel** (press and release the grip) shows the model in 3D stereo.

### H

H starts with a **free** camera at the game's framing.

| Action | How |
| :--- | :--- |
| Move the free camera | **Stick** (moves where you look) |
| Turn / up / down | **Grip + stick** (sideways turns, up/down rises and lowers) |
| Free camera ⇄ first person | **Tap** L3/R3 |
| Automatic mode on / off | **Hold** L3/R3 for 0.6 s (on: three short pulses; off: one long pulse) |
| Show / hide the panel | **Trigger + grip** (the panel appears in front of your head) |
| Start the act; once started, strong mode | **Trigger + stick up** (free camera) or **tap up** on the stick (first person) |
| Finish | **Trigger + stick** **left**, **down** or **right**: the 1st, 2nd and 3rd available finish button, left to right on screen |
| First person: switch man ⇄ woman | **Hold** the stick up (1.5 s, `HoldTime`) |

**First person:** the camera is pinned to the point between the actor's eyes and follows the actor's head. The head is hidden. Looking down moves the camera slightly forward so the neck cut is not visible. The view never rolls, and it never turns upside down, even when the actor lies on their back or arches the head back.

**Free shot** (the finish where you choose where to ejaculate):

- **In the man's first person:** the active hand becomes an aim with a laser, and the **trigger** shoots. **Trigger + grip** brings up the panel with the end button.
- **In the woman's first person or in automatic mode:** the target is random, and the shot ends on its own.

**Automatic mode:**

1. Starts the act and waits a random time (`AutoTimeMin`/`AutoTimeMax`).
2. Picks an available finish.
3. Switches to another pose of the same type and repeats.

Any manual act or finish shortcut turns automatic mode off.

### Main settings (`com.marcus.amanatsu.vr.cfg`)

| Option | Default | Purpose |
| :--- | :--- | :--- |
| `WorldSize` | 1.8 | World size (1.0 = real life size) |
| `MoveSpeed` | 1 | Stick speed, in m/s |
| `TurnSpeed` | 60 | Turn speed, in degrees/s |
| `UIScreenDistance` / `UIScreenScale` | 1.2 / 1 | Panel distance and size |
| `LaserStabilization` | 1 | Damps laser tremor in menus |
| `HeadSmoothing` / `FollowHeadTilt` | 6 / true | First person comfort |

---

## AmanatsuUncensor

| File | What it does |
| :--- | :--- |
| `Class1.cs` | Plugin entry point and config; removes the mosaic in real time |
| `Genitais.cs` | Replaces the censored genitals with 3D meshes. The vulva has bones that fingers and penis push open; the anus has its own cavity with folds and bone-driven opening. During penetration the penis gets back the length the game's animation takes away from it, and in anal it is aimed at the canal. |
| `Freemode.cs` | **Freemode** button on the title screen. You pick a location and heroine in Recollection and start a playable H, with a random male partner from the available cards. All poses are unlocked in Freemode: the heroine's progress gate, `AnimationListInfo.CheckReleasePhase`, is bypassed only while that H runs. |
| `TosSkip.cs` | Skips the terms-of-service server check that kills the title screen when the server cannot be reached. **This is a development bypass.** |
| `genitais/*.py` | Offline converter. It turns the Koikatsu Sunshine meshes into this game's `.bin` format and builds the procedural anal cavity. Needs `UnityPy` and `numpy`. |

The plugin loads the converted meshes from `BepInEx\plugins\AmanatsuUncensor\genitais\*.bin`.

The plugin does not modify any game file (no hard-modded `body_00.unity3d`), so it does not conflict with third-party mods that touch the body files.

See `AmanatsuUncensor/README.md` for the full technical notes (in Portuguese).

| Option (`com.amanatsu.uncensor.cfg`) | Purpose |
| :--- | :--- |
| `MaxDepth` / `AnalDepth` | How deep the penis goes before it starts shortening (vaginal / anal) |
| `ExtraDepth` | How much of the length the animation removes is given back |
| `MouthLength` / `MouthDepth` | Length in oral |
| `Collision` | Turns the push of fingers and penis on the vulva on or off |

## AmanatsuTranslation

Translates the UI (TextMeshPro, uGUI and the game's own text components) and dialogue from the files in `BepInEx\Translation\` (`en`, `pt`).

## CreationTuneUp

In character creation, the 0 to 100 sliders accept **-100 to 200**, both dragging and typing. The class comment explains every place where the game clamps the value, as read in IDA.

---

## Compatibility with other mods

- **Another uncensor** (**AL_Uncensor** or **UncensorSelector** loaded): AmanatsuUncensor turns off its 3D genitals, its bone changes (vulva, anus, penis stretch and aim) and the genital collision, and leaves the genitals to the other mod. Mosaic removal and Freemode keep working. Detection (`Genitais.OtherUncensor`) runs on the first `Human` update over the chainloader's loaded plugins (GUID, name and file). During our `Load()` the plugins after us are not loaded yet. The BepInEx log names the detected mod.
- **SliderUnlocker** (`SliderUnlocker.dll` anywhere under `BepInEx\plugins`): CreationTuneUp does not patch anything, since both would widen the same sliders. This one is checked by file at `Load()`.

---

## Credits

This project was only possible thanks to other people's work:

- **HC_VRTrial** (VR mod for HoneyCome, ILLGAMES): AmanatsuVR's base architecture comes from it. That includes the VR camera, the UI panel, the camera hijacking, the tracking translation and the packaging of the SteamVR libraries.
- **BetterPenetration** by **Animal42069** (Koikatsu Sunshine): the male and female 3D genital meshes come from it, converted for this game.
- **SoS** by **DeathWeasel** (Koikatsu): meshes used in the conversion.
- **UncensorSelector** (KK_Plugins, by **DeathWeasel** and IllusionMods): the technique of swapping the mesh and rebinding bones by name.
- Koikatsu uncensors **Profundis**, **Moderchan**, **Nam** and the **SAC** pack: measured as an anatomy reference. None of their meshes is included.
- **BepInEx**, **Il2CppInterop** and **HarmonyX** (BepInEx team): the plugin loader and patching.
- **Il2CppDumper** (Perfare) and **UnityPy** (K0lb3): used to read the game and convert the meshes.
- **SteamVR / OpenVR** (Valve).

Free for non-commercial use. If you redistribute, keep these credits.
