using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Opens the exact font Face and checks one Unicode mapping through FreeType when it is installed.</summary>
internal static class FreeTypeFontVerifier
{
    private static readonly Lazy<Bindings?> Native = new(LoadBindings);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitDelegate(out IntPtr library);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int NewMemoryFaceDelegate(IntPtr library, byte* data, long size, long faceIndex, out IntPtr face);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint GetCharIndexDelegate(IntPtr face, nuint charCode);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DoneFaceDelegate(IntPtr face);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DoneLibraryDelegate(IntPtr library);

    internal static bool IsAvailable => Native.Value is not null;

    internal static unsafe FontVerificationStatus Verify(byte[] data, int faceIndex, InspectedFontFace inspected)
    {
        Bindings? native = Native.Value;
        if (native is null)
        {
            return FontVerificationStatus.Unknown;
        }

        if (data.Length == 0 || inspected.UnicodeMappings.Count == 0)
        {
            return FontVerificationStatus.Failed;
        }

        if (native.Init(out IntPtr library) != 0 || library == IntPtr.Zero)
        {
            return FontVerificationStatus.Failed;
        }

        try
        {
            fixed (byte* pointer = data)
            {
                if (native.NewMemoryFace(library, pointer, data.LongLength, faceIndex, out IntPtr face) != 0 || face == IntPtr.Zero)
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
            if (!NativeLibrary.TryLoad(candidate, out IntPtr handle))
            {
                continue;
            }

            if (NativeLibrary.TryGetExport(handle, "FT_Init_FreeType", out IntPtr init)
                && NativeLibrary.TryGetExport(handle, "FT_New_Memory_Face", out IntPtr newFace)
                && NativeLibrary.TryGetExport(handle, "FT_Get_Char_Index", out IntPtr getCharIndex)
                && NativeLibrary.TryGetExport(handle, "FT_Done_Face", out IntPtr doneFace)
                && NativeLibrary.TryGetExport(handle, "FT_Done_FreeType", out IntPtr doneLibrary))
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
