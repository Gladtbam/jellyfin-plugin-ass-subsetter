using System;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.Naming;

/// <summary>A naming syntax or type validation error.</summary>
public sealed class NamingSyntaxException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="NamingSyntaxException"/> class.</summary>
    public NamingSyntaxException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="NamingSyntaxException"/> class.</summary>
    /// <param name="message">The syntax error message.</param>
    public NamingSyntaxException(string message) : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="NamingSyntaxException"/> class.</summary>
    /// <param name="message">The syntax error message.</param>
    /// <param name="innerException">The exception that caused the syntax error.</param>
    public NamingSyntaxException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="NamingSyntaxException"/> class with a source location.</summary>
    /// <param name="code">The stable diagnostic code.</param>
    /// <param name="offset">The original input location.</param>
    public NamingSyntaxException(string code, int offset) : base(code)
    {
        Offset = offset;
    }

    /// <summary>Gets the error position in the original input.</summary>
    public int Offset { get; }
}
