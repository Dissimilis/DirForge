using System.Buffers.Binary;
using System.Numerics;

namespace DirForge.Services;

public static partial class McpEndpoints
{
    // ── Sampling constants ────────────────────────────────────────────────────
    private const long SampleChunkSize = 2 * 1024 * 1024; // 2 MiB per chunk
    private const long SampleAlignBytes = 65_536; // 64 KiB alignment
    private const ulong SampleSeedGolden = 0x9E3779B97F4A7C15UL; // fractional golden ratio

    // ── SplitMix64 PRNG ───────────────────────────────────────────────────────
    private static ulong SplitMix64(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        ulong z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EDUL;
        return z ^ (z >> 31);
    }

    // ── XXH3-128 (seed = 0, default secret) ──────────────────────────────────
    // Primes from xxHash spec
    private const ulong Xp32_1 = 0x9E3779B1UL;
    private const ulong Xp32_2 = 0x85EBCA77UL;
    private const ulong Xp32_3 = 0xC2B2AE3DUL;
    private const ulong Xp64_1 = 0x9E3779B185EBCA87UL;
    private const ulong Xp64_2 = 0xC2B2AE3D27D4EB4FUL;
    private const ulong Xp64_3 = 0x165667B19E3779F9UL;
    private const ulong Xp64_4 = 0x85EBCA77C2B2AE63UL;
    private const ulong Xp64_5 = 0x27D4EB2F165667C5UL;

    // 192-byte default secret from the xxHash reference implementation
    private static ReadOnlySpan<byte> Xxh3Secret =>
    [
        0xb8,0xfe,0x6c,0x39,0x23,0xa4,0x4b,0xbe, 0x7c,0x01,0x81,0x2c,0xf7,0x21,0xad,0x1c,
        0xde,0xd4,0x6d,0xe9,0x83,0x90,0x97,0xdb, 0x72,0x40,0xa4,0xa4,0xb7,0xb3,0x67,0x1f,
        0xcb,0x79,0xe6,0x4e,0xcc,0xc0,0xe5,0x78, 0x82,0x5a,0xd0,0x7d,0xcc,0xff,0x72,0x21,
        0xb8,0x08,0x46,0x74,0xf7,0x43,0x24,0x8e, 0xe0,0x35,0x90,0xe6,0x81,0x3a,0x26,0x4c,
        0x3c,0x28,0x52,0xbb,0x91,0xc3,0x00,0xcb, 0x88,0xd0,0x65,0x8b,0x1b,0x53,0x2e,0xa3,
        0x71,0x64,0x48,0x97,0xa2,0x0d,0xf9,0x4e, 0x38,0x19,0xef,0x46,0xa9,0xde,0xac,0xd8,
        0xa8,0xfa,0x76,0x3f,0xe3,0x9c,0x34,0x3f, 0xf9,0xdc,0xbb,0xc7,0xc7,0x0b,0x4f,0x1d,
        0x8a,0x51,0xe0,0x4b,0xcd,0xb4,0x59,0x31, 0xc8,0x9f,0x7e,0xc9,0xd9,0x78,0x73,0x64,
        0xea,0xc5,0xac,0x83,0x34,0xd3,0xeb,0xc3, 0xc5,0x81,0xa0,0xff,0xfa,0x13,0x63,0xeb,
        0x17,0x0d,0xdd,0x51,0xb7,0xf0,0xda,0x49, 0xd3,0x16,0x55,0x26,0x29,0xd4,0x68,0x9e,
        0x2b,0x16,0xbe,0x58,0x7d,0x47,0xa1,0xfc, 0x8f,0xf8,0xb8,0xd1,0x7a,0xd0,0x31,0xce,
        0x45,0xcb,0x3a,0x8f,0x95,0x16,0x04,0x28, 0xaf,0xd7,0xfb,0xca,0xbb,0x4b,0x40,0x7e,
    ];

    // ── Primitives ────────────────────────────────────────────────────────────

    // 64×64 → 128-bit multiply, fold high into low
    private static ulong XMulFold64(ulong a, ulong b)
    {
        var p = (UInt128)a * b;
        return (ulong)p ^ (ulong)(p >> 64);
    }

    private static ulong XAvalanche(ulong h)
    {
        h ^= h >> 37;
        h *= 0x165667919E3779F9UL;
        h ^= h >> 32;
        return h;
    }

    private static ulong X64Avalanche(ulong h)
    {
        h ^= h >> 33;
        h *= Xp64_2;
        h ^= h >> 29;
        h *= Xp64_3;
        h ^= h >> 32;
        return h;
    }

    // Mix 16 bytes at input[i..] XOR secret[s..] (seed = 0)
    private static ulong XMix16B(ReadOnlySpan<byte> data, ReadOnlySpan<byte> sec, int i, int s)
        => XMulFold64(
            BinaryPrimitives.ReadUInt64LittleEndian(data[i..]) ^ BinaryPrimitives.ReadUInt64LittleEndian(sec[s..]),
            BinaryPrimitives.ReadUInt64LittleEndian(data[(i + 8)..]) ^ BinaryPrimitives.ReadUInt64LittleEndian(sec[(s + 8)..]));

    private static void XAccumulate512(ulong[] acc, ReadOnlySpan<byte> data, ReadOnlySpan<byte> sec)
    {
        for (int i = 0; i < 8; i++)
        {
            ulong dv = BinaryPrimitives.ReadUInt64LittleEndian(data[(8 * i)..]);
            ulong dk = dv ^ BinaryPrimitives.ReadUInt64LittleEndian(sec[(8 * i)..]);
            acc[i ^ 1] += dv;
            acc[i] += (dk & 0xFFFF_FFFFUL) * (dk >> 32);
        }
    }

    private static void XScrambleAcc(ulong[] acc, ReadOnlySpan<byte> sec)
    {
        for (int i = 0; i < 8; i++)
        {
            acc[i] ^= acc[i] >> 47;
            acc[i] ^= BinaryPrimitives.ReadUInt64LittleEndian(sec[(8 * i)..]);
            acc[i] *= Xp32_1;
        }
    }

    private static ulong XMergeAccs(ulong[] acc, ReadOnlySpan<byte> sec, int s, ulong start)
    {
        ulong r = start;
        for (int i = 0; i < 4; i++)
            r += XMulFold64(
                acc[2 * i] ^ BinaryPrimitives.ReadUInt64LittleEndian(sec[(s + 16 * i)..]),
                acc[2 * i + 1] ^ BinaryPrimitives.ReadUInt64LittleEndian(sec[(s + 16 * i + 8)..]));
        return XAvalanche(r);
    }

    // ── xxHash3 128-bit dispatch ──────────────────────────────────────────────

    private static (ulong Lo, ulong Hi) Xxh3Hash128(ReadOnlySpan<byte> data)
    {
        var s = Xxh3Secret;
        int len = data.Length;
        if (len > 240) return Xxh3Long128(data, s);
        if (len > 128) return Xxh3Mid128(data, s, len);
        if (len > 16) return Xxh3Short128(data, s, len);
        if (len > 8) return Xxh3Len9To16(data, s, len);
        if (len >= 4) return Xxh3Len4To8(data, s, len);
        if (len > 0) return Xxh3Len1To3(data, s, len);
        // len == 0
        ulong bl = BinaryPrimitives.ReadUInt64LittleEndian(s[64..]) ^ BinaryPrimitives.ReadUInt64LittleEndian(s[72..]);
        ulong bh = BinaryPrimitives.ReadUInt64LittleEndian(s[80..]) ^ BinaryPrimitives.ReadUInt64LittleEndian(s[88..]);
        return (X64Avalanche(bl), X64Avalanche(bh));
    }

    // len 1–3
    private static (ulong Lo, ulong Hi) Xxh3Len1To3(ReadOnlySpan<byte> d, ReadOnlySpan<byte> s, int len)
    {
        byte c1 = d[0], c2 = d[len >> 1], c3 = d[len - 1];
        uint combL = c1 | ((uint)c2 << 8) | ((uint)c3 << 16) | ((uint)len << 24);
        uint combH = BitOperations.RotateLeft(BinaryPrimitives.ReverseEndianness(combL), 13);
        ulong bfl = (ulong)BinaryPrimitives.ReadUInt32LittleEndian(s) ^ (ulong)BinaryPrimitives.ReadUInt32LittleEndian(s[4..]);
        ulong bfh = (ulong)BinaryPrimitives.ReadUInt32LittleEndian(s[8..]) ^ (ulong)BinaryPrimitives.ReadUInt32LittleEndian(s[12..]);
        return (X64Avalanche(combL ^ bfl), X64Avalanche(combH ^ bfh));
    }

    // len 4–8
    private static (ulong Lo, ulong Hi) Xxh3Len4To8(ReadOnlySpan<byte> d, ReadOnlySpan<byte> s, int len)
    {
        uint dLo = BinaryPrimitives.ReadUInt32LittleEndian(d);
        uint dHi = BinaryPrimitives.ReadUInt32LittleEndian(d[(len - 4)..]);
        ulong d64 = dLo | ((ulong)dHi << 32);
        ulong bitflip = BinaryPrimitives.ReadUInt64LittleEndian(s[16..]) ^ BinaryPrimitives.ReadUInt64LittleEndian(s[24..]);
        ulong keyed = d64 ^ bitflip;
        // In C: XXH_PRIME64_1 + (xxh_u64)(len+len) << 2  →  (P64_1 + 2*len) << 2  (+ binds before <<)
        ulong mul = unchecked((Xp64_1 + (ulong)(len + len)) << 2);
        var m = unchecked((UInt128)keyed * mul);
        ulong mLo = (ulong)m, mHi = (ulong)(m >> 64);
        mHi += mLo << 1;
        mLo ^= mHi >> 3;
        mLo ^= mLo >> 35;
        mLo *= 0x9FB21C651E98DF25UL;
        mLo ^= mLo >> 28;
        return (mLo, XAvalanche(mHi));
    }

    // len 9–16
    private static (ulong Lo, ulong Hi) Xxh3Len9To16(ReadOnlySpan<byte> d, ReadOnlySpan<byte> s, int len)
    {
        ulong bfl = BinaryPrimitives.ReadUInt64LittleEndian(s[32..]) ^ BinaryPrimitives.ReadUInt64LittleEndian(s[40..]);
        ulong bfh = BinaryPrimitives.ReadUInt64LittleEndian(s[48..]) ^ BinaryPrimitives.ReadUInt64LittleEndian(s[56..]);
        ulong dLo = BinaryPrimitives.ReadUInt64LittleEndian(d);
        ulong dHi = BinaryPrimitives.ReadUInt64LittleEndian(d[(len - 8)..]);
        var m128 = (UInt128)(dLo ^ dHi ^ bfl) * Xp64_1;
        ulong mLo = (ulong)m128 + (((ulong)len - 1) << 54);
        ulong mHi = (ulong)(m128 >> 64);
        dHi ^= bfh;
        // XXH_mult32to64(lo32(dHi), hi32(dHi)-1)
        ulong mult32 = (ulong)(uint)dHi * (ulong)unchecked((uint)(dHi >> 32) - 1u);
        mHi = unchecked(mHi + dHi + mult32);
        mLo = unchecked(mLo ^ (mHi + dHi));
        ulong hLo = XAvalanche(mLo);
        // hi: mult64to128(mLo, P64_2).lo64 + mHi*P64_2 + len*P64_1
        ulong hHi = XAvalanche(unchecked((ulong)((UInt128)mLo * Xp64_2) + mHi * Xp64_2 + (ulong)len * Xp64_1));
        return (hLo, hHi);
    }

    // len 17–128
    private static (ulong Lo, ulong Hi) Xxh3Short128(ReadOnlySpan<byte> d, ReadOnlySpan<byte> s, int len)
    {
        ulong a1 = Xp64_1 * (ulong)len, a2 = 0;
        if (len > 32)
        {
            if (len > 64)
            {
                if (len > 96) { a1 += XMix16B(d, s, 48, 96); a2 += XMix16B(d, s, len - 64, 112); }
                a1 += XMix16B(d, s, 32, 64); a2 += XMix16B(d, s, len - 48, 80);
            }
            a1 += XMix16B(d, s, 16, 32); a2 += XMix16B(d, s, len - 32, 48);
        }
        a1 += XMix16B(d, s, 0, 0); a2 += XMix16B(d, s, len - 16, 16);
        ulong lo = unchecked(a1 + a2);
        ulong hi = unchecked(a1 * Xp64_1 + a2 * Xp64_4 + (ulong)len * Xp64_2 - lo);
        return (XAvalanche(lo), unchecked(0UL - XAvalanche(hi)));
    }

    // len 129–240
    private static (ulong Lo, ulong Hi) Xxh3Mid128(ReadOnlySpan<byte> d, ReadOnlySpan<byte> s, int len)
    {
        // SECRET_SIZE_MIN=136, MIDSIZE_STARTOFFSET=3, MIDSIZE_LASTOFFSET=17
        ulong a1 = Xp64_1 * (ulong)len, a2 = 0;
        int rounds = len / 32;
        // First 4 rounds use secret at same offset as data
        for (int i = 0; i < 4; i++)
        {
            a1 += XMix16B(d, s, 32 * i, 32 * i);
            a2 += XMix16B(d, s, 32 * i + 16, 32 * i + 16);
        }
        a1 = XAvalanche(a1); a2 = XAvalanche(a2);
        // Remaining rounds use secret with STARTOFFSET=3
        for (int i = 4; i < rounds; i++)
        {
            a1 += XMix16B(d, s, 32 * i, 3 + 32 * (i - 4));
            a2 += XMix16B(d, s, 32 * i + 16, 3 + 32 * (i - 4) + 16);
        }
        // Last bytes: secret at 136-17=119 and 136-17-16=103
        a1 += XMix16B(d, s, len - 16, 119);
        a2 += XMix16B(d, s, len - 32, 103);
        ulong lo = unchecked(a1 + a2);
        ulong hi = unchecked(a1 * Xp64_1 + a2 * Xp64_4 + (ulong)len * Xp64_2 - lo);
        return (XAvalanche(lo), unchecked(0UL - XAvalanche(hi)));
    }

    // len > 240 — full stripe accumulation
    private static (ulong Lo, ulong Hi) Xxh3Long128(ReadOnlySpan<byte> d, ReadOnlySpan<byte> s)
    {
        // nbStripesPerBlock = (192-64)/8 = 16;  blockLen = 64*16 = 1024
        const int STRIPE = 64, RATE = 8, BLOCK = 1024, SPB = 16;
        // secret[192-64..] = secret[128..] for scramble
        // secret[192-64-7..] = secret[121..] for last stripe
        // mergeAccs: lo at secret[11..], hi at secret[192-64-11..]=secret[117..]
        const int SCRAMBLE_OFF = 128;
        const int LAST_ACC_OFF = 121; // 192 - 64 - 7
        const int MERGE_LO_OFF = 11;
        const int MERGE_HI_OFF = 117; // 192 - 64 - 11

        ulong[] acc = [Xp32_3, Xp64_1, Xp64_2, Xp64_3, Xp64_4, Xp32_2, Xp64_5, Xp32_1];

        int len = d.Length;
        int nbBlocks = (len - 1) / BLOCK;

        for (int b = 0; b < nbBlocks; b++)
        {
            var block = d[(b * BLOCK)..];
            for (int st = 0; st < SPB; st++)
                XAccumulate512(acc, block[(st * STRIPE)..], s[(st * RATE)..]);
            XScrambleAcc(acc, s[SCRAMBLE_OFF..]);
        }

        // Last partial block
        int lastStart = nbBlocks * BLOCK;
        int lastStripes = (len - 1 - lastStart) / STRIPE;
        for (int st = 0; st < lastStripes; st++)
            XAccumulate512(acc, d[(lastStart + st * STRIPE)..], s[(st * RATE)..]);

        // Last 64 bytes of input (always processed last)
        XAccumulate512(acc, d[(len - STRIPE)..], s[LAST_ACC_OFF..]);

        ulong lo = XMergeAccs(acc, s, MERGE_LO_OFF, (ulong)len * Xp64_1);
        ulong hi = XMergeAccs(acc, s, MERGE_HI_OFF, unchecked(~((ulong)len * Xp64_2)));
        return (lo, hi);
    }

    // ── 5-chunk probabilistic sampled fingerprint ─────────────────────────────
    //
    // For any file size: 5 × 2 MiB chunks, 64 KiB-aligned, sorted, overlap-resolved.
    //   Chunk 0  : offset 0
    //   Chunk 4  : offset AlignFloor(fileSize − 2 MiB)
    //   Chunks 1–3 : one per interior third of [2 MiB, fileSize−2 MiB], offset
    //               drawn from SplitMix64 seeded by fileSize ⊕ golden-ratio constant.
    //
    // Signature bytes fed to XXH3-128:
    //   [ fileSize : uint64-LE ]
    //   for each merged region (sorted, non-overlapping):
    //     [ offset : uint64-LE ][ len : uint32-LE ][ data : bytes ]
    //
    private static async Task<string?> ComputeSmartHashAsync(
        string physicalPath, long fileSize, CancellationToken cancellationToken)
    {
        try
        {
            const long C = SampleChunkSize;   // 2 MiB
            const long A = SampleAlignBytes;  // 64 KiB

            static long AlignFloor(long v) => v & ~(A - 1);

            // ── 1. Compute 5 chunk offsets ────────────────────────────────────
            ulong smState = (ulong)fileSize ^ SampleSeedGolden;

            long intStart = C;
            long intEnd = fileSize - C;   // may be <= intStart for small files

            var offsets = new long[5];
            offsets[0] = 0L;
            offsets[4] = AlignFloor(Math.Max(0L, fileSize - C));

            for (int i = 0; i < 3; i++)
            {
                long off;
                if (intEnd > intStart)
                {
                    long intLen = intEnd - intStart;
                    long thirdLen = intLen / 3;
                    long tStart = intStart + i * thirdLen;
                    long tEnd = (i == 2) ? intEnd : tStart + thirdLen;
                    long range = Math.Max(1L, tEnd - tStart);
                    ulong r = SplitMix64(ref smState);
                    long candidate = tStart + (long)(r % (ulong)range);
                    off = AlignFloor(Math.Clamp(candidate, 0L, Math.Max(0L, fileSize - C)));
                }
                else
                {
                    off = 0L; // too small for interior; merges away via overlap resolution
                }
                offsets[1 + i] = off;
            }

            // ── 2. Sort for sequential I/O ────────────────────────────────────
            Array.Sort(offsets);

            // ── 3. Overlap resolution → merged regions ────────────────────────
            var regions = new List<(long Off, long Len)>(5);
            foreach (long off in offsets)
            {
                long rLen = Math.Min(C, fileSize - off);
                if (rLen <= 0) continue;
                if (regions.Count > 0)
                {
                    var (pOff, pLen) = regions[^1];
                    if (off < pOff + pLen)
                    {
                        regions[^1] = (pOff, Math.Max(pOff + pLen, off + rLen) - pOff);
                        continue;
                    }
                }
                regions.Add((off, rLen));
            }

            // ── 4. Build signature buffer (metadata + sampled bytes) ──────────
            long totalBytes = 0;
            foreach (var (_, rLen) in regions) totalBytes += rLen;
            // 8 bytes for fileSize + 12 bytes per region header + data
            var sig = new byte[8 + regions.Count * 12 + (int)totalBytes];
            int pos = 0;

            BinaryPrimitives.WriteUInt64LittleEndian(sig, (ulong)fileSize);
            pos += 8;

            const int IoBuf = 65_536; // 64 KiB I/O buffer
            var ioBuf = new byte[IoBuf];

            await using var stream = new FileStream(
                physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read, IoBuf, useAsync: true);

            foreach (var (off, rLen) in regions)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(sig.AsSpan(pos), (ulong)off);
                BinaryPrimitives.WriteUInt32LittleEndian(sig.AsSpan(pos + 8), (uint)rLen);
                pos += 12;

                stream.Seek(off, SeekOrigin.Begin);
                long remaining = rLen;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(remaining, IoBuf);
                    int read = await stream.ReadAsync(ioBuf.AsMemory(0, toRead), cancellationToken);
                    if (read == 0) break;
                    ioBuf.AsSpan(0, read).CopyTo(sig.AsSpan(pos));
                    pos += read;
                    remaining -= read;
                }
            }

            // ── 5. Hash the full signature with XXH3-128 ─────────────────────
            var (lo, hi) = Xxh3Hash128(sig.AsSpan(0, pos));
            return $"{lo:x16}{hi:x16}";
        }
        catch
        {
            return null;
        }
    }
}
