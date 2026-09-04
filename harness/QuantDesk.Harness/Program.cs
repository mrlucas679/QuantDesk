using QuantDesk.Runtime.Reliability;

string output = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "fault-campaign.json");

FaultCampaignReport report = FaultCampaign.Run();
FaultCampaign.Save(report, output);
Console.WriteLine(
    $"{report.CampaignId}: {report.Passed}/{report.Exercised} exercised cases passed, "
    + $"{report.Exercised}/{report.Total} cases have a driver; {output}");

// Start-up blocks on a case that ran and failed, not on one nobody has written a driver for.
// Refusing to boot over missing coverage would take a working paper system down for a reason
// that has nothing to do with how it behaves under fault. Gate R11 is where full coverage is
// required, and it reads FullyCovered rather than this exit code.
return report.NoExercisedFailure ? 0 : 1;
