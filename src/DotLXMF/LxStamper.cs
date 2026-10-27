using System.Numerics;
using System.Security.Cryptography;
using DotReticulum.Core;
using DotReticulum.Crypto;

namespace DotLXMF;

/// <summary>LXMF proof-of-work stamps (upstream <c>LXStamper.py</c>): a 32-byte value whose hash with an HKDF-expanded workblock has <c>cost</c> leading zero bits.</summary>
public static class LxStamper
{
    public const int StampSize = 32;
    public const int WorkblockExpandRounds = 3000;
    public const int WorkblockExpandRoundsPn = 1000;

    public static byte[] Workblock(ReadOnlySpan<byte> material, int expandRounds = WorkblockExpandRounds)
    {
        var block = new byte[expandRounds * 256];
        for (var n = 0; n < expandRounds; n++)
        {
            var packedN = MsgPack.Pack((long)n);
            var input = new byte[material.Length + packedN.Length];
            material.CopyTo(input);
            packedN.CopyTo(input, material.Length);
            var salt = SHA256.HashData(input);
            KeyDerivation.HkdfSha256(material, 256, salt).CopyTo(block, n * 256);
        }

        return block;
    }

    public static bool IsValid(ReadOnlySpan<byte> stamp, int targetCost, ReadOnlySpan<byte> workblock)
    {
        var digest = Hash(workblock, stamp);
        return LeadingZeroBits(digest) >= targetCost;
    }

    /// <summary>Number of leading zero bits (upstream <c>stamp_value</c>).</summary>
    public static int Value(ReadOnlySpan<byte> workblock, ReadOnlySpan<byte> stamp) => LeadingZeroBits(Hash(workblock, stamp));

    /// <summary>Validates a message stamp against the message id (hash).</summary>
    public static bool Validate(ReadOnlySpan<byte> messageId, ReadOnlySpan<byte> stamp, int targetCost) =>
        stamp.Length == StampSize && IsValid(stamp, targetCost, Workblock(messageId));

    /// <summary>Searches for a stamp of at least <paramref name="cost"/> bits for <paramref name="messageId"/> using all cores.</summary>
    public static byte[] Generate(byte[] messageId, int cost, CancellationToken cancellationToken = default)
    {
        var workblock = Workblock(messageId);
        byte[]? found = null;
        var workers = Math.Max(1, Environment.ProcessorCount);
        var tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
        {
            Span<byte> candidate = stackalloc byte[StampSize];
            var buffer = new byte[workblock.Length + StampSize];
            workblock.CopyTo(buffer, 0);
            while (Volatile.Read(ref found) is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var i = 0; i < 256; i++)
                {
                    RandomNumberGenerator.Fill(candidate);
                    candidate.CopyTo(buffer.AsSpan(workblock.Length));
                    if (LeadingZeroBits(SHA256.HashData(buffer)) >= cost)
                    {
                        Interlocked.CompareExchange(ref found, candidate.ToArray(), null);
                        return;
                    }
                }
            }
        }, CancellationToken.None)).ToArray();
        Task.WaitAll(tasks);
        return found!;
    }

    private static byte[] Hash(ReadOnlySpan<byte> workblock, ReadOnlySpan<byte> stamp)
    {
        var buffer = new byte[workblock.Length + stamp.Length];
        workblock.CopyTo(buffer);
        stamp.CopyTo(buffer.AsSpan(workblock.Length));
        return SHA256.HashData(buffer);
    }

    private static int LeadingZeroBits(ReadOnlySpan<byte> digest)
    {
        var bits = 0;
        foreach (var b in digest)
        {
            if (b == 0)
            {
                bits += 8;
                continue;
            }

            return bits + BitOperations.LeadingZeroCount((uint)b) - 24;
        }

        return bits;
    }
}
