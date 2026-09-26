using System;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;

namespace Strikers.App;

public partial class PlayPanel
{
    private readonly StopReport stopReport = new();
    private string? reportZip;
    private DateTime? reportPendingSince;
    private (string Headline, string Detail, bool Ask)? stopSaid;

    private void StopWithReport(string headline, string detail, bool ask, bool swapComing)
    {
        if (stopReport.SaveAtStop(driver.Generation))
        {
            foreach (var line in Play.StopLines(FactsFor(headline)))
            {
                driver.Say(line);
            }

            SaveReport(headline);
        }

        stopSaid = (headline, detail, ask);
        reportPendingSince = swapComing && !stopReport.TheirLogLanded ? DateTime.UtcNow : null;
        if (reportPendingSince is not null)
        {
            tick.Start();
        }

        ShowStop();
    }

    private void TheirRecordingLanded()
    {
        stopReport.NoteTheirRecording(driver.Generation);
    }

    private void TheirLogLanded()
    {
        if (!stopReport.SaveOnTheirLog(driver.Generation))
        {
            return;
        }

        SaveReport(stopSaid?.Headline ?? "Stopped");
        reportPendingSince = null;
        ShowStop();
    }

    private void CheckReportSettled()
    {
        if (reportPendingSince is not { } since || !Play.ReportSettled(since, DateTime.UtcNow))
        {
            return;
        }

        reportPendingSince = null;
        if (stopReport.SaveOnSettle(driver.Generation))
        {
            SaveReport(stopSaid?.Headline ?? "Stopped");
        }

        ShowStop();
    }

    private ReportFacts FactsFor(string why)
    {
        var hosted = playSpawned || driver.AnythingRunning ? flow.Hosting : (bool?)null;
        return Identity.Facts(why, hosted);
    }

    private void SaveReport(string why)
    {
        var zip = Report.TrySave(AppContext.BaseDirectory, DateTime.Now, out var problem, FactsFor(why));
        if (zip is null)
        {
            driver.Say($"report not saved: {problem}");
            return;
        }

        driver.Say($"report saved: {Report.Folder}\\{System.IO.Path.GetFileName(zip)}");
        if (reportZip is not null && !string.Equals(reportZip, zip, StringComparison.OrdinalIgnoreCase))
        {
            Report.Drop(reportZip);
        }

        reportZip = zip;
    }

    private void ShowStop()
    {
        if (stopSaid is not { } said)
        {
            return;
        }

        var screen = Play.StopScreenFor(said.Detail, said.Ask, pending: reportPendingSince is not null,
                                        reportSaved: reportZip is not null, stopReport.TheirsLanded);

        SayBad(said.Headline, screen.Detail);
        if (playSpawned)
        {
            Find<TextBlock>("PlayStatus").Text = screen.Status;
        }

        Find<Button>("StopButton").IsEnabled = screen.StopEnabled;
        Find<Arc>("WaitSpinner").IsVisible = screen.Spinner;
        Find<Button>("ReportProblemButton").IsVisible = screen.ReportVisible;
        Find<Button>("NewInviteButton").IsVisible = false;
        Find<Button>("UseArmyButton").IsVisible = false;
        Find<Button>("CodeMatchesButton").IsVisible = false;
        Find<Button>("WriteButton").IsVisible = false;
    }

    private void ForgetStop()
    {
        stopSaid = null;
        reportPendingSince = null;
        reportZip = null;
        Find<Button>("StopButton").IsEnabled = true;
        Find<Arc>("WaitSpinner").IsVisible = false;
        Find<Button>("ReportProblemButton").IsVisible = false;
    }

    private void SendReport()
    {
        if (stopSaid is { } said && reportPendingSince is null)
        {
            SaveReport(said.Headline);
        }

        var owner = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle ?? 0;
        var problem = Identity.SendReport(reportZip, owner, stopSaid?.Headline);
        if (problem is not null)
        {
            ToastRail.Show(ToastKind.Bad, problem);
            return;
        }

        ToastRail.Show(ToastKind.Info, Identity.SendHint);
    }

    private void PlayEnded(int? code)
    {
        if (!Play.PlayEndedUnexpectedly(code, halted))
        {
            return;
        }

        if (Play.RingsOnHalt(halted))
        {
            Attention.Raise(TopLevel.GetTopLevel(this) as Window);
        }

        halted = true;
        Ink("PlayStatus", Palette.SlateDark.Bad);
        Find<Border>("PlaySyncDot").Classes.Remove("on");
        var (step, detail) = Play.StoppedHereText();
        StopWithReport(step, detail, ask: true, swapComing: false);
    }
}
