using System.IO.Compression;
using System.Text;

namespace ArrowThing.Server;

/// <summary>
/// Gzip compress/decompress helpers for the replay-storage path. Used by EF
/// Core's value converter on <c>Score.ReplayJson</c> and by the lazy
/// backfill of legacy text-column rows.
/// </summary>
public static class GzipUtil
{
    public static byte[] Compress(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<byte>();
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            gz.Write(bytes, 0, bytes.Length);
        }
        return output.ToArray();
    }

    public static string Decompress(byte[] data)
    {
        if (data == null || data.Length == 0)
            return "";
        using var input = new MemoryStream(data);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gz.CopyTo(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
