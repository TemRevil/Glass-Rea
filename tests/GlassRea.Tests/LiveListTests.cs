using System.Collections.Specialized;
using GlassRea.Core.Look;

namespace GlassRea.Tests;

public class LiveListTests
{
    private sealed record R(int Id, string Total);
    private sealed class Item : Live<R>;

    private static (LiveList<Item, R> List, List<NotifyCollectionChangedAction> Changes) Make(params R[] rows)
    {
        var list = new LiveList<Item, R>(r => r.Id);
        list.Sync(rows);
        var changes = new List<NotifyCollectionChangedAction>();
        list.CollectionChanged += (_, e) => changes.Add(e.Action);
        return (list, changes);
    }

    [Fact]
    public void AChangedFigureUpdatesOnlyItsRow()
    {
        var (list, changes) = Make(new(1, "5 MB"), new(2, "3 MB"), new(3, "1 MB"));
        var items = list.ToList();
        var touched = new List<int>();
        foreach (var item in items) item.PropertyChanged += (_, _) => touched.Add(item.Row.Id);

        list.Sync([new(1, "5 MB"), new(2, "4 MB"), new(3, "1 MB")]);

        Assert.Empty(changes);                       // the list itself didn't change
        Assert.Equal(items, list.ToList());          // the same row objects
        Assert.Equal([2], touched);                  // and only row 2 was told
        Assert.Equal("4 MB", list[1].Row.Total);
    }

    [Fact]
    public void RowsArriveLeaveAndMoveWithoutAReset()
    {
        var (list, changes) = Make(new(1, "a"), new(2, "b"), new(3, "c"));
        var two = list[1];

        list.Sync([new(2, "b2"), new(4, "d"), new(1, "a")]);

        Assert.Equal([2, 4, 1], list.Select(i => i.Row.Id));
        Assert.Same(two, list[0]);
        Assert.Equal("b2", list[0].Row.Total);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
        Assert.Contains(NotifyCollectionChangedAction.Remove, changes);
        Assert.Contains(NotifyCollectionChangedAction.Add, changes);
        Assert.Contains(NotifyCollectionChangedAction.Move, changes);
    }

    [Fact]
    public void RowsSharingAKeyAreKeptApart()
    {
        var (list, _) = Make(new(7, "x"), new(7, "y"));
        list.Sync([new(7, "x"), new(7, "y"), new(7, "z")]);
        Assert.Equal(["x", "y", "z"], list.Select(i => i.Row.Total));
        list.Sync([new(7, "q")]);
        Assert.Equal(["q"], list.Select(i => i.Row.Total));
    }

    [Fact]
    public void EmptyingAndRefilling()
    {
        var (list, _) = Make(new R(1, "a"));
        list.Sync([]);
        Assert.Empty(list);
        list.Sync([new(2, "b"), new(3, "c")]);
        Assert.Equal([2, 3], list.Select(i => i.Row.Id));
    }
}
