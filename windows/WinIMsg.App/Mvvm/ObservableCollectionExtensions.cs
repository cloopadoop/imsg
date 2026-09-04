using System.Collections.ObjectModel;

namespace WinIMsg.App.Mvvm;

public static class ObservableCollectionExtensions
{
    public static void ReplaceWith<T>(this ObservableCollection<T> collection, IEnumerable<T> items)
    {
        var replacement = items as IReadOnlyList<T> ?? items.ToList();
        var comparer = EqualityComparer<T>.Default;

        if (collection.Count == replacement.Count)
        {
            var hasDifference = false;
            for (var index = 0; index < replacement.Count; index++)
            {
                if (!comparer.Equals(collection[index], replacement[index]))
                {
                    hasDifference = true;
                    break;
                }
            }

            if (!hasDifference)
            {
                return;
            }
        }

        var prependedCount = replacement.Count - collection.Count;
        if (prependedCount > 0 && EndsWithExistingItems(collection, replacement, prependedCount, comparer))
        {
            for (var index = prependedCount - 1; index >= 0; index--)
            {
                collection.Insert(0, replacement[index]);
            }

            return;
        }

        var commonCount = Math.Min(collection.Count, replacement.Count);
        for (var index = 0; index < commonCount; index++)
        {
            if (!comparer.Equals(collection[index], replacement[index]))
            {
                collection[index] = replacement[index];
            }
        }

        while (collection.Count > replacement.Count)
        {
            collection.RemoveAt(collection.Count - 1);
        }

        for (var index = collection.Count; index < replacement.Count; index++)
        {
            collection.Add(replacement[index]);
        }
    }

    private static bool EndsWithExistingItems<T>(
        ObservableCollection<T> collection,
        IReadOnlyList<T> replacement,
        int prependedCount,
        EqualityComparer<T> comparer)
    {
        for (var index = 0; index < collection.Count; index++)
        {
            if (!comparer.Equals(collection[index], replacement[index + prependedCount]))
            {
                return false;
            }
        }

        return true;
    }
}
