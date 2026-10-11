using System.Collections.Generic;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.Naming;

internal sealed record FontNamingFieldValue(string Text, string? MissingReason, bool BlocksWhenReferenced, IReadOnlyList<FontNamingFieldEvidence> Evidence);
