using System;
using System.IO;
using System.Security.Cryptography;
using SimBootstrap.Contracts;

namespace SimBootstrap.Engine;

public class IntegrityVerifier : IIntegrityVerifier
{
    public IntegrityCheckResult Verify(string filePath, string? expectedSha256, long? expectedSize)
    {
        if (!File.Exists(filePath))
        {
            return new IntegrityCheckResult(false, $"File does not exist: {filePath}");
        }

        if (expectedSize.HasValue)
        {
            var actualSize = new FileInfo(filePath).Length;
            if (actualSize != expectedSize.Value)
            {
                return new IntegrityCheckResult(false, $"Size mismatch. Expected: {expectedSize.Value} bytes, Actual: {actualSize} bytes.");
            }
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hashBytes = sha256.ComputeHash(stream);
            var actualHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

            if (!actualHash.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return new IntegrityCheckResult(false, $"Hash mismatch. Expected SHA256: '{expectedSha256}', Actual: '{actualHash}'.");
            }
        }

        return new IntegrityCheckResult(true, null);
    }
}
