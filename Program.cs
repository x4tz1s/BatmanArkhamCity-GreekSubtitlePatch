using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace UsmSubtitlePatcher
{
    class EditOp
    {
        public long Start { get; set; }
        public long End { get; set; }
        public byte[] NewBytes { get; set; } = Array.Empty<byte>();
    }

    class PatchManifest
    {
        public string RelativePath { get; set; } = "";
        public long OrigSize { get; set; }
        public string OrigSha256 { get; set; } = "";
        public long PatchedSize { get; set; }
        public string PatchedSha256 { get; set; } = "";
        public List<EditOp> Edits { get; set; } = new();
    }

    class Program
    {
        static int Main(string[] args)
        {
            Console.WriteLine("=== Batman Arkham City GOTY - Greek Patcher (cutscene videos + dialogue) ===");
            Console.WriteLine();

            string exeDir = AppContext.BaseDirectory;
            string usmPatchesDir = Path.Combine(exeDir, "Patches");
            string dialoguePatchesDir = Path.Combine(exeDir, "DialoguePatches");
            bool haveUsm = Directory.Exists(usmPatchesDir);
            bool haveDialogue = Directory.Exists(dialoguePatchesDir);
            if (!haveUsm && !haveDialogue)
            {
                Console.WriteLine($"ERROR: neither 'Patches' nor 'DialoguePatches' found next to the executable ({exeDir}).");
                Pause();
                return 1;
            }

            string gameDir = args.Length > 0 ? args[0] : FindOrAskGameDir();
            if (gameDir == null || !Directory.Exists(gameDir))
            {
                Console.WriteLine("ERROR: game folder not found or not provided.");
                Pause();
                return 1;
            }
            Console.WriteLine($"Game folder: {gameDir}");
            Console.WriteLine();

            int ok = 0, already = 0, failed = 0;

            if (haveUsm)
            {
                Console.WriteLine("--- Cutscene subtitles (video files) ---");
                var (o, a, f) = RunUsmPatches(gameDir, usmPatchesDir);
                ok += o; already += a; failed += f;
                Console.WriteLine();
            }

            if (haveDialogue)
            {
                Console.WriteLine("--- Gameplay dialogue (UPK files) ---");
                var (o, a, f) = DialoguePatcher.Run(gameDir, dialoguePatchesDir);
                ok += o; already += a; failed += f;
                Console.WriteLine();
            }

            Console.WriteLine($"Done. Patched now: {ok}, already patched: {already}, failed/skipped: {failed}.");
            if (failed > 0)
            {
                Console.WriteLine("Some files were not touched - see messages above. Pristine copies (when found) were saved as '<file>.pristine_backup'.");
            }
            Pause();
            return failed > 0 ? 2 : 0;
        }

        static (int ok, int already, int failed) RunUsmPatches(string gameDir, string patchesDir)
        {
            var manifestFiles = Directory.GetFiles(patchesDir, "*.json")
                .Where(f => Path.GetFileName(f) != "index.json")
                .OrderBy(f => f)
                .ToArray();

            int ok = 0, already = 0, failed = 0;

            foreach (var mf in manifestFiles)
            {
                var manifest = JsonSerializer.Deserialize<PatchManifestRaw>(File.ReadAllText(mf))!;
                var m = manifest.ToManifest();
                string target = Path.Combine(gameDir, m.RelativePath.Replace('/', Path.DirectorySeparatorChar));

                Console.Write($"{m.RelativePath} ... ");

                if (!File.Exists(target))
                {
                    Console.WriteLine("MISSING (skipped)");
                    failed++;
                    continue;
                }

                byte[] current = File.ReadAllBytes(target);
                string currentHash = Sha256Hex(current);

                if (current.LongLength == m.PatchedSize && currentHash == m.PatchedSha256)
                {
                    Console.WriteLine("already patched, ok");
                    already++;
                    continue;
                }

                if (current.LongLength != m.OrigSize || currentHash != m.OrigSha256)
                {
                    Console.WriteLine("UNEXPECTED CONTENT (not pristine, not already-patched) - skipped for safety");
                    failed++;
                    continue;
                }

                // apply edits
                byte[] outBytes = ApplyEdits(current, m.Edits);
                string outHash = Sha256Hex(outBytes);
                if (outBytes.LongLength != m.PatchedSize || outHash != m.PatchedSha256)
                {
                    Console.WriteLine("INTERNAL VERIFY FAILED - not writing (please report this)");
                    failed++;
                    continue;
                }

                string backupPath = target + ".pristine_backup";
                if (!File.Exists(backupPath))
                {
                    File.Copy(target, backupPath, overwrite: false);
                }

                File.WriteAllBytes(target, outBytes);
                Console.WriteLine("patched OK");
                ok++;
            }

            return (ok, already, failed);
        }

        static byte[] ApplyEdits(byte[] data, List<EditOp> edits)
        {
            var sorted = edits.OrderBy(e => e.Start).ToList();
            using var ms = new MemoryStream();
            long pos = 0;
            foreach (var e in sorted)
            {
                ms.Write(data, (int)pos, (int)(e.Start - pos));
                ms.Write(e.NewBytes, 0, e.NewBytes.Length);
                pos = e.End;
            }
            ms.Write(data, (int)pos, (int)(data.LongLength - pos));
            return ms.ToArray();
        }

        static string Sha256Hex(byte[] data)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(data)).ToLowerInvariant();
        }

        static string? FindOrAskGameDir()
        {
            // Try a few common Steam library locations (Windows-focused, since this ships to Windows users)
            string[] candidates =
            {
                @"C:\Program Files (x86)\Steam\steamapps\common\Batman Arkham City GOTY",
                @"C:\Program Files\Steam\steamapps\common\Batman Arkham City GOTY",
                @"D:\SteamLibrary\steamapps\common\Batman Arkham City GOTY",
                @"D:\Steam\steamapps\common\Batman Arkham City GOTY",
                @"E:\SteamLibrary\steamapps\common\Batman Arkham City GOTY",
            };
            foreach (var c in candidates)
            {
                if (Directory.Exists(c) && Directory.Exists(Path.Combine(c, "BmGame")))
                    return c;
            }

            Console.WriteLine("Could not auto-detect the game folder.");
            Console.Write("Please paste the full path to 'Batman Arkham City GOTY' (the folder containing 'BmGame'): ");
            string? input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) return null;
            input = input.Trim().Trim('"');
            return input;
        }

        static void Pause()
        {
            Console.WriteLine();
            Console.WriteLine("Press Enter to exit...");
            Console.ReadLine();
        }
    }

    // Raw JSON shape (snake_case + base64 bytes) mapped to the internal PatchManifest
    class PatchManifestRaw
    {
        public string relative_path { get; set; } = "";
        public long orig_size { get; set; }
        public string orig_sha256 { get; set; } = "";
        public long patched_size { get; set; }
        public string patched_sha256 { get; set; } = "";
        public List<EditRaw> edits { get; set; } = new();

        public PatchManifest ToManifest()
        {
            return new PatchManifest
            {
                RelativePath = relative_path,
                OrigSize = orig_size,
                OrigSha256 = orig_sha256,
                PatchedSize = patched_size,
                PatchedSha256 = patched_sha256,
                Edits = edits.Select(e => new EditOp
                {
                    Start = e.start,
                    End = e.end,
                    NewBytes = Convert.FromBase64String(e.new_bytes_b64)
                }).ToList()
            };
        }
    }

    class EditRaw
    {
        public long start { get; set; }
        public long end { get; set; }
        public string new_bytes_b64 { get; set; } = "";
    }
}
