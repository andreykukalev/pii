namespace Pii.Worker;

public sealed record WorkerCommand(bool Daily, bool FixtureScenario)
{
    public static WorkerCommand Parse(string[] args)
    {
        if (args.Length == 0) return new(false, false);
        if (args.Any(argument => argument is not ("--once" or "--daily" or "--fixture-scenario"))
            || args.Distinct(StringComparer.Ordinal).Count() != args.Length
            || args.Contains("--once") && args.Contains("--daily"))
            throw new ArgumentException("Use --once or --daily, optionally --fixture-scenario in explicit Development FixtureReplay.");
        return new(args.Contains("--daily"), args.Contains("--fixture-scenario"));
    }
}