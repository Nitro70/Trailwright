"""Scan the published exe for personal or build-machine strings before a release.

    python tools/scan_release.py [extra needle ...]

Scans bin/publish/Trailwright.exe and every file inside its single-file bundle (unpacked here,
because the bundle is compressed and a plain byte search would see nothing inside it). Each
needle is searched as ASCII, UTF-8 and UTF-16, ignoring case. Adapted from the scanner in the
SonsOfTheForest live editor.

Personal needles (this machine's user name, home folder, repo path and its top folder, the git
e-mail, Steam library and SteamID) must not appear anywhere. Build needles (PDB paths, obj folders,
user folders, temp folders) must not appear in our own files; third-party files (RedLoader, which
ships unchanged from its official release, and the .NET runtime inside the app) are only counted
for those. Exit code 1 on any hit that must not be there.
"""
import getpass
import os
import struct
import subprocess
import sys
import zipfile
import zlib

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(REPO, "bin", "publish", "Trailwright.exe")
B = chr(92)
# The marker the .NET host writes into a single-file app, 8 bytes after the bundle header's offset
# (HostWriter.bundleSignature in dotnet/runtime).
BUNDLE_SIGNATURE = bytes.fromhex("8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae")


def git(*args):
    try:
        return subprocess.run(["git", *args], cwd=REPO, capture_output=True, text=True).stdout.strip()
    except OSError:
        return ""


def personal_needles():
    # The main checkout, when this runs from a worktree of it.
    common = git("rev-parse", "--path-format=absolute", "--git-common-dir")
    main = os.path.dirname(os.path.normpath(common)) if common else REPO
    drive, rest = os.path.splitdrive(main)
    top = drive + B + rest.strip(B + "/").replace("/", B).split(B)[0]
    email = git("config", "user.email")
    # A Steam library on some drive (E:\SteamLibrary); the bare folder name is in the game-finding code.
    # (the repo folder's own name is the product name here, so it is not a needle)
    needles = [getpass.getuser(), os.path.expanduser("~"), REPO, main, top,
               ":" + B + "SteamLibrary", "7656119", "claude", "anthropic"]
    if email:
        needles += [email, email.split("@")[0]]
    return [n for n in dict.fromkeys(needles) if n]


BUILD_NEEDLES = [".pdb", B + "obj" + B, "/obj/", ":" + B + "Users" + B, "AppData", "Temp" + B]


# Third-party text that contains a needle without being about anyone: Brotli's built-in dictionary
# of common words (inside .NET's System.IO.Compression.Native) contains common first names.
ALLOWED = [b"city of(this);andrew unique checked",
           # the app's own hint text naming an MCP client
           b"claude code: ', el('code', {}, `claude mcp add --transport http trailwright"]


def find(low, data, needle):
    """The first match of needle (ASCII or UTF-16, any case) that is not allowed text, or None."""
    for enc in ("ascii", "utf-8", "utf-16le"):
        try:
            pattern = needle.lower().encode(enc)
        except UnicodeEncodeError:
            continue
        i = low.find(pattern)
        while i != -1:
            window = low[max(0, i - 80):i + 80]
            if not any(a in window for a in ALLOWED):
                return enc, data[max(0, i - 40):i + 80]
            i = low.find(pattern, i + 1)
    return None


def bundle_files(data):
    """The files inside a .NET single-file bundle (format 6, .NET 6), decompressed."""
    sig = data.find(BUNDLE_SIGNATURE)
    if sig < 8:
        raise SystemExit("Trailwright.exe is not a .NET single-file bundle")
    pos = struct.unpack_from("<q", data, sig - 8)[0]
    major, _minor, count = struct.unpack_from("<IIi", data, pos)
    pos += 12

    def string(p):
        length = shift = 0
        while True:
            b = data[p]
            p += 1
            length |= (b & 0x7F) << shift
            shift += 7
            if b < 0x80:
                return data[p:p + length].decode("utf-8"), p + length

    _bundle_id, pos = string(pos)
    if major >= 2:
        pos += 8 * 4 + 8          # deps.json and runtimeconfig.json offset/size, flags
    for _ in range(count):
        offset, size = struct.unpack_from("<qq", data, pos)
        pos += 16
        compressed = 0
        if major >= 6:
            compressed = struct.unpack_from("<q", data, pos)[0]
            pos += 8
        pos += 1                  # file type
        path, pos = string(pos)
        raw = data[offset:offset + (compressed or size)]
        body = zlib.decompress(raw, -15) if compressed else raw
        if len(body) != size:
            raise SystemExit(f"bundle entry {path}: unpacked {len(body)} bytes, expected {size}")
        yield path, body


def main():
    personal = personal_needles() + sys.argv[1:]
    exe = open(EXE, "rb").read()
    files = []
    bundle = list(bundle_files(exe))
    first = min((exe.find(body[:64]) for _, body in bundle if len(body) > 64 and exe.find(body[:64]) > 0), default=len(exe))
    files.append(("exe host", exe[:first], False))
    for path, body in bundle:
        files.append(("bundle/" + path, body, path.startswith("Trailwright.")))
    bad = third_party = 0
    for label, data, ours in files:
        low = data.lower()
        for needle in personal:
            hit = find(low, data, needle)
            if hit:
                bad += 1
                print(f"HIT {label}: personal string {needle!r} ({hit[0]}) ...{hit[1]!r}")
        for needle in BUILD_NEEDLES:
            hit = find(low, data, needle)
            if hit and ours:
                bad += 1
                print(f"HIT {label}: build string {needle!r} ({hit[0]}) ...{hit[1]!r}")
            elif hit:
                third_party += 1
    ours_count = sum(1 for f in files if f[2])
    print(f"scanned {len(files)} files ({ours_count} ours: " + ", ".join(f[0] for f in files if f[2]) + ")")
    if third_party:
        print(f"note: {third_party} build-string matches in third-party files (their own build paths, not ours)")
    print(f"{bad} hits")
    return 1 if bad else 0


if __name__ == "__main__":
    raise SystemExit(main())
