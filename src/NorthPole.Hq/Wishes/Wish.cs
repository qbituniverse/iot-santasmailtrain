using NorthPole.Hq.Santa;

namespace NorthPole.Hq.Wishes;

public enum WishStatus
{
    Received,
    Reading,
    OnTheTrain,
    AtTheNorthPole,
    Answered,
    Failed,
}

/// <summary>One step of the letter's journey, shown to the child as it happens.</summary>
public sealed record WishEvent(DateTimeOffset At, string Stage, string Message);

public sealed record TokenUsage(string Model, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens)
{
    /// <summary>Rough cost in USD from the published per-million-token prices (November 2026).</summary>
    public decimal EstimatedCostUsd
    {
        get
        {
            var (input, output, cacheRead) = Model switch
            {
                var m when m.Contains("haiku-5-5") => (0.10m, 0.50m, 0.01m),
                var m when m.Contains("sonnet-5-5") => (2m, 10m, 0.10m),
                var m when m.Contains("opus-5-5") => (4m, 20m, 0.20m),
                _ => (2m, 10m, 0.20m),
            };
            return (InputTokens * input + OutputTokens * output + CacheReadTokens * cacheRead
                    + CacheWriteTokens * input * 1.25m) / 1_000_000m;
        }
    }
}

public sealed class Wish
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string ChildId { get; init; } = "";
    public string ChildName { get; init; } = "";
    public DateOnly Day { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public string Text { get; init; } = "";
    public string? PhotoFile { get; set; }

    public WishStatus Status { get; set; } = WishStatus.Received;
    public List<WishEvent> Timeline { get; set; } = [];

    public SantaReading? Reading { get; set; }
    public string? Brain { get; set; }
    public TokenUsage? Usage { get; set; }
    public string? VoiceFile { get; set; }

    /// <summary>Parents tick gift ideas off as they shop. Same order as Reading.GiftIdeas.</summary>
    public List<bool> Bought { get; set; } = [];

    /// <summary>True if the train couldn't take it and "a reindeer" delivered it instead.</summary>
    public bool DeliveredByReindeer { get; set; }

    public void AddEvent(string stage, string message) => Timeline.Add(new(DateTimeOffset.Now, stage, message));
}
