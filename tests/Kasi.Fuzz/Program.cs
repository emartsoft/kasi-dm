using Kasi.Fuzz;
using SharpFuzz;

// Entry point for libFuzzer (build/Run-Fuzz.ps1 instruments the assemblies under test with SharpFuzz and starts libfuzzer-dotnet on
// this program). KASI_FUZZ_TARGET picks the target: "filename" (default, Kasi.Engine) or "host" (Kasi.NativeHost and Kasi.Ipc).
// Run without the fuzzer, "--replay <file or folder>" feeds the same target the saved inputs one by one, which is how a finding is
// reproduced and how the regression corpus is checked; "--host-seeds <folder>" writes the valid host messages the corpus starts from.
var host = Environment.GetEnvironmentVariable("KASI_FUZZ_TARGET") == "host";

if (args.Length == 2 && args[0] == "--host-seeds")
{
    Directory.CreateDirectory(args[1]);
    var index = 0;
    foreach (var seed in HostMessageFuzz.Seeds())
    {
        File.WriteAllBytes(Path.Combine(args[1], $"seed-{index++:D3}"), [0, .. seed]);
    }

    return 0;
}

if (args.Length == 2 && args[0] == "--replay")
{
    var files = Directory.Exists(args[1]) ? Directory.GetFiles(args[1]) : [args[1]];
    foreach (var file in files)
    {
        if (host)
        {
            HostMessageFuzz.Run(File.ReadAllBytes(file));
        }
        else
        {
            FileNameFuzz.Run(File.ReadAllBytes(file));
        }
    }

    Console.WriteLine($"replayed {files.Length} input(s) without a finding");
    return 0;
}

if (host)
{
    Fuzzer.LibFuzzer.Run(HostMessageFuzz.Run);
}
else
{
    Fuzzer.LibFuzzer.Run(FileNameFuzz.Run);
}

return 0;
