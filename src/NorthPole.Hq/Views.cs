using NorthPole.Hq.Santa;
using NorthPole.Hq.Wishes;

namespace NorthPole.Hq;

/// <summary>What the child's page sees. Santa's reply appears only once the letter has "arrived".</summary>
public sealed record KidWishView(
    Guid Id,
    DateOnly Day,
    string Text,
    bool HasPhoto,
    string Status,
    IReadOnlyList<WishEvent> Timeline,
    string? SantaReply,
    bool HasVoice,
    bool ByReindeer)
{
    public bool IsFinal => Status is nameof(WishStatus.Answered) or nameof(WishStatus.Failed);

    public static KidWishView From(Wish w) => new(
        w.Id,
        w.Day,
        w.Text,
        w.PhotoFile is not null,
        w.Status.ToString(),
        w.Timeline.ToList(),
        w.Status == WishStatus.Answered ? w.Reading?.SantaReply : null,
        w.Status == WishStatus.Answered && w.VoiceFile is not null,
        w.DeliveredByReindeer);
}

public sealed record KidHome(
    string Name,
    string DayLabel,
    int SleepsToChristmas,
    bool CanWishNow,
    KidWishView? Latest,
    IReadOnlyList<KidWishView> Earlier);

/// <summary>Everything the parents' dashboard shows about a wish.</summary>
public sealed record ElfWishView(
    Guid Id,
    string ChildId,
    string ChildName,
    DateOnly Day,
    DateTimeOffset CreatedAt,
    string Text,
    bool HasPhoto,
    string Status,
    SantaReading? Reading,
    IReadOnlyList<bool> Bought,
    bool HasVoice,
    bool ByReindeer,
    string? Brain,
    decimal? CostUsd)
{
    public static ElfWishView From(Wish w) => new(
        w.Id, w.ChildId, w.ChildName, w.Day, w.CreatedAt, w.Text, w.PhotoFile is not null, w.Status.ToString(),
        w.Reading, w.Bought.ToList(), w.VoiceFile is not null, w.DeliveredByReindeer, w.Brain, w.Usage?.EstimatedCostUsd);
}

public static class Calendar
{
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    public static int SleepsToChristmas(DateOnly day)
    {
        var christmas = new DateOnly(day.Month == 12 && day.Day > 25 ? day.Year + 1 : day.Year, 12, 25);
        return christmas.DayNumber - day.DayNumber;
    }

    public static string DayLabel(DateOnly day) => day switch
    {
        { Month: 12, Day: <= 24 } => $"Day {day.Day} of 24",
        { Month: 12, Day: 25 } => "Christmas Day!",
        _ => day.ToString("dddd d MMMM", System.Globalization.CultureInfo.InvariantCulture),
    };
}
