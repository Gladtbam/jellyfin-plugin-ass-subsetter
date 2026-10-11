using System.Text;
using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Benchmarks;

/// <summary>Creates deterministic data without reading host font collections.</summary>
internal static class SyntheticInventory
{
    internal const int FileCount = 20_000;
    internal static RuntimeFontFace[] CreateFaces()
    {
        var faces = new RuntimeFontFace[FileCount];
        for (int index = 0; index < faces.Length; index++)
        {
            string family = $"Synthetic Family {index:D5}";
            faces[index] = new RuntimeFontFace(index + 1, $"/synthetic/fonts/{index:D5}.ttf", 0, family, "Regular", [family, $"SyntheticAlias{index:D5}", $"Synthetic Alias {index:D5}", $"S{index:D5}"], 400, 5, 0, false, false, "TrueType", 100, true);
        }

        return faces;
    }

    internal static FontFaceEvidence[] CreateEvidence()
    {
        var faces = new FontFaceEvidence[FileCount];
        for (int index = 0; index < faces.Length; index++)
        {
            bool controlled = index >= FileCount - 100;
            int bucket = controlled ? (index - (FileCount - 100)) / 10 : index;
            string family = controlled ? $"Controlled Family {bucket:D2}" : $"Unrelated Family {index:D5}";
            string strict = controlled ? $"controlled-strict-{bucket:D2}" : $"unrelated-strict-{index:D5}";
            uint scalar = checked((uint)(0x1000 + index));
            faces[index] = new FontFaceEvidence
            {
                FaceId = index + 1,
                FileId = index + 1,
                IsClassifiable = true,
                IsDeepEvidenceComplete = true,
                PhysicalFileId = $"synthetic-file-{index:D5}",
                FaceIndex = 0,
                FileHash = Bytes($"file-hash-{index:D5}"),
                StrictFingerprint = Bytes(strict),
                Identity = new FontIdentityEvidence(
                    family,
                    family,
                    "REGULAR",
                    16,
                    17,
                    family + " REGULAR",
                    "SYNTHETIC:" + family,
                    family,
                    new HashSet<string>(StringComparer.Ordinal) { family, family + " REGULAR" },
                    "TEST",
                    400,
                    5,
                    0,
                    new FontVersionEvidence(null, false, [])),
                Technology = "TrueType",
                UnitsPerEm = 1000,
                UnicodeCoverage = [new UnicodeRange(scalar, scalar)],
                CharacterFingerprints = new Dictionary<uint, ReadOnlyMemory<byte>>
                {
                    [scalar] = Bytes($"glyph-{index:D5}")
                }
            };
        }

        return faces;
    }

    private static ReadOnlyMemory<byte> Bytes(string value) => Encoding.UTF8.GetBytes(value);
}
