namespace Radiosender.Services.Scheduling;

public record ScheduleOptions
{
    public TimeSpan NewsDuration { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan AdsPerHour { get; init; } = TimeSpan.FromMinutes(20);

    public TimeSpan ModeratorPerHour { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan MaxAdBlock { get; init; } = TimeSpan.FromMinutes(5);

    public int Attempts { get; init; } = 400;
}
