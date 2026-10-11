using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using System.Text.Json;

namespace Jellyfin.Plugin.AssSubsetter.Benchmarks;

/// <summary>Entry point for deterministic, filterable font-inventory benchmarks.</summary>
public static class Program
{
    /// <summary>Runs selected BenchmarkDotNet cases without an interactive menu.</summary>
    /// <param name="args">Optional BenchmarkDotNet filters.</param>
    public static void Main(string[] args)
    {
        string artifactsPath = GetArtifactsPath(args);
        Directory.CreateDirectory(artifactsPath);
        string contractPath = Path.Combine(artifactsPath, "benchmark-contract.json");
        File.WriteAllText(contractPath, JsonSerializer.Serialize(SubsettingBenchmarkContract.Create(), new JsonSerializerOptions { WriteIndented = true }));
        (Job job, string[] benchmarkArgs) = GetJob(args);
        IConfig config = ManualConfig.CreateMinimumViable()
            .AddJob(job.WithToolchain(InProcessNoEmitToolchain.Instance))
            .AddColumn(StatisticColumn.P95)
            .AddExporter(JsonExporter.Full);
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(benchmarkArgs, config);
    }

    private static string GetArtifactsPath(string[] args)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], "--artifacts", StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return "BenchmarkDotNet.Artifacts";
    }

    private static (Job Job, string[] Args) GetJob(string[] args)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (!string.Equals(args[index], "--job", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(args[index], "-j", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Job job = args[index + 1].ToLowerInvariant() switch
            {
                "dry" => Job.Default.WithStrategy(RunStrategy.ColdStart).WithWarmupCount(0).WithIterationCount(1),
                "short" => Job.Default.WithWarmupCount(3).WithIterationCount(3),
                "medium" => Job.Default.WithWarmupCount(10).WithIterationCount(15).WithLaunchCount(2),
                "long" => Job.Default.WithWarmupCount(15).WithIterationCount(100).WithLaunchCount(3),
                _ => Job.Default
            };
            return (job, args.Where((_, position) => position != index && position != index + 1).ToArray());
        }

        return (Job.Default, args);
    }
}
