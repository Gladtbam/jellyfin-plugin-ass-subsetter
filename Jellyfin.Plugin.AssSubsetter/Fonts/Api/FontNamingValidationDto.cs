using System.Collections.Generic;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.Naming;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>The current text-template diagnostics and shared settings catalog.</summary>
/// <param name="Errors">All positioned syntax errors.</param>
/// <param name="DefaultFileNameTemplate">The canonical filename template.</param>
/// <param name="FormatVersion">The supported rule version.</param>
/// <param name="Fields">The ordered text fields and fixed missing policies.</param>
public sealed record FontNamingValidationDto(IReadOnlyList<FontNamingError> Errors, string DefaultFileNameTemplate, int FormatVersion, IReadOnlyList<FontNamingFieldDescriptor> Fields);
