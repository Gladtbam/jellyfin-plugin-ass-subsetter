using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Opens the exact font Face and checks one Unicode mapping through FreeType when it is installed.</summary>
internal static class FreeTypeFontVerifier
{
    private static readonly Lazy<Bindings?> _native = new(LoadBindings);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitDelegate(out nint library);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int NewMemoryFaceDelegate(nint library, byte* data, long size, long faceIndex, out nint face);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint GetCharIndexDelegate(nint face, nuint charCode);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DoneFaceDelegate(nint face);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DoneLibraryDelegate(nint library);

    internal static bool IsAvailable => _native.Value is not null;

    internal static unsafe FontVerificationStatus Verify(byte[] data, int faceIndex, InspectedFontFace inspected)
    {
        Bindings? native = _native.Value;
        if (native is null)
        {
            return FontVerificationStatus.Unknown;
        }

        if (data.Length == 0 || inspected.UnicodeMappings.Count == 0)
        {
            return FontVerificationStatus.Failed;
        }

        if (native.Init(out nint library) != 0 || library == nint.Zero)
        {
            return FontVerificationStatus.Failed;
        }

        try
        {
            fixed (byte* pointer = data)
            {
                if (native.NewMemoryFace(library, pointer, data.LongLength, faceIndex, out nint face) != 0 || face == nint.Zero)
                {
                    return FontVerificationStatus.Failed;
                }

                try
                {
                    foreach (KeyValuePair<uint, ushort> mapping in inspected.UnicodeMappings)
                    {
                        if (mapping.Value != 0)
                        {
                            // FT_Load_Glyph can terminate the host on some TTC fonts.
                            // Skia and HarfBuzz provide separate glyph-loading evidence.
                            return native.GetCharIndex(face, (nuint)mapping.Key) == mapping.Value
                                ? FontVerificationStatus.Passed
                                : FontVerificationStatus.Failed;
                        }
                    }

                    return FontVerificationStatus.Failed;
                }
                finally
                {
                    native.DoneFace(face);
                }
            }
        }
        finally
        {
            native.DoneLibrary(library);
        }
    }

    private static Bindings? LoadBindings()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? ["freetype.dll", "freetype6.dll", "libfreetype-6.dll"]
            : OperatingSystem.IsMacOS()
                ? ["libfreetype.dylib"]
                : ["/usr/lib/jellyfin-ffmpeg/lib/libfreetype.so.6", "libfreetype.so.6", "libfreetype.so"];
        foreach (string candidate in candidates)
        {
            if (!NativeLibrary.TryLoad(candidate, out nint handle))
            {
                continue;
            }

            if (NativeLibrary.TryGetExport(handle, "FT_Init_FreeType", out nint init)
                && NativeLibrary.TryGetExport(handle, "FT_New_Memory_Face", out nint newFace)
                && NativeLibrary.TryGetExport(handle, "FT_Get_Char_Index", out nint getCharIndex)
                && NativeLibrary.TryGetExport(handle, "FT_Done_Face", out nint doneFace)
                && NativeLibrary.TryGetExport(handle, "FT_Done_FreeType", out nint doneLibrary))
            {
                return new Bindings(
                    Marshal.GetDelegateForFunctionPointer<InitDelegate>(init),
                    Marshal.GetDelegateForFunctionPointer<NewMemoryFaceDelegate>(newFace),
                    Marshal.GetDelegateForFunctionPointer<GetCharIndexDelegate>(getCharIndex),
                    Marshal.GetDelegateForFunctionPointer<DoneFaceDelegate>(doneFace),
                    Marshal.GetDelegateForFunctionPointer<DoneLibraryDelegate>(doneLibrary));
            }

            NativeLibrary.Free(handle);
        }

        return null;
    }

    private sealed record Bindings(
        InitDelegate Init,
        NewMemoryFaceDelegate NewMemoryFace,
        GetCharIndexDelegate GetCharIndex,
        DoneFaceDelegate DoneFace,
        DoneLibraryDelegate DoneLibrary);
}
