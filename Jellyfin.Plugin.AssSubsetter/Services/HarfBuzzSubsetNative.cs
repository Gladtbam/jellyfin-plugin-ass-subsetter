#pragma warning disable SA1300, SA1600, SA1611, SA1615, SA1503, CA1854, CA1861, CA1865, CA1869, SA1516, SA1028, CA5392, SA1513, SA1649, SA1402, CS1591
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
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr hb_blob_create(IntPtr data, uint length, int memoryMode, IntPtr userData, IntPtr destroy);

    // codeql[cs/unmanaged-code] Justification: Required for HarfBuzz native interop.
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr hb_face_create(IntPtr blob, uint index);

    // codeql[cs/unmanaged-code] Justification: Required for HarfBuzz native interop.
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr hb_subset_input_create_or_fail();

    // codeql[cs/unmanaged-code] Justification: Required for HarfBuzz native interop.
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr hb_subset_input_unicode_set(IntPtr input);

    // codeql[cs/unmanaged-code] Justification: Required for HarfBuzz native interop.
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void hb_set_add(IntPtr set, uint codepoint);

    // codeql[cs/unmanaged-code] Justification: Required for HarfBuzz native interop.
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr hb_subset_or_fail(IntPtr face, IntPtr input);

    // codeql[cs/unmanaged-code] Justification: Required for HarfBuzz native interop.
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr hb_face_reference_blob(IntPtr face);

    // codeql[cs/unmanaged-code] Justification: Required for HarfBuzz native interop.
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr hb_blob_get_data(IntPtr blob, out uint length);

    // codeql[cs/unmanaged-code] Justification: Required for HarfBuzz native interop.
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void hb_subset_input_destroy(IntPtr input);

    // codeql[cs/unmanaged-code] Justification: Required for HarfBuzz native interop.
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void hb_face_destroy(IntPtr face);

    // codeql[cs/unmanaged-code] Justification: Required for HarfBuzz native interop.
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void hb_blob_destroy(IntPtr blob);

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
            var blob = hb_blob_create(dataPtr, (uint)fontData.Length, 1, IntPtr.Zero, IntPtr.Zero);
            if (blob == IntPtr.Zero)
            {
                logger.LogError("[AssSubsetter] hb_blob_create failed.");
                return null;
            }

            // codeql[cs/call-to-unmanaged-code] Justification: Native API call.

            var face = hb_face_create(blob, faceIndex);
            if (face == IntPtr.Zero)
            {
                logger.LogError("[AssSubsetter] hb_face_create failed.");
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                hb_blob_destroy(blob);
                return null;
            }

            // codeql[cs/call-to-unmanaged-code] Justification: Native API call.

            var input = hb_subset_input_create_or_fail();
            if (input == IntPtr.Zero)
            {
                logger.LogError("[AssSubsetter] hb_subset_input_create_or_fail failed.");
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                hb_face_destroy(face);
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                hb_blob_destroy(blob);
                return null;
            }

            // codeql[cs/call-to-unmanaged-code] Justification: Native API call.

            var unicodeSet = hb_subset_input_unicode_set(input);
            if (unicodeSet == IntPtr.Zero)
            {
                logger.LogError("[AssSubsetter] hb_subset_input_unicode_set failed.");
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                hb_subset_input_destroy(input);
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                hb_face_destroy(face);
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                hb_blob_destroy(blob);
                return null;
            }

            foreach (var cp in unicodeCodepoints)
            {
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                hb_set_add(unicodeSet, cp);
            }

            // codeql[cs/call-to-unmanaged-code] Justification: Native API call.

            var subsetFace = hb_subset_or_fail(face, input);
            if (subsetFace == IntPtr.Zero)
            {
                logger.LogWarning("[AssSubsetter] hb_subset_or_fail failed. The font might be unsupported or empty.");
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                hb_subset_input_destroy(input);
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                hb_face_destroy(face);
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                hb_blob_destroy(blob);
                return null;
            }

            // codeql[cs/call-to-unmanaged-code] Justification: Native API call.

            var resultBlob = hb_face_reference_blob(subsetFace);
            if (resultBlob == IntPtr.Zero)
            {
                logger.LogError("[AssSubsetter] hb_face_reference_blob failed.");
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                hb_face_destroy(subsetFace);
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                hb_subset_input_destroy(input);
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                hb_face_destroy(face);
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                hb_blob_destroy(blob);
                return null;
            }

            // codeql[cs/call-to-unmanaged-code] Justification: Native API call.

            var resultDataPtr = hb_blob_get_data(resultBlob, out uint length);
            if (resultDataPtr == IntPtr.Zero || length == 0)
            {
                logger.LogError("[AssSubsetter] hb_blob_get_data failed or returned 0 length.");
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                hb_blob_destroy(resultBlob);
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                hb_face_destroy(subsetFace);
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                hb_subset_input_destroy(input);
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                hb_face_destroy(face);
                // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
                hb_blob_destroy(blob);
                return null;
            }

            byte[] result = new byte[length];
            Marshal.Copy(resultDataPtr, result, 0, (int)length);

            // Cleanup
            // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
            hb_blob_destroy(resultBlob);
            // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
            hb_face_destroy(subsetFace);
            // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
            hb_subset_input_destroy(input);
            // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
            hb_face_destroy(face);
            // codeql[cs/call-to-unmanaged-code] Justification: Native API call.
            hb_blob_destroy(blob);

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
        // codeql[cs/catch-of-all-exceptions] Justification: Native interop crashes can yield unpredictable managed exceptions.
        catch (Exception ex)
        {
            logger.LogError(ex, "[AssSubsetter] Exception occurred during font subsetting via HarfBuzzSharp.");
            return null;
        }
        finally
        {
            pin.Free();
        }
    }
}
