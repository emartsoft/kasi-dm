using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Kasi.App.ViewModels;

/// <summary>
/// An observable list that can take a whole new content with one change notification. The grid keeps its sort and its scroll position
/// across a change to the same collection (it would lose both if the list were replaced by another object), and a handful of changes
/// are applied one by one so a selection survives a row that merely moved in or out of a filter.
/// </summary>
internal sealed class BulkObservableCollection<T> : ObservableCollection<T>
    where T : class
{
    private const int MaxIndividualChanges = 64;

    /// <summary>Makes the content equal to <paramref name="items"/>, in that order.</summary>
    public void ReplaceAll(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (Count == items.Count)
        {
            var same = true;
            for (var i = 0; i < Count; i++)
            {
                if (!ReferenceEquals(this[i], items[i]))
                {
                    same = false;
                    break;
                }
            }

            if (same)
            {
                return;
            }
        }

        if (Math.Abs(Count - items.Count) > MaxIndividualChanges)
        {
            ResetTo(items);
            return;
        }

        var wanted = new HashSet<T>(items, ReferenceEqualityComparer.Instance as IEqualityComparer<T>);
        var present = new HashSet<T>(this, ReferenceEqualityComparer.Instance as IEqualityComparer<T>);
        var removals = 0;
        for (var i = 0; i < Count; i++)
        {
            if (!wanted.Contains(this[i]))
            {
                removals++;
            }
        }

        var additions = 0;
        for (var i = 0; i < items.Count; i++)
        {
            if (!present.Contains(items[i]))
            {
                additions++;
            }
        }

        if (removals + additions <= MaxIndividualChanges && removals + additions > 0 && AlreadyInOrder(items, present, wanted))
        {
            for (var i = Count - 1; i >= 0; i--)
            {
                if (!wanted.Contains(this[i]))
                {
                    RemoveAt(i);
                }
            }

            for (var i = 0; i < items.Count; i++)
            {
                if (i >= Count || !ReferenceEquals(this[i], items[i]))
                {
                    Insert(i, items[i]);
                }
            }

            return;
        }

        ResetTo(items);
    }

    private void ResetTo(IReadOnlyList<T> items)
    {
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    // the one-by-one path assumes what stays keeps its relative order
    private bool AlreadyInOrder(IReadOnlyList<T> items, HashSet<T> present, HashSet<T> wanted)
    {
        var i = 0;
        var j = 0;
        while (i < Count && j < items.Count)
        {
            if (!wanted.Contains(this[i]))
            {
                i++;
                continue;
            }

            if (!present.Contains(items[j]))
            {
                j++;
                continue;
            }

            if (!ReferenceEquals(this[i], items[j]))
            {
                return false;
            }

            i++;
            j++;
        }

        while (i < Count && !wanted.Contains(this[i]))
        {
            i++;
        }

        while (j < items.Count && !present.Contains(items[j]))
        {
            j++;
        }

        return i == Count && j == items.Count;
    }
}
