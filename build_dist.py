"""Builds the two distribution folders (py -3 build_dist.py):

dist/     mirrors the game root: copy its contents over a vanilla install. Plugins in Release.
dist_src/ source code of our plugins (no build output, no generated data, no dev tools).

Run after `dotnet build -c Release` of the four projects (this script does it too).
"""
import os
import shutil
import subprocess

GAME = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
TOOLS = os.path.join(GAME, "UserData", "tools")
DIST = os.path.join(GAME, "dist")
SRC = os.path.join(GAME, "dist_src")
PROJECTS = ["AmanatsuVR", "AmanatsuUncensor", "AmanatsuTranslation", "CreationTuneUp"]
README = os.path.join(TOOLS, "DIST_README.md")          # dist (Portuguese, for players)
SRC_README = os.path.join(TOOLS, "DIST_SRC_README.md")  # dist_src (English, for developers)


def reset(d):
    if os.path.exists(d):
        shutil.rmtree(d)
    os.makedirs(d)


def copy_file(src, dst):
    if not os.path.isfile(src):
        raise SystemExit(f"AUSENTE: {src}")
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    shutil.copy2(src, dst)


def copy_game(rel, ignore=None):
    src, dst = os.path.join(GAME, rel), os.path.join(DIST, rel)
    if os.path.isdir(src):
        shutil.copytree(src, dst, ignore=ignore)
    else:
        copy_file(src, dst)


def build_release():
    for p in PROJECTS:
        r = subprocess.run(["dotnet", "build", "-c", "Release", "-v:q"], cwd=os.path.join(TOOLS, p),
                           capture_output=True, text=True)
        if r.returncode != 0:
            raise SystemExit(f"build {p} falhou:\n{r.stdout[-2000:]}")
        print("build Release ok:", p)


def build_dist():
    reset(DIST)
    # BepInEx: core + runtime deps. No logs, caches, dumps or dev patchers (AmanatsuRdoc = RenderDoc loader).
    for rel in (r"BepInEx\core", r"BepInEx\unity-libs", r"BepInEx\interop"):
        copy_game(rel)
    copy_game(r"BepInEx\Translation", ignore=shutil.ignore_patterns("_missing.txt"))
    os.makedirs(os.path.join(DIST, "BepInEx", "patchers"))
    # Only the loader's own config; our plugins write theirs with defaults on first run.
    for cfg in ("BepInEx.cfg",):
        if os.path.isfile(os.path.join(GAME, "BepInEx", "config", cfg)):
            copy_game(os.path.join("BepInEx", "config", cfg))

    # Our plugins: Release DLL + the runtime files that live next to it.
    for p in PROJECTS:
        live = os.path.join(GAME, "BepInEx", "plugins", p)
        dst = os.path.join(DIST, "BepInEx", "plugins", p)
        shutil.copytree(live, dst, ignore=shutil.ignore_patterns("*.pdb", "*.bak*", f"{p}.dll"))
        copy_file(os.path.join(TOOLS, p, "bin", "Release", "net6.0", f"{p}.dll"), os.path.join(dst, f"{p}.dll"))

    # Doorstop + .NET runtime
    for rel in ("dotnet", "winhttp.dll", "doorstop_config.ini", ".doorstop_version"):
        copy_game(rel)

    # OpenVR runtime the game does not ship with
    for rel in (r"AmanatsuLocation_Data\Plugins\x86_64\openvr_api.dll",
                r"AmanatsuLocation_Data\Plugins\x86_64\XRSDKOpenVR.dll",
                r"AmanatsuLocation_Data\StreamingAssets\SteamVR",
                r"AmanatsuLocation_Data\UnitySubsystems\XRSDKOpenVR"):
        copy_game(rel)

    # Uncensor hardmod: the modified body bundle, at its real path (drop-in).
    copy_game(r"lib\chara\body\body_00.unity3d")

    for rel in ("Iniciar_VR.bat", "Iniciar_Desktop.bat"):
        copy_game(rel)
    copy_file(README, os.path.join(DIST, "README.md"))


def build_src():
    reset(SRC)
    keep = (".cs", ".csproj", ".py", ".md")
    for p in PROJECTS:
        root = os.path.join(TOOLS, p)
        for dirpath, dirs, files in os.walk(root):
            rel = os.path.relpath(dirpath, root)
            parts = rel.split(os.sep)
            if parts[0] in ("bin", "obj") or any(x.startswith(("backup", "__pycache__")) for x in parts):
                dirs[:] = []
                continue
            # genitais/: only the converter scripts; its subfolders are generated meshes and renders
            if parts[0] == "genitais" and rel != "genitais":
                dirs[:] = []
                continue
            for f in files:
                src = os.path.join(dirpath, f)
                ok = f.endswith(keep)
                ok |= p == "AmanatsuVR" and parts[0] in ("AssetBundles", "lib") and f != "HC_VRTrial.dll"
                if ok:
                    copy_file(src, os.path.join(SRC, p, rel, f))
    copy_file(__file__, os.path.join(SRC, "build_dist.py"))
    copy_file(SRC_README, os.path.join(SRC, "README.md"))


if __name__ == "__main__":
    build_release()
    build_dist()
    build_src()
    for d in (DIST, SRC):
        n = sum(len(f) for _, _, f in os.walk(d))
        size = sum(os.path.getsize(os.path.join(a, x)) for a, _, fs in os.walk(d) for x in fs)
        print(f"{d}: {n} arquivos, {size / 1e6:.0f} MB")
