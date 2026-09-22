namespace Sink.Models;

/// <summary>Progress reported by long-running Settings library maintenance.</summary>
public sealed record LibraryMaintenanceProgress(string Message, int Completed = 0, int Total = 0)
{
    public bool IsIndeterminate => Total <= 0;
    public double Percent => Total <= 0 ? 0 : Math.Clamp(Completed * 100d / Total, 0, 100);
}
