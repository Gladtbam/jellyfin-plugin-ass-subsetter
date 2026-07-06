using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Jellyfin.Plugin.AssSubsetter.Core;

/// <summary>
/// Pure C# encoder for PGS/SUP (Blu-ray Presentation Graphic Stream) format.
/// Handles palette quantization, RLE encoding, and segment assembly.
/// </summary>
internal static class PgsEncoder
{
    // PGS segment types
    private const byte SegmentTypePcs = 0x16; // Presentation Composition Segment
    private const byte SegmentTypeWds = 0x17; // Window Definition Segment
    private const byte SegmentTypePds = 0x14; // Palette Definition Segment
    private const byte SegmentTypeOds = 0x15; // Object Definition Segment
    private const byte SegmentTypeEnd = 0x80; // End of Display Set

    // PGS magic number "PG"
    private const ushort PgsMagic = 0x5047;

    // Maximum ODS data payload (excluding header) per segment
    private const int MaxOdsPayload = 65515; // 65535 - 4 (obj header) - 7 (first fragment header) - ... simplified

    /// <summary>
    /// Writes a complete Display Set to show a subtitle bitmap.
    /// </summary>
    /// <param name="stream">The output stream.</param>
    /// <param name="pts90">Presentation timestamp in 90kHz ticks.</param>
    /// <param name="videoWidth">Full video frame width.</param>
    /// <param name="videoHeight">Full video frame height.</param>
    /// <param name="indexedBitmap">Palette-indexed bitmap data (1 byte per pixel).</param>
    /// <param name="palette">RGBA palette entries (index 0 = transparent typically).</param>
    /// <param name="objectX">Object X position on screen.</param>
    /// <param name="objectY">Object Y position on screen.</param>
    /// <param name="objectWidth">Object bitmap width.</param>
    /// <param name="objectHeight">Object bitmap height.</param>
    /// <param name="compositionNumber">Sequence number for this composition.</param>
    internal static void WriteDisplaySet(
        Stream stream,
        long pts90,
        int videoWidth,
        int videoHeight,
        byte[] indexedBitmap,
        uint[] palette,
        int objectX,
        int objectY,
        int objectWidth,
        int objectHeight,
        ushort compositionNumber)
    {
        // RLE encode the bitmap
        byte[] rleData = RleEncode(indexedBitmap, objectWidth, objectHeight);

        // PCS - Presentation Composition Segment
        WritePcs(
            stream,
            pts90,
            videoWidth,
            videoHeight,
            compositionNumber,
            objectX,
            objectY,
            objectWidth,
            objectHeight,
            isEpochStart: true);

        // WDS - Window Definition Segment
        WriteWds(stream, pts90, objectX, objectY, objectWidth, objectHeight);

        // PDS - Palette Definition Segment
        WritePds(stream, pts90, palette);

        // ODS - Object Definition Segment(s)
        WriteOds(stream, pts90, objectWidth, objectHeight, rleData);

        // END
        WriteEnd(stream, pts90);
    }

    /// <summary>
    /// Writes a Display Set to clear/hide the current subtitle.
    /// </summary>
    /// <param name="stream">The output stream.</param>
    /// <param name="pts90">Presentation timestamp in 90kHz ticks.</param>
    /// <param name="videoWidth">Full video frame width.</param>
    /// <param name="videoHeight">Full video frame height.</param>
    /// <param name="compositionNumber">Sequence number for this composition.</param>
    internal static void WriteClearSet(
        Stream stream,
        long pts90,
        int videoWidth,
        int videoHeight,
        ushort compositionNumber)
    {
        // PCS with no objects (composition_object_count = 0)
        WritePcsClear(stream, pts90, videoWidth, videoHeight, compositionNumber);

        // WDS with minimal window
        WriteWds(stream, pts90, 0, 0, 1, 1);

        // END
        WriteEnd(stream, pts90);
    }

    /// <summary>
    /// Quantizes an RGBA bitmap to a palette of at most 256 colors.
    /// Returns the indexed bitmap and the palette.
    /// </summary>
    /// <param name="rgbaData">RGBA pixel data (4 bytes per pixel).</param>
    /// <param name="width">Image width.</param>
    /// <param name="height">Image height.</param>
    /// <param name="indexedBitmap">Output: palette-indexed bitmap.</param>
    /// <param name="palette">Output: RGBA palette entries.</param>
    internal static void QuantizeToPalette(
        byte[] rgbaData,
        int width,
        int height,
        out byte[] indexedBitmap,
        out uint[] palette)
    {
        // Simple quantization: map unique colors to palette indices
        // Index 0 is reserved for fully transparent
        var colorToIndex = new Dictionary<uint, byte>();
        indexedBitmap = new byte[width * height];

        // Reserve index 0 for fully transparent
        colorToIndex[0x00000000] = 0;
        int nextIndex = 1;

        for (int i = 0; i < width * height; i++)
        {
            int offset = i * 4;
            byte r = rgbaData[offset];
            byte g = rgbaData[offset + 1];
            byte b = rgbaData[offset + 2];
            byte a = rgbaData[offset + 3];

            if (a == 0)
            {
                indexedBitmap[i] = 0; // fully transparent → index 0
                continue;
            }

            uint color = ((uint)r << 24) | ((uint)g << 16) | ((uint)b << 8) | a;

            if (!colorToIndex.TryGetValue(color, out byte idx))
            {
                if (nextIndex >= 256)
                {
                    // Exceeded 256 colors - find closest existing color
                    idx = FindClosestColor(color, colorToIndex);
                }
                else
                {
                    idx = (byte)nextIndex++;
                    colorToIndex[color] = idx;
                }
            }

            indexedBitmap[i] = idx;
        }

        // Build palette array
        palette = new uint[nextIndex];
        foreach (var kvp in colorToIndex)
        {
            if (kvp.Value < palette.Length)
            {
                palette[kvp.Value] = kvp.Key;
            }
        }
    }

    /// <summary>
    /// PGS RLE encoding for indexed bitmap data.
    /// Format: sequences of (color, run_length) with special encoding for long runs and line ends.
    /// </summary>
    /// <param name="indexedBitmap">Palette-indexed bitmap data (1 byte per pixel).</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <returns>RLE-encoded byte array representing the bitmap.</returns>
    internal static byte[] RleEncode(byte[] indexedBitmap, int width, int height)
    {
        using var ms = new MemoryStream();

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * width;
            int x = 0;

            while (x < width)
            {
                byte color = indexedBitmap[rowStart + x];
                int runLength = 1;

                // Count consecutive pixels of same color
                while (x + runLength < width && indexedBitmap[rowStart + x + runLength] == color && runLength < 16383)
                {
                    runLength++;
                }

                if (color == 0)
                {
                    // Transparent pixels use special encoding
                    if (runLength < 64)
                    {
                        // 00 LLLLLL (6-bit length)
                        ms.WriteByte(0x00);
                        ms.WriteByte((byte)runLength);
                    }
                    else
                    {
                        // 00 01LLLLLL LLLLLLLL (14-bit length)
                        ms.WriteByte(0x00);
                        ms.WriteByte((byte)(0x40 | (runLength >> 8)));
                        ms.WriteByte((byte)(runLength & 0xFF));
                    }
                }
                else
                {
                    // Non-transparent pixels
                    if (runLength < 64)
                    {
                        // 00 1LLLLLLL CC (7-bit length + color)
                        ms.WriteByte(0x00);
                        ms.WriteByte((byte)(0x80 | runLength));
                        ms.WriteByte(color);
                    }
                    else
                    {
                        // 00 11LLLLLL LLLLLLLL CC (14-bit length + color)
                        ms.WriteByte(0x00);
                        ms.WriteByte((byte)(0xC0 | (runLength >> 8)));
                        ms.WriteByte((byte)(runLength & 0xFF));
                        ms.WriteByte(color);
                    }
                }

                x += runLength;
            }

            // End of line marker: 0x00 0x00
            ms.WriteByte(0x00);
            ms.WriteByte(0x00);
        }

        return ms.ToArray();
    }

    // --- Private segment writers ---

    private static void WriteSegmentHeader(Stream stream, long pts90, byte segmentType, int dataSize)
    {
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt16BigEndian(header, PgsMagic);
        BinaryPrimitives.WriteUInt32BigEndian(header.Slice(2), (uint)(pts90 & 0xFFFFFFFF));
        BinaryPrimitives.WriteUInt32BigEndian(header.Slice(6), 0); // DTS = 0
        header[10] = segmentType;
        BinaryPrimitives.WriteUInt16BigEndian(header.Slice(11), (ushort)dataSize);
        stream.Write(header);
    }

    private static void WritePcs(
        Stream stream,
        long pts90,
        int videoWidth,
        int videoHeight,
        ushort compositionNumber,
        int objX,
        int objY,
        int objW,
        int objH,
        bool isEpochStart)
    {
        // PCS data: 11 bytes base + 8 bytes per composition object
        const int pcsBaseSize = 11;
        const int objectEntrySize = 8;
        int totalSize = pcsBaseSize + objectEntrySize;

        WriteSegmentHeader(stream, pts90, SegmentTypePcs, totalSize);

        Span<byte> data = stackalloc byte[totalSize];

        // Video dimensions
        BinaryPrimitives.WriteUInt16BigEndian(data, (ushort)videoWidth);
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(2), (ushort)videoHeight);

        // Frame rate (always 0x10 for 24fps in BD spec, but often ignored)
        data[4] = 0x10;

        // Composition number
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(5), compositionNumber);

        // Composition state: 0x80 = epoch start, 0x00 = normal
        data[7] = isEpochStart ? (byte)0x80 : (byte)0x00;

        // Palette update flag: 0
        data[8] = 0x00;

        // Palette ID
        data[9] = 0x00;

        // Number of composition objects
        data[10] = 0x01;

        // Composition object entry
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(11), 0); // Object ID = 0
        data[13] = 0x00; // Window ID = 0
        data[14] = 0x00; // Cropped flag = 0 (no cropping)
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(15), (ushort)objX); // X position
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(17), (ushort)objY); // Y position

        stream.Write(data);
    }

    private static void WritePcsClear(Stream stream, long pts90, int videoWidth, int videoHeight, ushort compositionNumber)
    {
        const int pcsBaseSize = 11;

        WriteSegmentHeader(stream, pts90, SegmentTypePcs, pcsBaseSize);

        Span<byte> data = stackalloc byte[pcsBaseSize];
        BinaryPrimitives.WriteUInt16BigEndian(data, (ushort)videoWidth);
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(2), (ushort)videoHeight);
        data[4] = 0x10;
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(5), compositionNumber);
        data[7] = 0x00; // Normal update
        data[8] = 0x00;
        data[9] = 0x00;
        data[10] = 0x00; // No objects = clear

        stream.Write(data);
    }

    private static void WriteWds(Stream stream, long pts90, int x, int y, int w, int h)
    {
        const int wdsSize = 10;
        WriteSegmentHeader(stream, pts90, SegmentTypeWds, wdsSize);

        Span<byte> data = stackalloc byte[wdsSize];
        data[0] = 0x01; // Number of windows = 1
        data[1] = 0x00; // Window ID = 0
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(2), (ushort)x);
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(4), (ushort)y);
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(6), (ushort)w);
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(8), (ushort)h);

        stream.Write(data);
    }

    private static void WritePds(Stream stream, long pts90, uint[] palette)
    {
        // PDS: 2 bytes header + 5 bytes per entry (id, Y, Cb, Cr, A)
        int entryCount = Math.Min(palette.Length, 256);
        int pdsSize = 2 + (entryCount * 5);

        WriteSegmentHeader(stream, pts90, SegmentTypePds, pdsSize);

        Span<byte> header = stackalloc byte[2];
        header[0] = 0x00; // Palette ID
        header[1] = 0x00; // Palette version
        stream.Write(header);

        Span<byte> entry = stackalloc byte[5];
        for (int i = 0; i < entryCount; i++)
        {
            uint rgba = palette[i];
            byte r = (byte)(rgba >> 24);
            byte g = (byte)(rgba >> 16);
            byte b = (byte)(rgba >> 8);
            byte a = (byte)(rgba & 0xFF);

            // Convert RGB to YCbCr (BT.709)
            RgbToYcbcr(r, g, b, out byte y, out byte cb, out byte cr);

            entry[0] = (byte)i;  // Palette entry ID
            entry[1] = y;        // Y (luminance)
            entry[2] = cr;       // Cr
            entry[3] = cb;       // Cb
            entry[4] = a;        // Alpha (0 = transparent, 255 = opaque)

            stream.Write(entry);
        }
    }

    private static void WriteOds(Stream stream, long pts90, int width, int height, byte[] rleData)
    {
        // ODS header: object_id (2) + version (1) + sequence_flag (1) + data_length (3) + width (2) + height (2) = 11
        int totalDataLength = rleData.Length + 4; // +4 for width(2) + height(2)

        if (totalDataLength + 7 <= 65535) // Fits in single ODS
        {
            int odsSize = 7 + totalDataLength; // 4 (obj header) + 3 (data_length) + data
            WriteSegmentHeader(stream, pts90, SegmentTypeOds, odsSize);

            Span<byte> odsHeader = stackalloc byte[11];
            BinaryPrimitives.WriteUInt16BigEndian(odsHeader, 0); // Object ID = 0
            odsHeader[2] = 0x00; // Version
            odsHeader[3] = 0xC0; // Sequence flag: 0xC0 = first and last (single fragment)

            // Data length (24-bit big-endian)
            odsHeader[4] = (byte)(totalDataLength >> 16);
            odsHeader[5] = (byte)(totalDataLength >> 8);
            odsHeader[6] = (byte)totalDataLength;

            // Object dimensions
            BinaryPrimitives.WriteUInt16BigEndian(odsHeader.Slice(7), (ushort)width);
            BinaryPrimitives.WriteUInt16BigEndian(odsHeader.Slice(9), (ushort)height);

            stream.Write(odsHeader);
            stream.Write(rleData);
        }
        else
        {
            // Multi-fragment ODS - split into multiple segments
            WriteOdsMultiFragment(stream, pts90, width, height, rleData, totalDataLength);
        }
    }

    private static void WriteOdsMultiFragment(Stream stream, long pts90, int width, int height, byte[] rleData, int totalDataLength)
    {
        int offset = 0;
        bool isFirst = true;

        // Allocate outside the loop to avoid CA2014 (stackalloc in loop)
        Span<byte> firstFragHeader = stackalloc byte[11];
        Span<byte> contFragHeader = stackalloc byte[4];

        while (offset < rleData.Length)
        {
            bool isLast = false;
            int available;

            if (isFirst)
            {
                // First fragment includes width/height (4 bytes) + data_length (3 bytes)
                available = MaxOdsPayload - 7; // Reserve for obj_header(4) + data_length(3)
                int chunkSize = Math.Min(available - 4, rleData.Length - offset); // -4 for width+height

                int fragDataSize = 7 + 4 + chunkSize; // obj_header + data_length + width + height + data
                isLast = offset + chunkSize >= rleData.Length;

                WriteSegmentHeader(stream, pts90, SegmentTypeOds, fragDataSize);

                BinaryPrimitives.WriteUInt16BigEndian(firstFragHeader, 0);
                firstFragHeader[2] = 0x00;
                firstFragHeader[3] = isLast ? (byte)0xC0 : (byte)0x80; // first+last or first only

                firstFragHeader[4] = (byte)(totalDataLength >> 16);
                firstFragHeader[5] = (byte)(totalDataLength >> 8);
                firstFragHeader[6] = (byte)totalDataLength;

                BinaryPrimitives.WriteUInt16BigEndian(firstFragHeader.Slice(7), (ushort)width);
                BinaryPrimitives.WriteUInt16BigEndian(firstFragHeader.Slice(9), (ushort)height);

                stream.Write(firstFragHeader);
                stream.Write(rleData, offset, chunkSize);
                offset += chunkSize;
                isFirst = false;
            }
            else
            {
                // Continuation fragments: obj_header(4) only, no data_length/dimensions
                available = MaxOdsPayload - 4;
                int chunkSize = Math.Min(available, rleData.Length - offset);
                isLast = offset + chunkSize >= rleData.Length;

                int fragDataSize = 4 + chunkSize;

                WriteSegmentHeader(stream, pts90, SegmentTypeOds, fragDataSize);

                BinaryPrimitives.WriteUInt16BigEndian(contFragHeader, 0);
                contFragHeader[2] = 0x00;
                contFragHeader[3] = isLast ? (byte)0x40 : (byte)0x00; // last or middle

                stream.Write(contFragHeader);
                stream.Write(rleData, offset, chunkSize);
                offset += chunkSize;
            }
        }
    }

    private static void WriteEnd(Stream stream, long pts90)
    {
        WriteSegmentHeader(stream, pts90, SegmentTypeEnd, 0);
    }

    // --- Color helpers ---

    private static void RgbToYcbcr(byte r, byte g, byte b, out byte y, out byte cb, out byte cr)
    {
        // BT.709 conversion (limited range)
        int yVal = (int)(16 + (65.481 * r / 255.0) + (128.553 * g / 255.0) + (24.966 * b / 255.0));
        int cbVal = (int)(128 + (-37.797 * r / 255.0) + (-74.203 * g / 255.0) + (112.0 * b / 255.0));
        int crVal = (int)(128 + (112.0 * r / 255.0) + (-93.786 * g / 255.0) + (-18.214 * b / 255.0));

        y = (byte)Math.Clamp(yVal, 16, 235);
        cb = (byte)Math.Clamp(cbVal, 16, 240);
        cr = (byte)Math.Clamp(crVal, 16, 240);
    }

    private static byte FindClosestColor(uint targetColor, Dictionary<uint, byte> colorMap)
    {
        byte targetR = (byte)(targetColor >> 24);
        byte targetG = (byte)(targetColor >> 16);
        byte targetB = (byte)(targetColor >> 8);
        byte targetA = (byte)(targetColor & 0xFF);

        byte closestIdx = 0;
        int minDistance = int.MaxValue;

        foreach (var kvp in colorMap)
        {
            uint c = kvp.Key;
            int dr = (byte)(c >> 24) - targetR;
            int dg = (byte)(c >> 16) - targetG;
            int db = (byte)(c >> 8) - targetB;
            int da = (byte)(c & 0xFF) - targetA;
            int distance = (dr * dr) + (dg * dg) + (db * db) + (da * da);

            if (distance < minDistance)
            {
                minDistance = distance;
                closestIdx = kvp.Value;
            }
        }

        return closestIdx;
    }
}
