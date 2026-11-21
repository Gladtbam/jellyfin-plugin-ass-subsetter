using System;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.Naming;

/// <summary>A required naming field or result is unavailable.</summary>
public sealed class NamingEvaluationException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="NamingEvaluationException"/> class.</summary>
    public NamingEvaluationException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="NamingEvaluationException"/> class.</summary>
    /// <param name="reason">The failure with field provenance.</param>
    public NamingEvaluationException(string reason) : base(reason)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="NamingEvaluationException"/> class.</summary>
    /// <param name="message">The evaluation failure message.</param>
    /// <param name="innerException">The exception that caused the evaluation failure.</param>
    public NamingEvaluationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
