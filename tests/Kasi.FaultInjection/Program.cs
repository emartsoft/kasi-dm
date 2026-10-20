using System.Globalization;
using Kasi.Core;
using Kasi.FaultInjection;

// Two modes. With three arguments (<database> <job id> <bytes per second>) this is the child the kill loops start: it opens the
// engine (which recovers anything a previous, killed run left behind), starts or resumes the job and waits for it to end.
// Otherwise it is the harness itself: it parses the options and runs a suite (see FaultInjectionOptions.Usage).
if (args.Length == 3
    && JobId.TryParse(args[1], out var childJob)
    && long.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytesPerSecond))
{
    return await ChildRunner.RunAsync(args[0], childJob, bytesPerSecond);
}

try
{
    return await FaultInjectionRunner.RunAsync(FaultInjectionOptions.Parse(args));
}
catch (ArgumentException ex)
{
    await Console.Error.WriteLineAsync(ex.Message);
    await Console.Error.WriteLineAsync(FaultInjectionOptions.Usage);
    return 2;
}
