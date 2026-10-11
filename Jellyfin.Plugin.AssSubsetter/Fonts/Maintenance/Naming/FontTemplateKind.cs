namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.Naming;

/// <summary>The path context in which a template is compiled.</summary>
public enum FontTemplateKind
{
    /// <summary>A single filename stem.</summary>
    FileName,

    /// <summary>Root-relative directory components.</summary>
    Folder
}
