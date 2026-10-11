using System;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.Naming;

/// <summary>A naming syntax or type validation error.</summary>
/// <param name="code">The stable diagnostic code.</param>
/// <param name="offset">The original input location.</param>
public sealed class NamingSyntaxException(string code, int offset) : Exception(code)
{
    /// <summary>Gets the error position in the original input.</summary>
    public int Offset { get; } = offset;
}
