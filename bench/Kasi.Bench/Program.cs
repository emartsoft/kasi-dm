using BenchmarkDotNet.Running;
using Kasi.Bench;

// Macro modes (throughput, local-1gbit, resources, compare) are handled by Cli; everything else is BenchmarkDotNet.
if (args.Length > 0 && Cli.Modes.Contains(args[0]))
{
    return await Cli.RunAsync(args);
}

BenchmarkSwitcher.FromAssembly(typeof(Cli).Assembly).Run(args);
return 0;
