using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Maintenance;

internal static class FontNamingTestData
{
    public static FontFaceRecord Face(int index = 0, byte seed = 0, params (ushort Id, string Value)[] names) => new()
    {
        FaceIndex = index,
        StrictFingerprint = Enumerable.Repeat(seed, 32).ToArray(),
        FamilyName = "not-a-name-record",
        StyleName = "not-a-style-record",
        Names = [],
        Technology = "TrueType",
        NameRecords = names.Select(name => new OpenTypeNameRecord(3, 1, 0x0409, name.Id, name.Value)).ToArray()
    };

    public static FontFileRecord File(string path, params FontFaceRecord[] faces) => new()
    {
        Id = 1,
        GenerationId = 1,
        Path = path,
        NormalizedPath = path,
        PhysicalIdentity = "test-file",
        Sha256 = new byte[32],
        DirectoryPolicy = FontDirectoryPolicy.Custom,
        Health = FontFileHealth.Healthy,
        Faces = faces
    };
}
