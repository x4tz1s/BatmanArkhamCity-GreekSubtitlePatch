using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using VCDiff.Decoders;

namespace UsmSubtitlePatcher
{
    // P/Invoke bindings for liblzo2-2.dll (bundled alongside the exe), matching
    // the exact two functions this whole pipeline was built and verified against:
    // lzo1x_1_compress / lzo1x_decompress_safe.
    static class Lzo
    {
        [DllImport("liblzo2-2.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int lzo1x_decompress_safe(byte[] src, uint src_len, byte[] dst, ref uint dst_len, IntPtr wrkmem);

        [DllImport("liblzo2-2.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int lzo1x_1_compress(byte[] src, uint src_len, byte[] dst, ref uint dst_len, byte[] wrkmem);

        public static byte[] Decompress(byte[] compressed, int uncompressedSize)
        {
            var dst = new byte[uncompressedSize];
            uint dstLen = (uint)uncompressedSize;
            int ret = lzo1x_decompress_safe(compressed, (uint)compressed.Length, dst, ref dstLen, IntPtr.Zero);
            if (ret != 0)
                throw new InvalidOperationException($"lzo1x_decompress_safe failed, ret={ret}");
            if (dstLen != uncompressedSize)
                throw new InvalidOperationException($"decompressed size mismatch: got {dstLen}, expected {uncompressedSize}");
            return dst;
        }

        public static byte[] Compress(byte[] data)
        {
            var wrkmem = new byte[16384 * 8];
            var dst = new byte[data.Length + data.Length / 16 + 64 + 3];
            uint dstLen = (uint)dst.Length;
            int ret = lzo1x_1_compress(data, (uint)data.Length, dst, ref dstLen, wrkmem);
            if (ret != 0)
                throw new InvalidOperationException($"lzo1x_1_compress failed, ret={ret}");
            var result = new byte[dstLen];
            Array.Copy(dst, result, (int)dstLen);
            return result;
        }
    }

    class ChunkEntry
    {
        public int UOff, USize, COff, CSize;
    }

    class DialogueManifest
    {
        public string relative_path { get; set; } = "";
        public int n_chunks { get; set; }
        public int header_prefix_len { get; set; }
        public int table_start { get; set; }
        public int meta_start { get; set; }
        public int body_start { get; set; }
        public string content_orig_sha256 { get; set; } = "";
        public string content_patched_sha256 { get; set; } = "";
        public string vcdiff_file { get; set; } = "";
        public int[] chunk_sizes { get; set; } = Array.Empty<int>();
    }

    static class DialoguePatcher
    {
        const uint SIGNATURE = 0x9E2A83C1;

        static ChunkEntry[] ReadChunkTable(byte[] data, int tableStart, int nChunks)
        {
            var chunks = new ChunkEntry[nChunks];
            for (int i = 0; i < nChunks; i++)
            {
                int off = tableStart + i * 16;
                chunks[i] = new ChunkEntry
                {
                    UOff = BitConverter.ToInt32(data, off + 0),
                    USize = BitConverter.ToInt32(data, off + 4),
                    COff = BitConverter.ToInt32(data, off + 8),
                    CSize = BitConverter.ToInt32(data, off + 12),
                };
            }
            return chunks;
        }

        // Mirrors ue3_lzo.parse_and_decompress_chunk: reads one physical
        // CompressedChunkHeader at data[compressedOffset] and returns its full
        // decompressed bytes (length == uncompressedSizeTotal).
        static byte[] DecompressOneChunk(byte[] data, int compressedOffset, int uncompressedSizeTotal)
        {
            int off = compressedOffset;
            uint tag = BitConverter.ToUInt32(data, off); off += 4;
            int chunkSize = BitConverter.ToInt32(data, off); off += 4;
            if (unchecked((uint)chunkSize) == SIGNATURE)
                chunkSize = 0x20000;
            int summaryCompressed = BitConverter.ToInt32(data, off); off += 4;
            int summaryUncompressed = BitConverter.ToInt32(data, off); off += 4;
            if (summaryUncompressed != uncompressedSizeTotal)
                throw new InvalidOperationException($"chunk summary mismatch: {summaryUncompressed} != {uncompressedSizeTotal}");

            int numBlocks = (summaryUncompressed + chunkSize - 1) / chunkSize;
            var blocks = new (int c, int u)[numBlocks];
            for (int i = 0; i < numBlocks; i++)
            {
                blocks[i] = (BitConverter.ToInt32(data, off), BitConverter.ToInt32(data, off + 4));
                off += 8;
            }

            using var outMs = new MemoryStream(summaryUncompressed);
            foreach (var (c, u) in blocks)
            {
                var blockBytes = new byte[c];
                Array.Copy(data, off, blockBytes, 0, c);
                off += c;
                if (c == u)
                {
                    outMs.Write(blockBytes, 0, c);
                }
                else
                {
                    var dec = Lzo.Decompress(blockBytes, u);
                    outMs.Write(dec, 0, dec.Length);
                }
            }
            return outMs.ToArray();
        }

        static byte[] DecompressFull(byte[] raw, int tableStart, int nChunks, out ChunkEntry[] chunks)
        {
            chunks = ReadChunkTable(raw, tableStart, nChunks);
            int bodyStart = chunks[0].UOff;
            using var virt = new MemoryStream();
            virt.Write(raw, 0, bodyStart);
            foreach (var ch in chunks)
            {
                var dec = DecompressOneChunk(raw, ch.COff, ch.USize);
                virt.Write(dec, 0, dec.Length);
            }
            return virt.ToArray();
        }

        // Mirrors ue3_lzo.build_chunk, with the same c==u collision-avoidance retry
        // used (and verified) in the manifest generator: parse_and_decompress_chunk
        // treats compressed_size==uncompressed_size as "stored raw", which is wrong
        // when it's genuine (incompressible) LZO output that happens to match length.
        static byte[] BuildChunkSafe(byte[] data)
        {
            int[] deltas = { 0, 64, 128, 256, 512, 1024, 2048, 4096 };
            foreach (var delta in deltas)
            {
                int cs = 0x20000 - delta;
                int numBlocks = (data.Length + cs - 1) / cs;
                var comps = new byte[numBlocks][];
                bool collision = false;
                for (int i = 0; i < numBlocks; i++)
                {
                    int start = i * cs;
                    int len = Math.Min(cs, data.Length - start);
                    var piece = new byte[len];
                    Array.Copy(data, start, piece, 0, len);
                    var comp = Lzo.Compress(piece);
                    if (comp.Length == piece.Length) { collision = true; break; }
                    comps[i] = comp;
                }
                if (collision) continue;
                return AssembleChunk(data, cs, comps);
            }
            throw new InvalidOperationException("could not find a collision-free sub-block size");
        }

        static byte[] AssembleChunk(byte[] data, int chunkSize, byte[][] comps)
        {
            int numBlocks = comps.Length;
            int totalCompressed = comps.Sum(c => c.Length);
            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms, System.Text.Encoding.ASCII, true))
            {
                bw.Write(SIGNATURE);
                bw.Write(chunkSize);
                bw.Write(totalCompressed);
                bw.Write(data.Length);
                for (int i = 0; i < numBlocks; i++)
                {
                    int start = i * chunkSize;
                    int len = Math.Min(chunkSize, data.Length - start);
                    bw.Write(comps[i].Length);
                    bw.Write(len);
                }
                foreach (var c in comps) bw.Write(c);
            }
            return ms.ToArray();
        }

        static string Sha256Hex(byte[] data)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(data)).ToLowerInvariant();
        }

        static string ContentHash(byte[] virtBytes, int metaStart, int bodyStart)
        {
            using var ms = new MemoryStream();
            ms.Write(virtBytes, 0, metaStart);
            ms.Write(virtBytes, bodyStart, virtBytes.Length - bodyStart);
            return Sha256Hex(ms.ToArray());
        }

        static byte[] ApplyVcdiff(byte[] source, byte[] delta)
        {
            using var srcStream = new MemoryStream(source);
            using var deltaStream = new MemoryStream(delta);
            using var outStream = new MemoryStream();
            using var decoder = new VcDecoder(srcStream, deltaStream, outStream, int.MaxValue, false);
            var result = decoder.Decode(out long bytesWritten);
            if (result != VCDiff.Includes.VCDiffResult.SUCCESS)
                throw new InvalidOperationException($"VCDIFF decode failed: {result}");
            return outStream.ToArray();
        }

        public static (int ok, int already, int failed) Run(string gameDir, string patchesDir)
        {
            var manifestFiles = Directory.GetFiles(patchesDir, "*.json")
                .Where(f => Path.GetFileName(f) != "index.json")
                .OrderBy(f => f)
                .ToArray();

            int ok = 0, already = 0, failed = 0;

            foreach (var mf in manifestFiles)
            {
                var m = JsonSerializer.Deserialize<DialogueManifest>(File.ReadAllText(mf))!;
                string target = Path.Combine(gameDir, m.relative_path.Replace('/', Path.DirectorySeparatorChar));

                Console.Write($"{m.relative_path} ... ");

                if (!File.Exists(target))
                {
                    Console.WriteLine("MISSING (skipped)");
                    failed++;
                    continue;
                }

                try
                {
                    byte[] current = File.ReadAllBytes(target);
                    var currentVirt = DecompressFull(current, m.table_start, m.n_chunks, out var curChunks);
                    int bodyStart = curChunks[0].UOff;
                    string curHash = ContentHash(currentVirt, m.meta_start, bodyStart);

                    if (curHash == m.content_patched_sha256)
                    {
                        Console.WriteLine("already patched, ok");
                        already++;
                        continue;
                    }
                    if (curHash != m.content_orig_sha256)
                    {
                        Console.WriteLine("UNEXPECTED CONTENT (not pristine, not already-patched) - skipped for safety");
                        failed++;
                        continue;
                    }

                    byte[] vcdiffBytes = File.ReadAllBytes(Path.Combine(patchesDir, m.vcdiff_file));
                    byte[] newVirt = ApplyVcdiff(currentVirt, vcdiffBytes);

                    byte[] finalBytes = Rebuild(current, newVirt, m.table_start, m.meta_start, m.header_prefix_len, m.n_chunks, bodyStart, m.chunk_sizes);

                    // verify: decompress our own output and confirm it matches expected
                    var verifyVirt = DecompressFull(finalBytes, m.table_start, m.n_chunks, out _);
                    string verifyHash = ContentHash(verifyVirt, m.meta_start, bodyStart);
                    if (verifyHash != m.content_patched_sha256)
                    {
                        Console.WriteLine("INTERNAL VERIFY FAILED - not writing (please report this)");
                        failed++;
                        continue;
                    }

                    string backupPath = target + ".pristine_backup";
                    if (!File.Exists(backupPath))
                        File.Copy(target, backupPath, overwrite: false);

                    File.WriteAllBytes(target, finalBytes);
                    Console.WriteLine("patched OK");
                    ok++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ERROR: {ex.Message}");
                    failed++;
                }
            }

            return (ok, already, failed);
        }

        static byte[] Rebuild(byte[] currentRaw, byte[] newVirt, int tableStart, int metaStart, int headerPrefixLen, int nChunks, int bodyStart, int[] chunkSizes)
        {
            var headerPrefix = new byte[headerPrefixLen];
            Array.Copy(newVirt, 0, headerPrefix, 0, metaStart);
            Array.Copy(currentRaw, metaStart, headerPrefix, metaStart, headerPrefixLen - metaStart);

            int uoffCheck = BitConverter.ToInt32(headerPrefix, tableStart);
            if (uoffCheck != bodyStart)
                throw new InvalidOperationException($"layout sanity check failed: {uoffCheck} != {bodyStart}");

            // Re-chunk using the EXACT per-chunk decompressed sizes the real,
            // in-game-verified file uses -- NOT an arbitrary equal split. An equal
            // split can carve a chunk boundary through the middle of a single
            // export's serialized bytes, which the game's loader does not tolerate
            // (confirmed: caused an infinite busy-loop hang on launch in testing).
            if (chunkSizes.Length != nChunks)
                throw new InvalidOperationException($"chunk_sizes length {chunkSizes.Length} != n_chunks {nChunks}");
            long totalBodyLong = (long)newVirt.Length - bodyStart;
            long sumSizes = 0;
            foreach (var s in chunkSizes) sumSizes += s;
            if (sumSizes != totalBodyLong)
                throw new InvalidOperationException($"chunk_sizes sum {sumSizes} != body length {totalBodyLong}");

            var boundaries = new int[nChunks + 1];
            boundaries[0] = bodyStart;
            for (int i = 0; i < nChunks; i++)
                boundaries[i + 1] = boundaries[i] + chunkSizes[i];

            var pieces = new byte[nChunks][];
            for (int i = 0; i < nChunks; i++)
            {
                int len = boundaries[i + 1] - boundaries[i];
                pieces[i] = new byte[len];
                Array.Copy(newVirt, boundaries[i], pieces[i], 0, len);
            }

            var physicals = new byte[nChunks][];
            for (int i = 0; i < nChunks; i++)
                physicals[i] = BuildChunkSafe(pieces[i]);

            var newUOff = new int[nChunks];
            int cursorU = bodyStart;
            for (int i = 0; i < nChunks; i++) { newUOff[i] = cursorU; cursorU += pieces[i].Length; }

            var newCOff = new int[nChunks];
            int cursorC = headerPrefixLen;
            for (int i = 0; i < nChunks; i++) { newCOff[i] = cursorC; cursorC += physicals[i].Length; }

            for (int i = 0; i < nChunks; i++)
            {
                int entryOff = tableStart + i * 16;
                Array.Copy(BitConverter.GetBytes(newUOff[i]), 0, headerPrefix, entryOff + 0, 4);
                Array.Copy(BitConverter.GetBytes(pieces[i].Length), 0, headerPrefix, entryOff + 4, 4);
                Array.Copy(BitConverter.GetBytes(newCOff[i]), 0, headerPrefix, entryOff + 8, 4);
                Array.Copy(BitConverter.GetBytes(physicals[i].Length), 0, headerPrefix, entryOff + 12, 4);
            }

            using var ms = new MemoryStream();
            ms.Write(headerPrefix, 0, headerPrefix.Length);
            foreach (var p in physicals) ms.Write(p, 0, p.Length);
            return ms.ToArray();
        }
    }
}
