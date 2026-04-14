using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

/// <summary>
/// Compact binary encoder/decoder for arrow board snapshots.
///
/// Two layers:
/// - <see cref="EncodeArrows"/> / <see cref="DecodeArrows"/> — arrow-table
///   only. No header. Used by replay snapshots (where width/seed/etc. live
///   elsewhere in <see cref="ReplayData"/>).
/// - <see cref="EncodeFull"/> / <see cref="DecodeFull"/> — self-contained
///   format with a 32-byte header (magic ATSB, version, width, height, seed,
///   etc.). Used by the co-op WebSocket transport so a single binary frame
///   can fully describe a board.
///
/// Pure C# (netstandard2.1, C# 9). No Unity dependencies. Compiled into both
/// the Unity client and the .NET server via the existing
/// <c>ArrowThing.Domain.csproj</c> wildcard include.
/// </summary>
public static class BinarySnapshot
{
    // -- Constants for the "full" format header -----------------------------
    private const uint Magic = 0x42535441; // 'ATSB' little-endian: A=0x41, T=0x54, S=0x53, B=0x42
    private const ushort FormatVersion = 1;
    private const ushort FlagGzipped = 1 << 1;
    private const int HeaderSize = 32;

    // ─── Arrow data layer (no header) ──────────────────────────────────────

    /// <summary>
    /// Encodes a list of arrows (each arrow is an ordered list of cells, head first)
    /// into a compact byte array. No header; the caller is responsible for any framing.
    ///
    /// Format:
    ///   u32 arrowCount
    ///   for each arrow:
    ///     u8  length (cells, &gt;= 2)
    ///     u16 headX
    ///     u16 headY
    ///     bit-packed direction stream: 2 bits per segment, (length-1) segments,
    ///     byte-padded to the next byte after each arrow.
    ///     (0=Up, 1=Right, 2=Down, 3=Left)
    /// </summary>
    public static byte[] EncodeArrows(IReadOnlyList<IReadOnlyList<Cell>> arrows)
    {
        if (arrows == null)
            throw new ArgumentNullException(nameof(arrows));

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        WriteArrowTable(bw, arrows);
        return ms.ToArray();
    }

    /// <summary>
    /// Decodes a byte array produced by <see cref="EncodeArrows"/> back into
    /// a list of arrows.
    /// </summary>
    public static List<List<Cell>> DecodeArrows(byte[] data)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));

        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);
        return ReadArrowTable(br);
    }

    // ─── Full layer (self-contained, with header) ──────────────────────────

    /// <summary>
    /// Encodes a board into a self-contained binary snapshot (header + arrow data).
    /// When <paramref name="gzip"/> is true, the arrow-data section is gzip-compressed.
    /// </summary>
    public static byte[] EncodeFull(Board board, int seed, int maxArrowLength, bool gzip = true)
    {
        if (board == null)
            throw new ArgumentNullException(nameof(board));

        // Build the arrow table as a byte buffer first so we know its size.
        var arrowCells = new List<IReadOnlyList<Cell>>(board.Arrows.Count);
        foreach (var a in board.Arrows)
            arrowCells.Add(a.Cells);

        byte[] arrowData = EncodeArrows(arrowCells);
        byte[] payload = gzip ? Gzip(arrowData) : arrowData;

        ushort flags = 0;
        if (gzip)
            flags |= FlagGzipped;

        using var ms = new MemoryStream(HeaderSize + payload.Length);
        using var bw = new BinaryWriter(ms);
        bw.Write(Magic);
        bw.Write(FormatVersion);
        bw.Write(flags);
        bw.Write((ushort)board.Width);
        bw.Write((ushort)board.Height);
        bw.Write(seed);
        bw.Write((ushort)maxArrowLength);
        bw.Write((ushort)0); // reserved
        bw.Write((uint)board.Arrows.Count);
        bw.Write((uint)0); // eventSeqHigh — Phase 4: always 0
        bw.Write(payload);
        return ms.ToArray();
    }

    /// <summary>
    /// Decodes a binary snapshot produced by <see cref="EncodeFull"/> into a fully
    /// populated <see cref="Board"/>.
    /// </summary>
    public static Board DecodeFull(byte[] data)
    {
        if (data == null || data.Length < HeaderSize)
            throw new InvalidDataException("Snapshot too short for header.");

        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);

        var header = ReadHeader(br);
        var board = new Board(header.Width, header.Height);

        int payloadLength = data.Length - HeaderSize;
        var rawPayload = br.ReadBytes(payloadLength);
        byte[] arrowData = (header.Flags & FlagGzipped) != 0 ? Gunzip(rawPayload) : rawPayload;

        var arrows = DecodeArrows(arrowData);
        if (arrows.Count != header.ArrowCount)
            throw new InvalidDataException(
                $"Arrow count mismatch: header={header.ArrowCount}, decoded={arrows.Count}"
            );

        foreach (var cells in arrows)
            board.AddArrow(new Arrow(cells));

        return board;
    }

    /// <summary>
    /// Reads the fixed 32-byte header without decoding the body. Useful for
    /// validating a snapshot before accepting it.
    /// </summary>
    public static (int Width, int Height, int Seed, int ArrowCount) PeekHeader(byte[] data)
    {
        if (data == null || data.Length < HeaderSize)
            throw new InvalidDataException("Snapshot too short for header.");
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);
        var h = ReadHeader(br);
        return (h.Width, h.Height, h.Seed, (int)h.ArrowCount);
    }

    // ─── Internals ─────────────────────────────────────────────────────────

    private struct Header
    {
        public ushort Flags;
        public int Width;
        public int Height;
        public int Seed;
        public int MaxArrowLength;
        public uint ArrowCount;
    }

    private static Header ReadHeader(BinaryReader br)
    {
        uint magic = br.ReadUInt32();
        if (magic != Magic)
            throw new InvalidDataException($"Bad magic: 0x{magic:X8}, expected 0x{Magic:X8}");
        ushort version = br.ReadUInt16();
        if (version != FormatVersion)
            throw new InvalidDataException(
                $"Unsupported snapshot version {version}, expected {FormatVersion}"
            );
        ushort flags = br.ReadUInt16();
        ushort width = br.ReadUInt16();
        ushort height = br.ReadUInt16();
        int seed = br.ReadInt32();
        ushort maxLen = br.ReadUInt16();
        br.ReadUInt16(); // reserved
        uint arrowCount = br.ReadUInt32();
        br.ReadUInt32(); // eventSeqHigh
        return new Header
        {
            Flags = flags,
            Width = width,
            Height = height,
            Seed = seed,
            MaxArrowLength = maxLen,
            ArrowCount = arrowCount,
        };
    }

    private static void WriteArrowTable(BinaryWriter bw, IReadOnlyList<IReadOnlyList<Cell>> arrows)
    {
        bw.Write((uint)arrows.Count);
        for (int i = 0; i < arrows.Count; i++)
        {
            var cells = arrows[i];
            int len = cells.Count;
            if (len < 2)
                throw new InvalidDataException($"Arrow {i} has {len} cells; minimum is 2.");
            if (len > 255)
                throw new InvalidDataException(
                    $"Arrow {i} has {len} cells; maximum is 255 (u8 length)."
                );

            bw.Write((byte)len);
            bw.Write((ushort)cells[0].X);
            bw.Write((ushort)cells[0].Y);

            // Bit-pack (len-1) 2-bit direction values, byte-padded.
            int segments = len - 1;
            int byteCount = (segments * 2 + 7) / 8;
            var packed = new byte[byteCount];
            for (int s = 0; s < segments; s++)
            {
                int dx = cells[s + 1].X - cells[s].X;
                int dy = cells[s + 1].Y - cells[s].Y;
                int dir = DeltaToDirection(dx, dy);
                int bitOffset = s * 2;
                packed[bitOffset / 8] |= (byte)(dir << (bitOffset % 8));
            }
            bw.Write(packed);
        }
    }

    private static List<List<Cell>> ReadArrowTable(BinaryReader br)
    {
        uint count = br.ReadUInt32();
        var result = new List<List<Cell>>((int)count);
        for (uint i = 0; i < count; i++)
        {
            int len = br.ReadByte();
            int headX = br.ReadUInt16();
            int headY = br.ReadUInt16();

            int segments = len - 1;
            int byteCount = (segments * 2 + 7) / 8;
            var packed = br.ReadBytes(byteCount);

            var cells = new List<Cell>(len) { new Cell(headX, headY) };
            int x = headX;
            int y = headY;
            for (int s = 0; s < segments; s++)
            {
                int bitOffset = s * 2;
                int dir = (packed[bitOffset / 8] >> (bitOffset % 8)) & 0x3;
                var (dx, dy) = DirectionDelta(dir);
                x += dx;
                y += dy;
                cells.Add(new Cell(x, y));
            }
            result.Add(cells);
        }
        return result;
    }

    /// <summary>
    /// Maps a (dx, dy) delta to a Direction enum value (0..3).
    /// Mirrors <see cref="Arrow.GetDirectionStep"/> in the inverse direction.
    /// </summary>
    private static int DeltaToDirection(int dx, int dy)
    {
        if (dx == 0 && dy == 1)
            return (int)Arrow.Direction.Up;
        if (dx == 1 && dy == 0)
            return (int)Arrow.Direction.Right;
        if (dx == 0 && dy == -1)
            return (int)Arrow.Direction.Down;
        if (dx == -1 && dy == 0)
            return (int)Arrow.Direction.Left;
        throw new InvalidDataException($"Non-orthogonal step ({dx}, {dy}) in arrow path.");
    }

    private static (int dx, int dy) DirectionDelta(int dir)
    {
        switch (dir)
        {
            case (int)Arrow.Direction.Up:
                return (0, 1);
            case (int)Arrow.Direction.Right:
                return (1, 0);
            case (int)Arrow.Direction.Down:
                return (0, -1);
            case (int)Arrow.Direction.Left:
                return (-1, 0);
            default:
                throw new InvalidDataException($"Bad direction code {dir}.");
        }
    }

    private static byte[] Gzip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            gz.Write(data, 0, data.Length);
        }
        return ms.ToArray();
    }

    private static byte[] Gunzip(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gz.CopyTo(output);
        return output.ToArray();
    }
}
