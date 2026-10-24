using Kasi.Fuzz;
using SharpFuzz;

// Entry point for libFuzzer (build/Run-Fuzz.ps1 instruments the assemblies under test with SharpFuzz and starts libfuzzer-dotnet on
// this program). KASI_FUZZ_TARGET picks the target: "filename" (default, Kasi.Engine), "host" (Kasi.NativeHost and Kasi.Ipc),
// "hls" or "dash" (Kasi.Media). Run without the fuzzer, "--replay <file or folder>" feeds the same target the saved inputs one by
// one, which is how a finding is reproduced and how the regression corpus is checked; "--host-seeds <folder>" writes the valid host
// messages the corpus starts from.
var target = Environment.GetEnvironmentVariable("KASI_FUZZ_TARGET") ?? "filename";

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
        switch (target)
        {
            case "host":
                HostMessageFuzz.Run(File.ReadAllBytes(file));
                break;
            case "hls":
                HlsFuzz.Run(File.ReadAllBytes(file));
                break;
            case "dash":
                DashFuzz.Run(File.ReadAllBytes(file));
                break;
            default:
                FileNameFuzz.Run(File.ReadAllBytes(file));
                break;
        }
    }

    Console.WriteLine($"replayed {files.Length} input(s) without a finding");
    return 0;
}

switch (target)
{
    case "host":
        Fuzzer.LibFuzzer.Run(HostMessageFuzz.Run);
        break;
    case "hls":
        Fuzzer.LibFuzzer.Run(HlsFuzz.Run);
        break;
    case "dash":
        Fuzzer.LibFuzzer.Run(DashFuzz.Run);
        break;
    default:
        Fuzzer.LibFuzzer.Run(FileNameFuzz.Run);
        break;
}

return 0;
