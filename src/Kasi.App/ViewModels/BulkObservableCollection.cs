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
        if (Count == items.Count && items.Select((item, i) => ReferenceEquals(item, this[i])).All(same => same))
        {
            return;
        }

        var wanted = new HashSet<T>(items, ReferenceEqualityComparer.Instance as IEqualityComparer<T>);
        var present = new HashSet<T>(this, ReferenceEqualityComparer.Instance as IEqualityComparer<T>);
        var removals = this.Count(item => !wanted.Contains(item));
        var additions = items.Count(item => !present.Contains(item));
        if (removals + additions <= MaxIndividualChanges && removals + additions > 0 && AlreadyInOrder(items, present))
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
    private bool AlreadyInOrder(IReadOnlyList<T> items, HashSet<T> present)
    {
        var kept = items.Where(present.Contains).ToList();
        var current = this.Where(item => kept.Contains(item)).ToList();
        return kept.Count == current.Count && kept.Zip(current).All(pair => ReferenceEquals(pair.First, pair.Second));
    }
}
