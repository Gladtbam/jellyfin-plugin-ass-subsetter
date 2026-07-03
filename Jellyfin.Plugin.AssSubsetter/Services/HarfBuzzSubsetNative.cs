using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.Services;

/// <summary>
/// Native P/Invoke bindings for HarfBuzz subsetting.
/// </summary>
public static class HarfBuzzSubsetNative
{
    private const string LibName = "libHarfBuzzSharp";

    // codeql[cs/unmanaged-code] Justification: Required for HarfBuzz native interop.
    [DllImport(LibName, EntryPoint = "hb_blob_create", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr HbBlobCreate(IntPtr data, uint length, int memoryMode, IntPtr userData, IntPtr destroy);

    // codeql[cs/unmanaged-code] Justification: Required for HarfBuzz native interop.
    [DllImport(LibName, EntryPoint = "hb_face_create", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr HbFaceCreate(IntPtr blob, uint index);

    // codeql[cs/unmanaged-code] Justification: Required for HarfBuzz native interop.
    [DllImport(LibName, EntryPoint = "hb_subset_input_create_or_fail", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr HbSubsetInputCreateOrFail();

    // codeql[cs/unmanaged-code] Justification: Required for HarfBuzz native interop.
    [DllImport(LibName, EntryPoint = "hb_subset_input_unicode_set", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr HbSubsetInputUnicodeSet(IntPtr input);

    // codeql[cs/unmanaged-code] Justification: Required for HarfBuzz native interop.
    [DllImport(LibName, EntryPoint = "hb_set_add", CallingConvention = CallingConvention.Cdecl)]
    private static extern void HbSetAdd(IntPtr set, uint codepoint);

    // codeql[cs/unmanaged-code] Justification: Required for HarfBuzz native interop.
    [DllImport(LibName, EntryPoint = "hb_subset_or_fail", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr HbSubsetOrFail(IntPtr face, IntPtr input);

    // codeql[cs/unmanaged-code] Justification: Required for HarfBuzz native interop.
    [DllImport(LibName, EntryPoint = "hb_face_reference_blob", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr HbFaceReferenceBlob(IntPtr face);

    // codeql[cs/unmanaged-code] Justification: Required for HarfBuzz native interop.
    [DllImport(LibName, EntryPoint = "hb_blob_get_data", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr HbBlobGetData(IntPtr blob, out uint length);

    // codeql[cs/unmanaged-code] Justification: Required for HarfBuzz native interop.
    [DllImport(LibName, EntryPoint = "hb_subset_input_destroy", CallingConvention = CallingConvention.Cdecl)]
    private static extern void HbSubsetInputDestroy(IntPtr input);

    // codeql[cs/unmanaged-code] Justification: Required for HarfBuzz native interop.
    [DllImport(LibName, EntryPoint = "hb_face_destroy", CallingConvention = CallingConvention.Cdecl)]
    private static extern void HbFaceDestroy(IntPtr face);

    // codeql[cs/unmanaged-code] Justification: Required for HarfBuzz native interop.
    [DllImport(LibName, EntryPoint = "hb_blob_destroy", CallingConvention = CallingConvention.Cdecl)]
    private static extern void HbBlobDestroy(IntPtr blob);

    /// <summary>
    /// Subsets a font in memory keeping only the specified codepoints.
    /// </summary>
    /// <param name="fontData">The raw binary font data.</param>
    /// <param name="faceIndex">The index of the face within a TTC, or 0 for TTF/OTF.</param>
    /// <param name="unicodeCodepoints">A collection of Unicode codepoints to retain.</param>
    /// <param name="logger">The logger for warnings/errors.</param>
    /// <returns>The subsetted binary font data, or null if subsetting failed.</returns>
    // codeql[cs/call-to-unmanaged-code] Justification: Official HarfBuzzSharp NuGet package does not expose subsetting APIs for libHarfBuzzSharp.
    public static byte[]? SubsetFont(byte[] fontData, uint faceIndex, IEnumerable<uint> unicodeCodepoints, ILogger logger)
    {
        var pin = GCHandle.Alloc(fontData, GCHandleType.Pinned);
        try
        {
            var dataPtr = pin.AddrOfPinnedObject();
            // 0 = HB_MEMORY_MODE_DUPLICATE, 1 = HB_MEMORY_MODE_READONLY
            // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
            var blob = HbBlobCreate(dataPtr, (uint)fontData.Length, 1, IntPtr.Zero, IntPtr.Zero);
            if (blob == IntPtr.Zero)
            {
                logger.LogError("[AssSubsetter] hb_blob_create failed.");
                return null;
            }

            // codeql[cs/call-to-unmanaged-code] Justification: Native API call.

            var face = HbFaceCreate(blob, faceIndex);
            if (face == IntPtr.Zero)
            {
                logger.LogError("[AssSubsetter] hb_face_create failed.");
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                HbBlobDestroy(blob);
                return null;
            }

            // codeql[cs/call-to-unmanaged-code] Justification: Native API call.

            var input = HbSubsetInputCreateOrFail();
            if (input == IntPtr.Zero)
            {
                logger.LogError("[AssSubsetter] hb_subset_input_create_or_fail failed.");
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                HbFaceDestroy(face);
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                HbBlobDestroy(blob);
                return null;
            }

            // codeql[cs/call-to-unmanaged-code] Justification: Native API call.

            var unicodeSet = HbSubsetInputUnicodeSet(input);
            if (unicodeSet == IntPtr.Zero)
            {
                logger.LogError("[AssSubsetter] hb_subset_input_unicode_set failed.");
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                HbSubsetInputDestroy(input);
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                HbFaceDestroy(face);
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                HbBlobDestroy(blob);
                return null;
            }

            foreach (var cp in unicodeCodepoints)
            {
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                HbSetAdd(unicodeSet, cp);
            }

            // codeql[cs/call-to-unmanaged-code] Justification: Native API call.

            var subsetFace = HbSubsetOrFail(face, input);
            if (subsetFace == IntPtr.Zero)
            {
                logger.LogWarning("[AssSubsetter] hb_subset_or_fail failed. The font might be unsupported or empty.");
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                HbSubsetInputDestroy(input);
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                HbFaceDestroy(face);
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                HbBlobDestroy(blob);
                return null;
            }

            // codeql[cs/call-to-unmanaged-code] Justification: Native API call.

            var resultBlob = HbFaceReferenceBlob(subsetFace);
            if (resultBlob == IntPtr.Zero)
            {
                logger.LogError("[AssSubsetter] hb_face_reference_blob failed.");
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                HbFaceDestroy(subsetFace);
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                HbSubsetInputDestroy(input);
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                HbFaceDestroy(face);
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                HbBlobDestroy(blob);
                return null;
            }

            // codeql[cs/call-to-unmanaged-code] Justification: Native API call.

            var resultDataPtr = HbBlobGetData(resultBlob, out uint length);
            if (resultDataPtr == IntPtr.Zero || length == 0)
            {
                logger.LogError("[AssSubsetter] hb_blob_get_data failed or returned 0 length.");
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                HbBlobDestroy(resultBlob);
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                HbFaceDestroy(subsetFace);
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                HbSubsetInputDestroy(input);
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                HbFaceDestroy(face);
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                HbBlobDestroy(blob);
                return null;
            }

            byte[] result = new byte[length];
            Marshal.Copy(resultDataPtr, result, 0, (int)length);

            // Cleanup
            // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
            HbBlobDestroy(resultBlob);
            // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
            HbFaceDestroy(subsetFace);
            // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
            HbSubsetInputDestroy(input);
            // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
            HbFaceDestroy(face);
            // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
            HbBlobDestroy(blob);

            return result;
        }
        catch (DllNotFoundException ex)
        {
            logger.LogError(ex, "[AssSubsetter] Failed to load libHarfBuzzSharp. Ensure it is included in the Jellyfin environment.");
            return null;
        }
        catch (EntryPointNotFoundException ex)
        {
            logger.LogError(ex, "[AssSubsetter] hb_subset symbols not found in libHarfBuzzSharp. The native library might lack subsetting support.");
            return null;
        }
        catch (Exception ex)
        {
            // codeql[cs/catch-of-all-exceptions] Justification: Native interop crashes can yield unpredictable managed exceptions.
            logger.LogError(ex, "[AssSubsetter] Exception occurred during font subsetting via HarfBuzzSharp.");
            return null;
        }
        finally
        {
            pin.Free();
        }
    }
}
