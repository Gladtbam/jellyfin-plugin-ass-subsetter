namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>Identifies a persisted duplicate-group member decision.</summary>
public enum DuplicateGroupMemberDecision
{
    /// <summary>The Face belongs to the retained physical file.</summary>
    Retained,

    /// <summary>The Face belongs to a physical file eligible for quarantine.</summary>
    QuarantineCandidate
}
