# UsmSubtitlePatcher

Applies the Greek translation for **Batman: Arkham City GOTY**'s cutscene
subtitles and gameplay dialogue directly on top of your own game files,
instead of shipping ~10GB of re-encoded video and dialogue-package files.

Batman: Arkham City stores cutscene subtitles as plain UTF-8 text inside its
`.usm` cutscene video files (CRI Sofdec2 containers), and gameplay/ambient
dialogue as strings inside LZO-chunk-compressed `.upk` package files.
Translating them to Greek only changes a small amount of text per file — but
because the underlying files are collectively many gigabytes, distributing the
whole rewritten files would mean an enormous mod download. This tool instead
ships a tiny list of exact edits (byte-level splices for videos, a
decompress-patch-recompress pipeline for dialogue packages) and applies them
locally to the files you already have installed via Steam.

This is part of the [Greek translation mod for Batman: Arkham City
GOTY](https://www.nexusmods.com/) (see the Nexus page for the full mod,
including menu/UI text and fonts — this tool only handles cutscene subtitles
and gameplay dialogue).

## Usage

1. Make sure Batman: Arkham City GOTY is installed and up to date via Steam,
   and apply the rest of the Greek translation mod first (see the Nexus mod
   page — menus, fonts, and the TFC Installer step).
2. Download the latest release zip from the
   [Releases page](../../releases), and extract it anywhere.
3. Run `UsmSubtitlePatcher.exe`. It will try to auto-detect your game folder;
   if it can't, paste the full path to the `Batman Arkham City GOTY` folder
   (the one containing `BmGame`) when asked.
4. The tool checks each file before touching it — it only patches files that
   exactly match the known, unmodified Steam version, and it saves a
   `<filename>.pristine_backup` copy of every file it changes. Running it
   again on files that are already patched is safe (it just confirms and does
   nothing).

## How it works / safety

**Cutscene videos** (`Patches/*.json`): each entry records the original
file's exact size and SHA-256 hash, the resulting patched file's size and
hash, and the list of `(start, end, new_bytes)` byte-range replacements
between them.

**Dialogue packages** (`DialoguePatches/*.json` + `.vcdiff`): each entry
records the package's internal LZO-chunk table layout plus a
[VCDIFF](https://www.rfc-editor.org/rfc/rfc3284) binary patch between the
pristine and translated *decompressed* content. At patch time the tool
decompresses the package's LZO chunks, applies the VCDIFF patch, re-chunks and
recompresses the result (bundled `liblzo2-2.dll`), and rewrites just the
package's chunk table — leaving everything else byte-for-byte identical to
what a full re-cook would produce.

In both cases: before writing anything, the tool verifies your file's content
hash matches the known pristine version; after building the patched result in
memory, it re-verifies the result matches the expected patched hash before
writing it to disk. If either check fails, that file is left untouched and
reported, rather than guessed at.

If anything goes wrong, restore `<filename>.pristine_backup` back over the
patched file to fully revert.

## Building from source

```
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:SignAssembly=false -p:PublishAot=false -p:PublishSingleFile=true
```

`liblzo2-2.dll` is the official [LZO](https://www.oberhumer.com/opensource/lzo/)
2.10 library, cross-compiled for win-x64 with MinGW (`./configure
--host=x86_64-w64-mingw32 --enable-shared --disable-static`) — verified to
produce byte-identical compressed output to the Linux build used to originally
build this mod.

## Why not just post an .exe to Nexus?

Nexus Mods does not allow executable files in mod downloads (for good
security reasons — it can't practically vet arbitrary binaries). This tool is
therefore distributed here on GitHub instead, with the Nexus mod page linking
to it, matching the approach used by this author's other Greek translation
mods for binary-patch tools.
