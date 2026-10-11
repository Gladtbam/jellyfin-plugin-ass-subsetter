using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Maintenance;

public sealed class PhysicalFontRenameDirectoryOperationsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FontRenameDir_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task HiddenFilesRetainDirectoryAndRootCannotBeRemoved()
    {
        string directory = Path.Combine(_root, "old");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ".hidden"), "keep");
        var policy = new FontDirectoryPolicyResolver(_root, null, null, false);
        var operations = new PhysicalFontRenameDirectoryOperations();
        var retained = await operations.TryRemoveEmptyAsync(directory, _root, policy, CancellationToken.None);
        Assert.False(retained.Completed);
        Assert.True(File.Exists(Path.Combine(directory, ".hidden")));
        Assert.False((await operations.TryRemoveEmptyAsync(_root, _root, policy, CancellationToken.None)).Completed);
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public async Task CreationDistinguishesOwnedAndExistingDirectories()
    {
        Directory.CreateDirectory(_root);
        var policy = new FontDirectoryPolicyResolver(_root, null, null, false);
        var operations = new PhysicalFontRenameDirectoryOperations();
        string path = Path.Combine(_root, "new");
        var first = await operations.CreateDirectoryAsync(path, _root, policy, CancellationToken.None);
        var second = await operations.CreateDirectoryAsync(path, _root, policy, CancellationToken.None);
        Assert.True(first.Completed);
        Assert.True(first.CreatedByOperation);
        Assert.True(second.Completed);
        Assert.False(second.CreatedByOperation);
        Assert.True((await operations.TryRemoveEmptyAsync(path, _root, policy, CancellationToken.None)).Completed);
    }

    [Fact]
    public void ProbeUsesTheClosestExistingAncestorAndRejectsUnknownFilesystem()
    {
        Directory.CreateDirectory(_root);
        string source = Path.Combine(_root, "source.ttf");
        File.WriteAllText(source, "font");
        Assert.True(new PhysicalFontRenameDirectoryOperations().Probe(source, _root, Path.Combine(_root, "not-created", "child")).IsSameFileSystem);
        Assert.False(new PhysicalFontRenameDirectoryOperations(_ => null).Probe(source, _root, _root).IsSameFileSystem);
    }

    [Fact]
    public void DanglingLinkedTargetIsRejected()
    {
        Directory.CreateDirectory(_root);
        string source = Path.Combine(_root, "source.ttf");
        File.WriteAllText(source, "font");
        string linked = Path.Combine(_root, "linked");
        CreateDanglingDirectoryLink(linked);

        try
        {
            Assert.Equal("linked-path", new PhysicalFontRenameDirectoryOperations().Probe(source, _root, Path.Combine(linked, "child")).Error);
        }
        finally
        {
            if (OperatingSystem.IsWindows()) Directory.Delete(linked);
            else File.Delete(linked);
        }
    }

    [Fact]
    public async Task UnconfiguredSubdirectoryCannotBeUsedAsSourceRoot()
    {
        string fakeRoot = Path.Combine(_root, "child");
        string target = Path.Combine(fakeRoot, "target");
        Directory.CreateDirectory(target);
        var policy = new FontDirectoryPolicyResolver(_root, null, null, false);
        var operations = new PhysicalFontRenameDirectoryOperations();
        Assert.False((await operations.CreateDirectoryAsync(Path.Combine(fakeRoot, "new"), fakeRoot, policy, CancellationToken.None)).Completed);
        Assert.False((await operations.TryRemoveEmptyAsync(target, fakeRoot, policy, CancellationToken.None)).Completed);
        Assert.True(Directory.Exists(target));
        Assert.False(Directory.Exists(Path.Combine(fakeRoot, "new")));
    }

    [Fact]
    public void PreviewReusesTargetFilesystemEvidenceOnlyWithinOneCalculation()
    {
        Directory.CreateDirectory(_root);
        string firstPath = Path.Combine(_root, "first.ttf");
        string secondPath = Path.Combine(_root, "second.ttf");
        File.WriteAllText(firstPath, "first");
        File.WriteAllText(secondPath, "second");
        var queriedPaths = new List<string>();
        var operations = new PhysicalFontRenameDirectoryOperations(path =>
        {
            queriedPaths.Add(path);
            return "same-filesystem";
        });
        var planner = new FontRenamePlanner(operations);
        var policy = new FontDirectoryPolicyResolver(_root, null, null, false);
        var files = new[]
        {
            FontNamingTestData.File(firstPath, FontNamingTestData.Face(names: [(1, "First")])),
            FontNamingTestData.File(secondPath, FontNamingTestData.Face(names: [(1, "Second")])) with { Id = 2 }
        };

        var first = planner.Build(1, 1, files, new HashSet<long>(), new("{family}", "", false), policy, TestContext.Current.CancellationToken);

        Assert.All(first.Items, item => Assert.Null(item.SkipReason));
        Assert.Equal(1, queriedPaths.Count(path => path == _root));
        Assert.Single(queriedPaths, path => path == firstPath);
        Assert.Single(queriedPaths, path => path == secondPath);

        planner.Build(1, 1, files, new HashSet<long>(), new("{family}", "", false), policy, TestContext.Current.CancellationToken);

        Assert.Equal(2, queriedPaths.Count(path => path == _root));
        Assert.Equal(2, queriedPaths.Count(path => path == firstPath));
        Assert.Equal(2, queriedPaths.Count(path => path == secondPath));
    }

    [Fact]
    public void RefreshedPreviewAndExecutionRejectDirectoryReplacedByLink()
    {
        Directory.CreateDirectory(_root);
        string source = Path.Combine(_root, "source.ttf");
        string target = Path.Combine(_root, "target");
        File.WriteAllText(source, "font");
        Directory.CreateDirectory(target);
        var operations = new PhysicalFontRenameDirectoryOperations();
        var planner = new FontRenamePlanner(operations);
        var files = new[] { FontNamingTestData.File(source, FontNamingTestData.Face(names: [(1, "Example")])) };
        var policy = new FontDirectoryPolicyResolver(_root, null, null, false);
        var rules = new FontNamingRules("{family}", "target", false);
        Assert.Null(Assert.Single(planner.Build(1, 1, files, new HashSet<long>(), rules, policy, TestContext.Current.CancellationToken).Items).SkipReason);
        Directory.Delete(target);
        CreateDanglingDirectoryLink(target);

        try
        {
            Assert.Equal("linked-path", Assert.Single(planner.Build(1, 1, files, new HashSet<long>(), rules, policy, TestContext.Current.CancellationToken).Items).SkipReason);
            Assert.Equal("linked-path", operations.Probe(source, _root, target).Error);
        }
        finally
        {
            if (OperatingSystem.IsWindows()) Directory.Delete(target);
            else File.Delete(target);
        }
    }

    private void CreateDanglingDirectoryLink(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (string argument in new[] { "/c", "mklink", "/J", path, Path.Combine(_root, "missing") }) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        else
        {
            Directory.CreateSymbolicLink(path, Path.Combine(_root, "missing"));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
