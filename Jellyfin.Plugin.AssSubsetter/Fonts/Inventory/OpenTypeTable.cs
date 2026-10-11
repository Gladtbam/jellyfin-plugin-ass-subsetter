using System;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

/// <summary>
/// A bounds-checked OpenType table view into its source container.
/// </summary>
/// <param name="Tag">The four-byte table tag.</param>
/// <param name="Checksum">The directory checksum value.</param>
/// <param name="Offset">The absolute byte offset in the source container.</param>
/// <param name="Length">The table length in bytes.</param>
/// <param name="Data">The validated table bytes.</param>
public sealed record OpenTypeTable(uint Tag, uint Checksum, int Offset, int Length, ReadOnlyMemory<byte> Data);
