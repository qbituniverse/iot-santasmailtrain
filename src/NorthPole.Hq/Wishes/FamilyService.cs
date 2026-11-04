using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NorthPole.Contracts;

namespace NorthPole.Hq.Wishes;

/// <summary>
/// Each child gets a short secret code that goes into their QR card (and LEGO mosaic), e.g. http://np.local/W/K7Q2.
/// Codes live in data/family.json so printed cards keep working across restarts.
/// </summary>
public sealed class FamilyService
{
    // No 0/O or 1/I: easier to read off a card if someone has to type it.
    const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    const int CodeLength = 4;

    readonly HqOptions _o;
    readonly string _file;
    readonly Lock _lock = new();
    readonly FamilyState _state;

    public FamilyService(IOptions<HqOptions> options, ILogger<FamilyService> log)
    {
        _o = options.Value;
        var dir = Path.GetFullPath(_o.DataDirectory);
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "family.json");
        _state = File.Exists(_file)
            ? JsonSerializer.Deserialize<FamilyState>(File.ReadAllText(_file), NorthPoleJson.Options) ?? new()
            : new();

        foreach (var child in _o.Family.Where(c => !_state.Codes.ContainsKey(c.Id)))
        {
            _state.Codes[child.Id] = NewCode();
            log.LogInformation("New QR code for {Child}: {Code}", child.Name, _state.Codes[child.Id]);
        }
        Save();
    }

    public IReadOnlyList<ChildOptions> Children => _o.Family;

    public ChildOptions? FindByCode(string code)
    {
        lock (_lock)
        {
            var id = _state.Codes.FirstOrDefault(kv => string.Equals(kv.Value, code, StringComparison.OrdinalIgnoreCase)).Key;
            return id is null ? null : _o.Family.FirstOrDefault(c => c.Id == id);
        }
    }

    public ChildOptions? FindById(string id) => _o.Family.FirstOrDefault(c => c.Id == id);

    public string CodeFor(string childId)
    {
        lock (_lock) return _state.Codes[childId];
    }

    /// <summary>Lost card? Make a new code (and rebuild the LEGO mosaic, sorry).</summary>
    public string RegenerateCode(string childId)
    {
        lock (_lock)
        {
            _state.Codes[childId] = NewCode();
            Save();
            return _state.Codes[childId];
        }
    }

    public int ExtraWishesToday(string childId, DateOnly today)
    {
        lock (_lock)
        {
            return _state.ExtraWishes.TryGetValue(childId, out var grant) && grant.Day == today ? grant.Count : 0;
        }
    }

    public void GrantExtraWish(string childId, DateOnly today)
    {
        lock (_lock)
        {
            var count = _state.ExtraWishes.TryGetValue(childId, out var grant) && grant.Day == today ? grant.Count + 1 : 1;
            _state.ExtraWishes[childId] = new ExtraWishGrant(today, count);
            Save();
        }
    }

    string NewCode()
    {
        string code;
        do
        {
            code = string.Create(CodeLength, 0, (span, _) =>
            {
                for (var i = 0; i < span.Length; i++) span[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            });
        } while (_state.Codes.ContainsValue(code));
        return code;
    }

    void Save() => File.WriteAllText(_file, JsonSerializer.Serialize(_state, NorthPoleJson.Options));

    sealed class FamilyState
    {
        public Dictionary<string, string> Codes { get; set; } = [];
        public Dictionary<string, ExtraWishGrant> ExtraWishes { get; set; } = [];
    }

    sealed record ExtraWishGrant(DateOnly Day, int Count);
}
