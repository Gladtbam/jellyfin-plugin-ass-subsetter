using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.Native;

/// <summary>
///     P/Invoke bindings for libass (the ASS/SSA subtitle rendering library).
///     Automatically resolves the libass shared library from jellyfin-ffmpeg's directory.
/// </summary>
internal static partial class LibassNative
{
    private const string LibassLibrary = "libass";
    private static readonly object _resolverLock = new();
    private static bool _resolverRegistered;

    /// <summary>
    ///     Log callback delegate for libass messages.
    /// </summary>
    /// <param name="level">Log level (0=FATAL .. 7=DEBUG).</param>
    /// <param name="fmt">Format string (printf-style).</param>
    /// <param name="va">va_list pointer (not easily consumed in managed code).</param>
    /// <param name="userData">User data pointer.</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void AssLogCallback(int level, IntPtr fmt, IntPtr va, IntPtr userData);

    /// <summary>
    ///     Initializes the libass library.
    /// </summary>
    /// <returns>ASS_Library handle, or <see cref="IntPtr.Zero" /> on failure.</returns>
    [LibraryImport(LibassLibrary, EntryPoint = "ass_library_init")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial IntPtr AssLibraryInit();

    /// <summary>
    ///     Destroys a libass library instance.
    /// </summary>
    /// <param name="library">ASS_Library handle to destroy.</param>
    [LibraryImport(LibassLibrary, EntryPoint = "ass_library_done")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void AssLibraryDone(IntPtr library);

    /// <summary>
    ///     Initializes a renderer for the given library.
    /// </summary>
    /// <param name="library">ASS_Library handle.</param>
    /// <returns>ASS_Renderer handle, or <see cref="IntPtr.Zero" /> on failure.</returns>
    [LibraryImport(LibassLibrary, EntryPoint = "ass_renderer_init")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial IntPtr AssRendererInit(IntPtr library);

    /// <summary>
    ///     Destroys a renderer instance.
    /// </summary>
    /// <param name="renderer">ASS_Renderer handle to destroy.</param>
    [LibraryImport(LibassLibrary, EntryPoint = "ass_renderer_done")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void AssRendererDone(IntPtr renderer);

    /// <summary>
    ///     Sets the frame (canvas) size for the renderer.
    /// </summary>
    /// <param name="renderer">ASS_Renderer handle.</param>
    /// <param name="w">Frame width in pixels.</param>
    /// <param name="h">Frame height in pixels.</param>
    [LibraryImport(LibassLibrary, EntryPoint = "ass_set_frame_size")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void AssSetFrameSize(IntPtr renderer, int w, int h);

    /// <summary>
    ///     Sets the storage (source) video size for the renderer.
    /// </summary>
    /// <param name="renderer">ASS_Renderer handle.</param>
    /// <param name="w">Storage width in pixels.</param>
    /// <param name="h">Storage height in pixels.</param>
    [LibraryImport(LibassLibrary, EntryPoint = "ass_set_storage_size")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void AssSetStorageSize(IntPtr renderer, int w, int h);

    /// <summary>
    ///     Sets font settings for the renderer.
    /// </summary>
    /// <param name="renderer">ASS_Renderer handle.</param>
    /// <param name="defaultFont">Default font path, or null.</param>
    /// <param name="defaultFamily">Default font family name, or null.</param>
    /// <param name="dfp">Default font provider (1 = autodetect).</param>
    /// <param name="config">Fontconfig config path, or null.</param>
    /// <param name="update">Whether to update fontconfig cache (1 = yes).</param>
    [LibraryImport(LibassLibrary, EntryPoint = "ass_set_fonts", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void AssSetFonts(
        IntPtr renderer,
        string? defaultFont,
        string? defaultFamily,
        int dfp,
        string? config,
        int update);

    /// <summary>
    ///     Sets the fonts directory for the library.
    /// </summary>
    /// <param name="library">ASS_Library handle.</param>
    /// <param name="fontsDir">Path to directory containing font files.</param>
    [LibraryImport(LibassLibrary, EntryPoint = "ass_set_fonts_dir", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void AssSetFontsDir(
        IntPtr library,
        string fontsDir);

    /// <summary>
    ///     Reads a subtitle file and creates an ASS_Track.
    /// </summary>
    /// <param name="library">ASS_Library handle.</param>
    /// <param name="fname">Path to the ASS/SSA subtitle file.</param>
    /// <param name="codepage">Character encoding, or null for auto-detection.</param>
    /// <returns>ASS_Track handle, or <see cref="IntPtr.Zero" /> on failure.</returns>
    [LibraryImport(LibassLibrary, EntryPoint = "ass_read_file", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial IntPtr AssReadFile(
        IntPtr library,
        string fname,
        string? codepage);

    /// <summary>
    ///     Frees an ASS_Track.
    /// </summary>
    /// <param name="track">ASS_Track handle to free.</param>
    [LibraryImport(LibassLibrary, EntryPoint = "ass_free_track")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void AssFreeTrack(IntPtr track);

    /// <summary>
    ///     Renders a frame at the given timestamp.
    /// </summary>
    /// <param name="renderer">ASS_Renderer handle.</param>
    /// <param name="track">ASS_Track handle.</param>
    /// <param name="now">Timestamp in milliseconds.</param>
    /// <param name="detectChange">Output: non-zero if the rendered image changed.</param>
    /// <returns>Pointer to the first ASS_Image in a linked list, or <see cref="IntPtr.Zero" />.</returns>
    [LibraryImport(LibassLibrary, EntryPoint = "ass_render_frame")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial IntPtr AssRenderFrame(
        IntPtr renderer,
        IntPtr track,
        long now,
        out int detectChange);

    /// <summary>
    ///     Sets the logging callback for libass.
    /// </summary>
    /// <param name="library">ASS_Library handle.</param>
    /// <param name="callback">Log callback delegate, or null to disable logging.</param>
    /// <param name="userData">User data pointer passed to the callback.</param>
    [LibraryImport(LibassLibrary, EntryPoint = "ass_set_message_cb")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void AssSetMessageCb(
        IntPtr library,
        AssLogCallback? callback,
        IntPtr userData);

    /// <summary>
    ///     Registers a custom DLL import resolver to locate libass from the jellyfin-ffmpeg directory.
    ///     Should be called once during plugin initialization.
    /// </summary>
    /// <param name="logger">Logger for diagnostic messages.</param>
    internal static void RegisterResolver(ILogger? logger = null)
    {
        lock (_resolverLock)
        {
            if (_resolverRegistered)
            {
                return;
            }

            NativeLibrary.SetDllImportResolver(
                typeof(LibassNative).Assembly,
                (name, assembly, searchPath) =>
                {
                    if (!string.Equals(name, LibassLibrary, StringComparison.Ordinal))
                    {
                        return IntPtr.Zero;
                    }

                    // Strategy 1: Derive from ffmpeg path
                    var ffmpegPath = FindFfmpegPath();
                    if (!string.IsNullOrEmpty(ffmpegPath))
                    {
                        var ffmpegDir = Path.GetDirectoryName(ffmpegPath) ?? string.Empty;
                        var libassPath = Path.Join(ffmpegDir, "lib", GetPlatformLibName());
                        if (File.Exists(libassPath) && NativeLibrary.TryLoad(libassPath, out var handle1))
                        {
                            logger?.LogInformation("[AssSubsetter] Loaded libass from ffmpeg directory: {Path}", libassPath);
                            return handle1;
                        }

                        libassPath = Path.Join(ffmpegDir, GetPlatformLibName());
                        if (File.Exists(libassPath) && NativeLibrary.TryLoad(libassPath, out var handle2))
                        {
                            logger?.LogInformation("[AssSubsetter] Loaded libass from ffmpeg directory: {Path}", libassPath);
                            return handle2;
                        }
                    }

                    // Strategy 2: Well-known paths
                    string[] wellKnownPaths =
                    [
                        "/usr/lib/jellyfin-ffmpeg/lib/libass.so",
                        "/usr/lib/jellyfin-ffmpeg/lib/libass.so.9",
                        "/usr/lib/x86_64-linux-gnu/libass.so",
                        "/usr/lib/x86_64-linux-gnu/libass.so.9",
                        "/usr/lib/libass.so",
                        "/usr/lib/libass.so.9"
                    ];

                    foreach (var path in wellKnownPaths)
                    {
                        if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle3))
                        {
                            logger?.LogInformation("[AssSubsetter] Loaded libass from well-known path: {Path}", path);
                            return handle3;
                        }
                    }

                    // Strategy 3: System default search
                    if (NativeLibrary.TryLoad(GetPlatformLibName(), out var handle4))
                    {
                        logger?.LogInformation("[AssSubsetter] Loaded libass via system default search.");
                        return handle4;
                    }

                    logger?.LogWarning("[AssSubsetter] Failed to locate libass shared library. ASS to SUP conversion will not be available.");
                    return IntPtr.Zero;
                });

            _resolverRegistered = true;
        }
    }

    private static string? FindFfmpegPath()
    {
        var ffmpegOpt = Environment.GetEnvironmentVariable("JELLYFIN_FFMPEG_OPT");
        if (!string.IsNullOrEmpty(ffmpegOpt))
        {
            const string prefix = "--ffmpeg=";
            var idx = ffmpegOpt.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var path = ffmpegOpt.Substring(idx + prefix.Length).Trim();
                var spaceIdx = path.IndexOf(' ', StringComparison.Ordinal);
                if (spaceIdx > 0)
                {
                    path = path.Substring(0, spaceIdx);
                }

                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        var args = Environment.GetCommandLineArgs();
        foreach (var arg in args)
        {
            if (arg.StartsWith("--ffmpeg=", StringComparison.OrdinalIgnoreCase))
            {
                var path = arg.Substring("--ffmpeg=".Length);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        if (File.Exists("/usr/lib/jellyfin-ffmpeg/ffmpeg"))
        {
            return "/usr/lib/jellyfin-ffmpeg/ffmpeg";
        }

        return null;
    }

    private static string GetPlatformLibName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "libass.dll";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "libass.dylib";
        }

        return "libass.so";
    }

    /// <summary>
    ///     ASS_Image structure returned by ass_render_frame.
    ///     Each element represents a rendered bitmap layer (character/outline/shadow).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct AssImage
    {
        /// <summary>Bitmap width.</summary>
        public int W;

        /// <summary>Bitmap height.</summary>
        public int H;

        /// <summary>Bitmap stride (bytes per row).</summary>
        public int Stride;

        /// <summary>Pointer to 1-byte-per-pixel alpha buffer.</summary>
        public IntPtr Bitmap;

        /// <summary>
        ///     Bitmap color and alpha, RGBA format.
        ///     Note: ASS alpha is inverted (0 = opaque, 255 = transparent).
        /// </summary>
        public uint Color;

        /// <summary>Destination X coordinate on the video frame.</summary>
        public int DstX;

        /// <summary>Destination Y coordinate on the video frame.</summary>
        public int DstY;

        /// <summary>Pointer to next ASS_Image in linked list, or IntPtr.Zero.</summary>
        public IntPtr Next;

        /// <summary>Image type: 0=CHARACTER, 1=OUTLINE, 2=SHADOW.</summary>
        public int Type;
    }
}
