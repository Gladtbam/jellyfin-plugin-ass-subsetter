using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.AssSubsetter.Models;

/// <summary>
///     A subset font kept separate from the rewritten ASS document for container packaging.
/// </summary>
/// <param name="FileName">Safe attachment file name.</param>
/// <param name="MimeType">Attachment MIME type.</param>
/// <param name="Data">Subset font binary.</param>
[SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "The immutable record carries font bytes directly into file APIs and does not expose mutable application state.")]
public sealed record SubsetFontAttachment(string FileName, string MimeType, byte[] Data);
