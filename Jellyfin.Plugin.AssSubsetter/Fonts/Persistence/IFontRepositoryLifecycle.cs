using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Initializes the shared persistent font store.</summary>
public interface IFontRepositoryLifecycle
{
    /// <summary>Initializes persistent storage.</summary>
    /// <param name="cancellationToken">A token that cancels initialization.</param>
    /// <returns>A task representing initialization.</returns>
    Task InitializeAsync(CancellationToken cancellationToken);
}
