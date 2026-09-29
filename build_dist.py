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
SRC = os.path.join(GAME, "dist_src", "AmanatsuLocation")  # the published git repo
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


KEEP = os.path.join("BepInEx", "Translation")  # Marcus edits the translations directly in dist/: never touched


def clean_dist():
    """Empties dist/ except BepInEx/Translation."""
    keep = os.path.join(DIST, KEEP)
    if not os.path.isdir(DIST):
        os.makedirs(DIST)
        return
    for dirpath, dirs, files in os.walk(DIST, topdown=False):
        if dirpath == keep or dirpath.startswith(keep + os.sep):
            continue
        for f in files:
            os.remove(os.path.join(dirpath, f))
        for d in dirs:
            path = os.path.join(dirpath, d)
            if path != keep and not path.startswith(keep + os.sep) and not os.listdir(path):
                os.rmdir(path)
            elif path != keep and os.path.isdir(path) and not os.listdir(path):
                os.rmdir(path)


def build_dist():
    clean_dist()
    # BepInEx: core + runtime deps. No logs, caches, dumps or dev patchers (AmanatsuRdoc = RenderDoc loader).
    for rel in (r"BepInEx\core", r"BepInEx\unity-libs", r"BepInEx\interop"):
        copy_game(rel)
    if not os.path.isdir(os.path.join(DIST, KEEP)):  # only the first time; afterwards dist/ owns it
        copy_game(KEEP, ignore=shutil.ignore_patterns("_missing.txt"))
    os.makedirs(os.path.join(DIST, "BepInEx", "patchers"))
    # Only the loader's own config; our plugins write theirs with defaults on first run.
    for cfg in ("BepInEx.cfg",):
        if os.path.isfile(os.path.join(GAME, "BepInEx", "config", cfg)):
            copy_game(os.path.join("BepInEx", "config", cfg))

    # Our plugins: Release DLL + the runtime files that live next to it.
    for p in PROJECTS:
        live = os.path.join(GAME, "BepInEx", "plugins", p)
        dst = os.path.join(DIST, "BepInEx", "plugins", p)
        if os.path.isdir(live):  # a plugin may be uninstalled from the live game (only its DLL ships)
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

    for rel in ("Iniciar_VR.bat", "Iniciar_Desktop.bat"):
        copy_game(rel)
    copy_file(README, os.path.join(DIST, "README.md"))


def build_src():
    # dist_src/AmanatsuLocation is Marcus's git repo (published). NEVER rmtree it: a reset of dist_src once
    # emptied .git/hooks, info and logs before stopping at a read-only object. Only the entries this script
    # generates are replaced; .git, .gitignore, .gitattributes and LICENSE are left alone.
    os.makedirs(SRC, exist_ok=True)
    for name in PROJECTS + ["README.md", "build_dist.py"]:
        path = os.path.join(SRC, name)
        if os.path.isdir(path):
            shutil.rmtree(path)
        elif os.path.isfile(path):
            os.remove(path)
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


ZIPS = os.path.join(GAME, "dist_zips")
# One zip per mod, each already laid out like the game root (extract over it). Paths are relative to dist/.
ZIP_PARTS = {
    "BepInEx-base": ["README.md", "dotnet", "winhttp.dll", "doorstop_config.ini", ".doorstop_version",
                     "BepInEx/core", "BepInEx/unity-libs", "BepInEx/interop", "BepInEx/patchers", "BepInEx/config"],
    "AmanatsuVR": ["BepInEx/plugins/AmanatsuVR", "AmanatsuLocation_Data", "Iniciar_VR.bat", "Iniciar_Desktop.bat"],
    "AmanatsuUncensor": ["BepInEx/plugins/AmanatsuUncensor"],
    "AmanatsuTranslation": ["BepInEx/plugins/AmanatsuTranslation", "BepInEx/Translation"],
    "CreationTuneUp": ["BepInEx/plugins/CreationTuneUp"],
}


def build_zips():
    """Zips whatever dist/ holds now (so manual edits there, e.g. translations, are what ships)."""
    import zipfile
    os.makedirs(ZIPS, exist_ok=True)  # zips are overwritten in place (rmtree fails while one is open elsewhere)
    seen = set()
    locked = []
    for name, parts in ZIP_PARTS.items():
        target = os.path.join(ZIPS, name + ".zip")
        try:
            os.remove(target) if os.path.exists(target) else None
        except PermissionError:
            print(f"LOCKED (open elsewhere), left as is: {target}")
            locked.append(name)
            continue
        with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as z:
            for part in parts:
                path = os.path.join(DIST, *part.split("/"))
                if os.path.isfile(path):
                    walk = [(os.path.dirname(path), [], [os.path.basename(path)])]
                else:
                    walk = os.walk(path)
                for d, _, fs in walk:
                    for f in fs:
                        full = os.path.join(d, f)
                        rel = os.path.relpath(full, DIST)
                        z.write(full, rel.replace(os.sep, "/"))
                        seen.add(rel)
    rest = [os.path.relpath(os.path.join(d, f), DIST) for d, _, fs in os.walk(DIST) for f in fs
            if os.path.relpath(os.path.join(d, f), DIST) not in seen]
    if rest and not locked:
        raise SystemExit(f"dist files outside every zip: {rest[:10]}")
    for z in sorted(os.listdir(ZIPS)):
        print(f"{z}: {os.path.getsize(os.path.join(ZIPS, z)) / 1e6:.1f} MB")


if __name__ == "__main__":
    build_release()
    build_dist()
    build_src()
    build_zips()
    for d in (DIST, SRC):
        n = sum(len(f) for _, _, f in os.walk(d))
        size = sum(os.path.getsize(os.path.join(a, x)) for a, _, fs in os.walk(d) for x in fs)
        print(f"{d}: {n} arquivos, {size / 1e6:.0f} MB")
