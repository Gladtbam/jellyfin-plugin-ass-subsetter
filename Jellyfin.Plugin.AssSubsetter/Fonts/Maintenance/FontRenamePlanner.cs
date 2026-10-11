using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.Naming;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>Plans portable complete target paths without changing files or directories.</summary>
public sealed class FontRenamePlanner
{
    /// <summary>The compatible persisted candidate calculation version.</summary>
    public const int CurrentCandidateVersion = 1;

    private readonly IFontRenameDirectoryOperations _directories;
    private readonly Func<string, bool> _inUse;

    /// <summary>Initializes a new instance of the <see cref="FontRenamePlanner"/> class.</summary>
    /// <param name="directories">The read-only physical path and filesystem capability boundary.</param>
    /// <param name="inUse">Checks active runtime leases without accessing the catalog.</param>
    public FontRenamePlanner(IFontRenameDirectoryOperations directories, Func<string, bool>? inUse = null)
    {
        _directories = directories ?? throw new ArgumentNullException(nameof(directories));
        _inUse = inUse ?? (_ => false);
    }

    /// <summary>Creates an immutable plan from published evidence and the exact rule snapshot.</summary>
    /// <param name="generationId">The published generation.</param>
    /// <param name="analysisBatchId">The completed analysis.</param>
    /// <param name="files">The published physical files.</param>
    /// <param name="quarantineCandidateFileIds">Files reserved for duplicate isolation.</param>
    /// <param name="rules">The exact immutable rules.</param>
    /// <param name="policy">The current directory safety policy.</param>
    /// <param name="cancellationToken">Cancels preview calculation between file checks.</param>
    /// <returns>The proposals, exclusions and evaluated evidence.</returns>
    public FontRenameSnapshot Build(long generationId, long analysisBatchId, IReadOnlyList<FontFileRecord> files, IReadOnlySet<long> quarantineCandidateFileIds, FontNamingRules rules, FontDirectoryPolicyResolver policy, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(rules);
        FontNamingCompilation fileProgram = FontNamingTemplate.Compile(rules.FileNameTemplate, FontTemplateKind.FileName);
        FontNamingCompilation folderProgram = FontNamingTemplate.Compile(rules.FolderTemplate, FontTemplateKind.Folder);
        if (rules.FormatVersion != FontNamingRules.CurrentFormatVersion || fileProgram.Program is null || folderProgram.Program is null)
        {
            throw new ArgumentException("Invalid font naming rules.", nameof(rules));
        }

        var directories = _directories.CreatePreviewScope();
        var items = new Dictionary<long, FontRenameCandidate>();
        var proposals = new List<Proposal>();
        foreach (FontFileRecord file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? root = policy.GetOwningCustomRoot(file.Path);
            string? reason = root is null || (file.DirectoryPolicy & FontDirectoryPolicy.Protected) != 0
                ? "protected-or-not-custom"
                : file.Health != FontFileHealth.Healthy || file.Faces.Count == 0 || file.LinkKind != FontLinkKind.None || file.PathAliases.Count != 0
                    ? "unclassifiable-or-link"
                    : quarantineCandidateFileIds.Contains(file.Id) ? "quarantine-candidate" : _inUse(file.Path) ? "in-use" : null;
            var item = new FontRenameCandidate { File = file, SourceRoot = root, SkipReason = reason };
            items.Add(file.Id, item);
            if (reason is not null)
            {
                continue;
            }

            var fields = FontNamingFieldResolver.Resolve(file);
            FontTemplateResult name = FontNamingTemplate.Expand(fileProgram.Program, fields);
            FontTemplateResult folder = FontNamingTemplate.Expand(folderProgram.Program, fields);
            item = item with
            {
                NamingEvidence = new(name.Evidence.Fields.Concat(folder.Evidence.Fields).Concat(fields.Get("version").Evidence).Distinct().ToArray(), name.Evidence.Selections.Concat(folder.Evidence.Selections).Distinct().ToArray()),
                SkipReason = name.SkipReason ?? folder.SkipReason
            };
            items[file.Id] = item;
            if (item.SkipReason is not null)
            {
                continue;
            }

            string parent = folder.PreserveSourceDirectory ? Path.GetDirectoryName(file.Path)! : folder.Components.Aggregate(root!, Path.Combine);
            if (!FontPathComparison.IsWithin(parent, root!))
            {
                items[file.Id] = item with { SkipReason = "outside-source-root" };
                continue;
            }

            if ((policy.GetPolicy(parent) & FontDirectoryPolicy.Protected) != 0)
            {
                items[file.Id] = item with { SkipReason = "protected-or-not-custom" };
                continue;
            }

            if (!FontPathComparison.Comparer.Equals(policy.GetOwningCustomRoot(parent), root))
            {
                items[file.Id] = item with { SkipReason = "target-root-changed" };
                continue;
            }

            string extension = Path.GetExtension(file.Path).ToLowerInvariant();
            if (extension is not ".ttf" and not ".otf" and not ".ttc" and not ".otc")
            {
                items[file.Id] = item with { SkipReason = "unsupported-container" };
                continue;
            }

            if (GetBudget(parent) <= Encoding.UTF8.GetByteCount(extension))
            {
                items[file.Id] = item with { SkipReason = "path-too-long" };
                continue;
            }

            FontRenameDirectoryProbe probe = directories.Probe(file.Path, root!, parent);
            if (!probe.IsSafe || !probe.IsSameFileSystem)
            {
                items[file.Id] = item with { SkipReason = probe.Error ?? "different-filesystem" };
                continue;
            }

            string stem = name.Components.Single();
            string? candidate = Bound(parent, stem, name.ProtectedSuffix, extension, null);
            if (candidate is null)
            {
                items[file.Id] = item with { SkipReason = "path-too-long-or-unsafe-name" };
                continue;
            }

            proposals.Add(new(file.Id, parent, stem, name.ProtectedSuffix, extension, candidate, fields));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var knownPaths = files.GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Select(file => file.Id).ToArray(), StringComparer.OrdinalIgnoreCase);
        var occupied = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var assigned = new Dictionary<long, (string Path, string? Fingerprint)>();
        var collisions = new List<(Proposal Proposal, string Path, string Fingerprint)>();
        var ordered = proposals.OrderBy(proposal => CanonicalFingerprint(items[proposal.FileId].File), StringComparer.Ordinal)
            .ThenBy(proposal => Convert.ToHexString(items[proposal.FileId].File.Sha256.Span), StringComparer.Ordinal)
            .ThenBy(proposal => items[proposal.FileId].File.Path, StringComparer.Ordinal).ToArray();

        // Reserve accurate base owners across every group, including truncation collisions.
        foreach (var proposal in ordered.Where(proposal => FontPathComparison.Comparer.Equals(proposal.BasePath, items[proposal.FileId].File.Path)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (occupied.TryAdd(proposal.BasePath, proposal.FileId))
            {
                assigned.Add(proposal.FileId, (proposal.BasePath, null));
            }
        }

        // Reserve existing fingerprint targets before another name group can claim them as a base.
        var baseWinners = ordered.GroupBy(proposal => proposal.BasePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.FirstOrDefault(proposal => assigned.ContainsKey(proposal.FileId)) ?? group.First(), StringComparer.OrdinalIgnoreCase);
        var fingerprintOwners = new Dictionary<long, (Proposal Proposal, string Path, string Fingerprint)>();
        foreach (var proposal in ordered.Where(proposal => !assigned.ContainsKey(proposal.FileId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fingerprint = proposal.Fields.Get("fingerprint");
            string? candidate = fingerprint.BlocksWhenReferenced ? null : Bound(proposal.Parent, proposal.Stem, proposal.ProtectedSuffix, proposal.Extension, fingerprint.Text);
            if (candidate is not null && FontPathComparison.Comparer.Equals(candidate, items[proposal.FileId].File.Path))
            {
                fingerprintOwners.Add(proposal.FileId, (proposal, candidate, fingerprint.Text));
            }
        }

        var ownersToReserve = new Queue<long>(fingerprintOwners.Keys.Where(id => baseWinners[fingerprintOwners[id].Proposal.BasePath].FileId != id));
        while (ownersToReserve.TryDequeue(out long id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owner = fingerprintOwners[id];
            if (assigned.ContainsKey(id) || !occupied.TryAdd(owner.Path, id))
            {
                continue;
            }

            assigned.Add(id, (owner.Path, owner.Fingerprint));
            var item = items[id];
            items[id] = item with { NamingEvidence = item.NamingEvidence with { Fields = item.NamingEvidence.Fields.Concat(owner.Proposal.Fields.Get("fingerprint").Evidence).Distinct().ToArray() } };
            // A displaced base winner may itself own a fingerprint target in another group.
            if (baseWinners.TryGetValue(owner.Path, out var displaced) && fingerprintOwners.ContainsKey(displaced.FileId) && !assigned.ContainsKey(displaced.FileId))
            {
                ownersToReserve.Enqueue(displaced.FileId);
            }
        }

        foreach (var proposal in ordered.Where(proposal => !assigned.ContainsKey(proposal.FileId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (occupied.TryAdd(proposal.BasePath, proposal.FileId))
            {
                assigned.Add(proposal.FileId, (proposal.BasePath, null));
                continue;
            }

            var fingerprint = proposal.Fields.Get("fingerprint");
            var item = items[proposal.FileId];
            items[proposal.FileId] = item with { NamingEvidence = item.NamingEvidence with { Fields = item.NamingEvidence.Fields.Concat(fingerprint.Evidence).Distinct().ToArray() } };
            if (fingerprint.BlocksWhenReferenced)
            {
                items[proposal.FileId] = items[proposal.FileId] with { SkipReason = "fingerprint:" + fingerprint.MissingReason };
                continue;
            }

            string? candidate = Bound(proposal.Parent, proposal.Stem, proposal.ProtectedSuffix, proposal.Extension, fingerprint.Text);
            if (candidate is null)
            {
                items[proposal.FileId] = items[proposal.FileId] with { SkipReason = "path-too-long-or-unsafe-name" };
                continue;
            }

            collisions.Add((proposal, candidate, fingerprint.Text));
        }

        // A saved fingerprint owner wins over an unorganized file with the same digest.
        foreach (var collision in collisions.OrderByDescending(collision => FontPathComparison.Comparer.Equals(collision.Path, items[collision.Proposal.FileId].File.Path)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            long id = collision.Proposal.FileId;
            if (occupied.TryAdd(collision.Path, id))
            {
                assigned.Add(id, (collision.Path, collision.Fingerprint));
            }
            else
            {
                items[id] = items[id] with { SkipReason = "fingerprint-collision" };
            }
        }

        foreach (var assignment in assigned)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[assignment.Key];
            string candidate = assignment.Value.Path;
            bool self = FontPathComparison.Comparer.Equals(candidate, item.File.Path);
            bool knownOther = knownPaths.TryGetValue(candidate, out long[]? owners) && owners.Any(id => id != item.File.Id);
            string? reason = (File.Exists(candidate) || Directory.Exists(candidate) || knownOther) && !self
                ? "target-occupied"
                : string.Equals(item.File.Path, candidate, StringComparison.Ordinal) ? "already-normalized" : null;
            items[assignment.Key] = item with
            {
                NewPath = candidate,
                SkipReason = reason,
                CollisionFingerprint = assignment.Value.Fingerprint
            };
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new FontRenameSnapshot(generationId, analysisBatchId, rules, rules.GetFingerprint(), items.Values.OrderBy(item => item.File.Id).ToArray());
    }

    private static int GetBudget(string parent) => 240 - Encoding.UTF8.GetByteCount(parent) - 1;

    private static string? Bound(string parent, string stem, string protectedSuffix, string extension, string? fingerprint)
    {
        int budget = GetBudget(parent);
        string collisionSuffix = fingerprint is null ? string.Empty : "-" + fingerprint;
        if (Encoding.UTF8.GetByteCount(stem + collisionSuffix + extension) <= budget)
        {
            return Path.Combine(parent, stem + collisionSuffix + extension);
        }

        string suffix = protectedSuffix + collisionSuffix;
        if (budget <= Encoding.UTF8.GetByteCount(extension + suffix))
        {
            return null;
        }

        string body = stem[..(stem.Length - protectedSuffix.Length)];
        string bounded = FilenameSanitizer.LimitPath(body, extension, suffix, budget);
        string boundedStem = Path.GetFileNameWithoutExtension(bounded);
        if (boundedStem.Length <= suffix.Length || !FontNamingComponent.IsSafe(boundedStem) || Encoding.UTF8.GetByteCount(bounded) > budget)
        {
            return null;
        }

        return Path.Combine(parent, bounded);
    }

    private static string CanonicalFingerprint(FontFileRecord file)
    {
        if (file.Faces.Count == 1)
        {
            return Convert.ToHexString(file.Faces[0].StrictFingerprint.Span);
        }

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file.Faces.OrderBy(face => face.FaceIndex).SelectMany(face => face.StrictFingerprint.ToArray()).ToArray()));
    }

    private sealed record Proposal(long FileId, string Parent, string Stem, string ProtectedSuffix, string Extension, string BasePath, FontNamingFields Fields);
}
