using Anthropic.Helpers;

namespace NorthPole.Hq.Santa;

/// <summary>
/// What Claude returns for each wish. The C# SDK turns this class into a JSON schema
/// (structured outputs), so the reply is guaranteed to parse: no regex, no "please answer in JSON".
/// Only <see cref="SantaReply"/> is shown to the child; everything else is for the parents' dashboard.
/// </summary>
[SchemaClass("Santa's reading of one child's daily wish letter.")]
public sealed class SantaReading
{
    [SchemaProperty("What Santa says out loud to the child. 40-80 words, warm and specific, written for the ear.")]
    public string SantaReply { get; set; } = "";

    [SchemaProperty("One sentence for the parents summarising what the child asked for and why.")]
    public string ParentSummary { get; set; } = "";

    [SchemaProperty("Concrete gift ideas the letter mentions or clearly implies. Empty if none.")]
    public List<GiftIdea> GiftIdeas { get; set; } = [];

    [SchemaProperty("The overall feeling of the letter.",
        Enum = new object[] { "excited", "hopeful", "kind", "silly", "curious", "worried", "sad" })]
    public string Mood { get; set; } = "hopeful";

    [SchemaProperty("A private note for the parents ONLY if something needs a grown-up's attention " +
                    "(worry, sadness, a fight, illness, fear). Otherwise an empty string.")]
    public string ParentHeadsUp { get; set; } = "";

    [SchemaProperty("If a drawing or photo was attached: a short description of it. Otherwise an empty string.")]
    public string DrawingDescription { get; set; } = "";
}

public sealed class GiftIdea
{
    [SchemaProperty("The thing, as specific as the letter allows, e.g. 'LEGO fire station' or 'purple scooter'.")]
    public string Item { get; set; } = "";

    [SchemaProperty("Kind of gift.",
        Enum = new object[] { "lego", "toy", "book", "game", "craft", "outdoor", "clothing", "experience", "pet", "other" })]
    public string Category { get; set; } = "other";

    [SchemaProperty("True if the child wished this for someone else (a sibling, a parent, a pet).")]
    public bool ForSomeoneElse { get; set; }
}
