using System.Collections.ObjectModel;
using System.ComponentModel;

namespace GlassRea.Core.Look;

/// <summary>
/// One row of a list that refreshes in place: the item stays the same object for as long as its row is in the list,
/// and only <see cref="Row"/> changes, so a list on screen updates the figures that moved instead of being rebuilt.
/// </summary>
public abstract class Live<T> : INotifyPropertyChanged where T : class
{
    private T _row = null!;

    public T Row
    {
        get => _row;
        set
        {
            if (EqualityComparer<T>.Default.Equals(_row, value)) return;
            _row = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Row)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// A list shown on screen that takes each refresh's rows and changes only what differs: rows that left are removed,
/// new ones inserted where they belong, moved ones moved, and rows that stayed get their new values. It never resets,
/// so the list keeps its scroll position, its selection and every row that didn't change.
/// </summary>
/// <param name="key">What identifies a row across refreshes (an app's id, a device's address).</param>
public sealed class LiveList<TItem, TRow>(Func<TRow, object> key) : ObservableCollection<TItem>
    where TItem : Live<TRow>, new()
    where TRow : class
{
    public void Sync(IReadOnlyList<TRow> rows)
    {
        // Two rows with the same key are told apart by which of them it is, in order.
        var keys = new List<(object, int)>(rows.Count);
        var seen = new Dictionary<object, int>();
        foreach (var row in rows)
        {
            var k = key(row);
            var n = seen.GetValueOrDefault(k);
            seen[k] = n + 1;
            keys.Add((k, n));
        }
        var wanted = keys.ToHashSet();
        var current = Keys();
        for (var i = Count - 1; i >= 0; i--)
            if (!wanted.Contains(current[i])) { RemoveAt(i); current.RemoveAt(i); }

        for (var i = 0; i < rows.Count; i++)
        {
            var at = current.IndexOf(keys[i], i);
            if (at < 0)
            {
                Insert(i, new TItem { Row = rows[i] });
                current.Insert(i, keys[i]);
                continue;
            }
            if (at != i)
            {
                Move(at, i);
                var k = current[at];
                current.RemoveAt(at);
                current.Insert(i, k);
            }
            this[i].Row = rows[i];
        }
    }

    private List<(object, int)> Keys()
    {
        var list = new List<(object, int)>(Count);
        var seen = new Dictionary<object, int>();
        foreach (var item in this)
        {
            var k = key(item.Row);
            var n = seen.GetValueOrDefault(k);
            seen[k] = n + 1;
            list.Add((k, n));
        }
        return list;
    }
}
