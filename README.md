# UsmSubtitlePatcher

Applies the Greek cutscene-subtitle translation for **Batman: Arkham City GOTY**
directly on top of your own game files, instead of shipping ~8GB of re-encoded
video files.

Batman: Arkham City stores cutscene subtitles as plain UTF-8 text inside its
`.usm` cutscene video files (CRI Sofdec2 containers). Translating them to Greek
only changes a handful of small text chunks per file — but because the files
are hundreds of megabytes each, distributing the whole rewritten videos would
mean an ~8GB mod download. This tool instead ships a tiny (~150KB total) list
of exact byte-level edits and applies them locally to the `.usm` files you
already have installed via Steam.

This is part of the [Greek translation mod for Batman: Arkham City
GOTY](https://www.nexusmods.com/) (see the Nexus page for the full mod,
including menu/UI text, in-game dialogue, and fonts — this tool only handles
the cutscene video subtitles).

## Usage

1. Make sure Batman: Arkham City GOTY is installed and up to date via Steam,
   and apply the rest of the Greek translation mod first (see the Nexus mod
   page).
2. Download the latest release zip from the
   [Releases page](../../releases), and extract it anywhere.
3. Run `UsmSubtitlePatcher.exe`. It will try to auto-detect your game folder;
   if it can't, paste the full path to the `Batman Arkham City GOTY` folder
   (the one containing `BmGame`) when asked.
4. The tool checks each video file before touching it — it only patches files
   that exactly match the known, unmodified Steam version, and it saves a
   `<filename>.pristine_backup` copy of every file it changes. Running it
   again on files that are already patched is safe (it just confirms and does
   nothing).

## How it works / safety

Each entry in `Patches/*.json` records the original file's exact size and
SHA-256 hash, the resulting patched file's size and hash, and the list of
`(start, end, new_bytes)` byte-range replacements between them. Before writing
anything, the tool verifies your file's hash matches the known pristine
version; after building the patched result in memory, it re-verifies the
result's hash matches the expected patched hash before writing it to disk. If
either check fails, that file is left untouched and reported, rather than
guessed at.

If anything goes wrong, restore `<filename>.pristine_backup` back over the
patched file to fully revert.

## Building from source

```
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:SignAssembly=false -p:PublishAot=false -p:PublishSingleFile=true
```

## Why not just post an .exe to Nexus?

Nexus Mods does not allow executable files in mod downloads (for good
security reasons — it can't practically vet arbitrary binaries). This tool is
therefore distributed here on GitHub instead, with the Nexus mod page linking
to it, matching the approach used by this author's other Greek translation
mods for binary-patch tools.
