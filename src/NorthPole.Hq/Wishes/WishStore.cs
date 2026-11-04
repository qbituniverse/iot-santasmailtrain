using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using NorthPole.Contracts;

namespace NorthPole.Hq.Wishes;

/// <summary>
/// All wishes in one JSON file. A family sends a few dozen letters a December, so a database would be
/// overkill; this file is easy to back up, read and keep as a memento.
/// All access goes through a lock; readers get projections, never the live objects.
/// </summary>
public sealed class WishStore
{
    readonly string _file;
    readonly Lock _lock = new();
    readonly List<Wish> _wishes;
    readonly ILogger<WishStore> _log;

    public WishStore(IOptions<HqOptions> options, ILogger<WishStore> log)
    {
        _log = log;
        var dir = Path.GetFullPath(options.Value.DataDirectory);
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "wishes.json");
        _wishes = File.Exists(_file)
            ? JsonSerializer.Deserialize<List<Wish>>(File.ReadAllText(_file), NorthPoleJson.Options) ?? []
            : [];
        _log.LogInformation("Loaded {Count} wishes from {File}", _wishes.Count, _file);
    }

    /// <summary>Raised (outside the lock) with the id of the wish that changed.</summary>
    public event Action<Guid>? Changed;

    public void Add(Wish wish)
    {
        lock (_lock)
        {
            _wishes.Add(wish);
            Save();
        }
        Changed?.Invoke(wish.Id);
    }

    public bool Update(Guid id, Action<Wish> mutate)
    {
        lock (_lock)
        {
            var wish = _wishes.Find(w => w.Id == id);
            if (wish is null) return false;
            mutate(wish);
            Save();
        }
        Changed?.Invoke(id);
        return true;
    }

    public T? Read<T>(Guid id, Func<Wish, T> project) where T : class?
    {
        lock (_lock)
        {
            var wish = _wishes.Find(w => w.Id == id);
            return wish is null ? null : project(wish);
        }
    }

    public List<T> Query<T>(Func<IEnumerable<Wish>, IEnumerable<T>> query)
    {
        lock (_lock) return query(_wishes).ToList();
    }

    /// <summary>
    /// Streams a fresh projection of one wish every time it changes, until <paramref name="isFinal"/> says stop.
    /// Feeds the Server-Sent Events endpoint the kids' page listens to.
    /// </summary>
    public async IAsyncEnumerable<T> WatchAsync<T>(Guid id, Func<Wish, T> project, Func<T, bool> isFinal,
        [EnumeratorCancellation] CancellationToken ct = default) where T : class
    {
        var signal = Channel.CreateUnbounded<bool>();
        void OnChanged(Guid changed)
        {
            if (changed == id) signal.Writer.TryWrite(true);
        }

        Changed += OnChanged;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var view = Read(id, project);
                if (view is null) yield break;
                yield return view;
                if (isFinal(view)) yield break;

                await signal.Reader.WaitToReadAsync(ct);
                while (signal.Reader.TryRead(out _)) { } // collapse bursts into one update
            }
        }
        finally
        {
            Changed -= OnChanged;
        }
    }

    void Save()
    {
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_wishes, NorthPoleJson.Options));
        File.Move(tmp, _file, overwrite: true);
    }
}
