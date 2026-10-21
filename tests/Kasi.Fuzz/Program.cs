using Kasi.Fuzz;
using SharpFuzz;

// Entry point for libFuzzer (build/Run-Fuzz.ps1 instruments Kasi.Engine.dll with SharpFuzz and starts libfuzzer-dotnet on this
// program). Run without the fuzzer, "--replay <file or folder>" feeds the same target the saved inputs one by one, which is how a
// finding is reproduced and how the regression corpus is checked.
if (args.Length == 2 && args[0] == "--replay")
{
    var files = Directory.Exists(args[1]) ? Directory.GetFiles(args[1]) : [args[1]];
    foreach (var file in files)
    {
        FileNameFuzz.Run(File.ReadAllBytes(file));
    }

    Console.WriteLine($"replayed {files.Length} input(s) without a finding");
    return 0;
}

Fuzzer.LibFuzzer.Run(FileNameFuzz.Run);
return 0;
