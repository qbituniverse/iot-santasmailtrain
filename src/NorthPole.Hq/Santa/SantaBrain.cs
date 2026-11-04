using System.Text;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Messages;
using Anthropic.Services;
using Microsoft.Extensions.Options;
using NorthPole.Hq.Wishes;

namespace NorthPole.Hq.Santa;

/// <summary>Everything Santa needs to answer one letter.</summary>
public sealed record WishLetter(
    ChildOptions Child,
    DateOnly Day,
    string Text,
    byte[]? Photo,
    string? PhotoMediaType,
    IReadOnlyList<PastWish> Earlier);

public sealed record PastWish(DateOnly Day, string Text, string? SantaReply);

public sealed record SantaAnswer(SantaReading Reading, string Brain, TokenUsage? Usage);

public interface ISantaBrain
{
    Task<SantaAnswer> ReadAsync(WishLetter letter, CancellationToken ct);
}

/// <summary>
/// Claude reads the letter (and the drawing, if there is one) and answers as Santa.
///   * Structured outputs: Messages.Create&lt;SantaReading&gt; derives a JSON schema from the class, so the reply always parses.
///   * Prompt caching: the system prompt (persona + family) is identical for every child and every day,
///     so it carries a cache breakpoint. Note that caching only kicks in once the prefix passes the model's
///     minimum cacheable length; grow FamilyLore and it will.
///   * Vision: a photo of a drawing goes in as an image block next to the text.
/// </summary>
public sealed class ClaudeSantaBrain(IOptions<HqOptions> options, AnthropicClient client, ILogger<ClaudeSantaBrain> log)
    : ISantaBrain
{
    readonly HqOptions _o = options.Value;

    public async Task<SantaAnswer> ReadAsync(WishLetter letter, CancellationToken ct)
    {
        var content = new List<ContentBlockParam>();

        if (letter.Earlier.Count > 0)
            content.Add(new TextBlockParam { Text = SantaPrompt.Earlier(letter) });

        content.Add(new TextBlockParam { Text = SantaPrompt.Today(letter) });

        if (letter.Photo is { Length: > 0 } photo)
        {
            content.Add(new ImageBlockParam
            {
                Source = new Base64ImageSource
                {
                    Data = Convert.ToBase64String(photo),
                    MediaType = letter.PhotoMediaType == "image/png" ? MediaType.ImagePng : MediaType.ImageJpeg,
                },
            });
        }

        var parameters = new MessageCreateParams
        {
            Model = _o.Santa.Model,
            MaxTokens = 1024,
            System = new List<TextBlockParam>
            {
                new()
                {
                    Text = SantaPrompt.System(_o),
                    CacheControl = new CacheControlEphemeral(),
                },
            },
            Messages = [new UserMessageParam { Content = content }],
        };

        var response = await client.Messages.Create<SantaReading>(parameters, ct);
        var reading = response.Content.Select(block => block.Parsed()).OfType<SantaReading>().FirstOrDefault()
                      ?? throw new InvalidOperationException("Claude returned no reading");

        var usage = response.Usage;
        var tokens = new TokenUsage(
            _o.Santa.Model,
            usage.InputTokens,
            usage.OutputTokens,
            usage.CacheReadInputTokens ?? 0,
            usage.CacheCreationInputTokens ?? 0);

        log.LogInformation("Santa read {Child}'s letter: {In} in / {Out} out tokens (cache read {Cached}), ~${Cost:0.0000}",
            letter.Child.Name, tokens.InputTokens, tokens.OutputTokens, tokens.CacheReadTokens, tokens.EstimatedCostUsd);

        return new SantaAnswer(reading, _o.Santa.Model, tokens);
    }
}

/// <summary>
/// No API key, no internet, or Claude failed: a cheerful template Santa.
/// It can't really read, so it stays careful: it only names a wish it finds in a clear phrase ("I want a..."),
/// and any sad or worried word gets a gentle reply plus a heads-up for the parents. It errs towards flagging:
/// a needless heads-up costs a parent a glance, a missed one costs far more.
/// </summary>
public sealed class OfflineSantaBrain : ISantaBrain
{
    static readonly string[] Openers =
    [
        "Ho ho ho, {0}! Your letter just rolled into the North Pole on the mail train.",
        "Well hello, {0}! The elves rushed your letter straight to my desk.",
        "{0}, what a lovely letter! Mrs Claus read it over my shoulder.",
    ];

    // Words that mean a grown-up should know. Sad ones are checked first.
    // Skips the cheerful uses kids write a lot in December: "dead excited", "snowball fight".
    static readonly Regex SadWords = new(
        @"\b(sad|unhappy|upset|lonely|cry|cries|crying|cried|tears|miss(es|ed|ing)?\s+(my|him|her|them|you|our)|died|dies|dead(?!\s+(good|excited|happy|cool|fun|funny|easy))|death|passed\s+away|funeral|divorce)\b",
        RegexOptions.IgnoreCase);

    static readonly Regex WorriedWords = new(
        @"\b(worried|worry|worries|worrying|anxious|nervous|scared|afraid|frightened|fear|nightmares?|hospital|sick|ill|poorly|hurt|hurts|bully|bullies|bullied|bullying|(?<!(pillow|snowball|snow|water|food|nerf|sword|play)\s)fight(s|ing)?|argue|arguing|shouting|alone)\b",
        RegexOptions.IgnoreCase);

    // "I want a...", "I'd love...", "please bring me...": the wish is the rest of that sentence.
    static readonly Regex WishPhrase = new(
        @"\b(?: i \s+ (?:really \s+)? (?:wish \s+ for | want | hope \s+ for | ask \s+ for)
              | i (?:\s+ would | ['’]d) \s+ (?:really \s+)? (?:like | love)
              | my \s+ wish \s+ is (?:\s+ for)?
              | can \s+ i \s+ (?:please \s+)? have
              | please \s+ (?:can \s+ i \s+ have | bring (?:\s+ me)?)
           ) \s+ (?!to\b|you\b|it\b|that\b|this\b) (?<wish>[^.!?\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace);

    static readonly Regex TrailingFiller = new(
        @"(\s+(please|for\s+christmas|this\s+year|this\s+christmas|thank\s+you|thanks))+\s*$", RegexOptions.IgnoreCase);

    static readonly Regex DanglingWord = new(@"(\s+(and|or|that|which|with|the|a|an|of|in))+$", RegexOptions.IgnoreCase);

    static readonly Dictionary<string, string> ToYou = new(StringComparer.OrdinalIgnoreCase)
    {
        ["i"] = "you", ["me"] = "you", ["my"] = "your", ["mine"] = "yours", ["myself"] = "yourself", ["our"] = "your",
    };

    public Task<SantaAnswer> ReadAsync(WishLetter letter, CancellationToken ct)
    {
        var name = letter.Child.Name;
        var text = letter.Text.Trim();
        var wish = FindWish(text);
        var sad = SadWords.Match(text);
        var cue = sad.Success ? sad : WorriedWords.Match(text);
        var mood = sad.Success ? "sad" : cue.Success ? "worried" : "hopeful";
        var thanksForPicture = letter.Photo is not null ? " And thank you for the picture, it's going on my workshop wall!" : "";

        string reply;
        if (cue.Success)
        {
            // Gentle, and no "you wished for": the letter may not be about a present at all.
            reply = new StringBuilder($"{name}, thank you for trusting me with your letter.")
                .Append($" It sounds like you're feeling {(mood == "sad" ? "sad" : "a bit worried")}, and it's all right to feel that way.")
                .Append(" Please tell a grown-up who loves you how you feel, and let them give you a big hug.")
                .Append(thanksForPicture)
                .Append(" Mrs Claus, the elves and I are sending you the warmest thoughts from the North Pole.")
                .ToString();
        }
        else
        {
            reply = new StringBuilder(string.Format(Openers[Math.Abs(letter.Day.DayNumber) % Openers.Length], name))
                .Append(wish is null
                    ? " I've read every word of your letter, and I've asked the elves to keep it somewhere very safe."
                    : $" You wished for {ToSecondPerson(wish)}, and I've asked the elves to write that down very carefully.")
                .Append(thanksForPicture)
                .Append(" Be kind, sleep well, and I'll be listening for tomorrow's wish.")
                .ToString();
        }

        var reading = new SantaReading
        {
            SantaReply = reply,
            ParentSummary = wish is null ? $"{name} wrote: \"{Shorten(text, 120)}\"" : $"{name} wished for: {wish}",
            GiftIdeas = wish is null ? [] : [new GiftIdea { Item = wish, Category = "other" }],
            Mood = mood,
            ParentHeadsUp = cue.Success
                ? $"{name} wrote: \"{SentenceAround(text, cue)}\" Santa sent a gentle reply. It may be worth a quiet chat."
                : "",
            DrawingDescription = letter.Photo is not null ? "A drawing was attached." : "",
        };
        return Task.FromResult(new SantaAnswer(reading, "offline", null));
    }

    static string? FindWish(string text)
    {
        var match = WishPhrase.Match(text);
        if (!match.Success) return null;
        var wish = TrailingFiller.Replace(match.Groups["wish"].Value.Trim(), "").TrimEnd(',', ' ');
        if (wish.Length > 60) wish = DanglingWord.Replace(Shorten(wish, 60, ""), "") + ", and more";
        return wish.Length == 0 ? null : wish;
    }

    static string ToSecondPerson(string phrase) =>
        Regex.Replace(phrase, @"\b\w+\b", m => ToYou.TryGetValue(m.Value, out var you) ? you : m.Value);

    static string SentenceAround(string text, Match cue)
    {
        char[] ends = ['.', '!', '?', '\n'];
        var start = cue.Index == 0 ? 0 : text.LastIndexOfAny(ends, cue.Index - 1) + 1;
        var end = text.IndexOfAny(ends, cue.Index + cue.Length);
        return Shorten(text[start..(end < 0 ? text.Length : end + 1)].Trim(), 160);
    }

    static string Shorten(string s, int max, string more = "...")
    {
        if (s.Length <= max) return s;
        var cut = s.LastIndexOf(' ', max);
        return s[..(cut > 0 ? cut : max)].TrimEnd(',', ' ') + more;
    }
}
