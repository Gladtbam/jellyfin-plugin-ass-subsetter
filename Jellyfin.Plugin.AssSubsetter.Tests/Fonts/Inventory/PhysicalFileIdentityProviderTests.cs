using System.Runtime.InteropServices;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Inventory;

public sealed class PhysicalFileIdentityProviderTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "FontIdentityTests_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void GetIdentity_ShouldMatchHardLinkedPaths()
    {
        Directory.CreateDirectory(_root);
        string first = Path.Join(_root, "first.ttf");
        string second = Path.Join(_root, "second.ttf");
        File.WriteAllBytes(first, [1, 2, 3]);
        CreateHardLink(second, first);
        var provider = new PlatformPhysicalFileIdentityProvider();

        Assert.Equal(provider.GetIdentity(first), provider.GetIdentity(second));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void CreateHardLink(string newPath, string existingPath)
    {
        int result = OperatingSystem.IsWindows()
            ? CreateHardLinkWindows(newPath, existingPath, IntPtr.Zero) ? 0 : Marshal.GetLastPInvokeError()
            : LinkUnix(existingPath, newPath);
        Assert.Equal(0, result);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string newFileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int LinkUnix([MarshalAs(UnmanagedType.LPUTF8Str)] string existingPath, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);
}
