using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.Naming;

/// <summary>The single catalog shared by template validation and settings help.</summary>
public static class FontNamingFieldCatalog
{
    /// <summary>Gets the complete ordered text-placeholder catalog.</summary>
    public static IReadOnlyList<FontNamingFieldDescriptor> Fields { get; } = Array.AsReadOnly<FontNamingFieldDescriptor>([
        new("family", "skip", false),
        new("style", "empty", false),
        new("fullName", "skip", false),
        new("postscript", "skip", false),
        new("manufacturer", "empty", false),
        new("version", "empty", false),
        new("legacyFamily", "skip", false),
        new("legacyStyle", "empty", false),
        new("typographicFamily", "skip", false),
        new("typographicStyle", "empty", false),
        new("styleSuffix", "empty", true),
        new("versionSuffix", "empty", true),
        new("collectionSuffix", "collection-family-required", true),
        new("originalName", "skip", false),
        new("faceCount", "skip", false),
        new("fingerprint", "skip", false)
    ]);
}
