using System.Buffers.Binary;
using System.Security.Cryptography;
using ModelContextProtocol;
using SymphonyRecomp.Automation.Contracts;

namespace SymphonyRecomp.Mcp;

internal static class PngScreenshotValidator
{
    internal const int MaximumBytes = 8 * 1024 * 1024;

    public static byte[] DecodeAndValidate(ScreenshotDto screenshot)
    {
        byte[] png;
        try { png = Convert.FromBase64String(screenshot.Base64Data); }
        catch (FormatException) { throw new McpException("The game bridge returned invalid screenshot data."); }

        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (png.Length is < 24 or > MaximumBytes || !png.AsSpan(0, 8).SequenceEqual(signature)
            || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw new McpException("The game bridge returned an invalid or oversized PNG screenshot.");
        int width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        int height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        if (width is <= 0 or > 1024 || height is <= 0 or > 512 || width != screenshot.Width || height != screenshot.Height)
            throw new McpException("The game bridge returned invalid screenshot dimensions.");
        if (!string.Equals(screenshot.MimeType, "image/png", StringComparison.OrdinalIgnoreCase))
            throw new McpException("The game bridge returned an unsupported screenshot type.");
        string hash = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
        if (!string.Equals(hash, screenshot.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new McpException("The game bridge screenshot failed its integrity check.");
        return png;
    }
}
