using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.Naming;

/// <summary>The compiled program or validation errors.</summary>
/// <param name="Program">The validated immutable program.</param>
/// <param name="Errors">The compilation errors.</param>
public sealed record FontNamingCompilation(FontNamingProgram? Program, IReadOnlyList<FontNamingError> Errors);
