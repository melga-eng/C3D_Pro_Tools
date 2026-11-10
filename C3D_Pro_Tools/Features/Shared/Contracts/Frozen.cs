using System.Collections.Generic;
using System.Collections.Immutable;

namespace C3DProTools.Features.Shared.Contracts
{
    /// <summary>
    /// Copies a collection into immutable storage. Contracts store every list through it (in constructors and in init
    /// accessors, so <c>with</c> copies are frozen too): a plan cannot change after the preview the user reviewed, whatever
    /// the caller later does with the list it passed in.
    /// </summary>
    internal static class Frozen
    {
        public static IReadOnlyList<T> List<T>(IEnumerable<T> items) => items == null ? ImmutableArray<T>.Empty : ImmutableArray.CreateRange(items);

        /// <summary>A frozen copy of a map, its keys compared ordinally, ignoring case.</summary>
        public static IReadOnlyDictionary<string, double> Map(IEnumerable<KeyValuePair<string, double>> items) =>
            items == null ? ImmutableDictionary<string, double>.Empty.WithComparers(System.StringComparer.OrdinalIgnoreCase)
                          : ImmutableDictionary.CreateRange(System.StringComparer.OrdinalIgnoreCase, items);

        /// <summary>A frozen copy of a map with non-string keys (enums, numbers); null gives an empty map.</summary>
        public static IReadOnlyDictionary<TKey, TValue> Dictionary<TKey, TValue>(IEnumerable<KeyValuePair<TKey, TValue>> items) =>
            items == null ? ImmutableDictionary<TKey, TValue>.Empty : ImmutableDictionary.CreateRange(items);

        /// <summary>A frozen copy of a set of names, compared ordinally, ignoring case; null gives an empty set.</summary>
        public static IReadOnlySet<string> Names(IEnumerable<string> items) =>
            items == null ? ImmutableHashSet<string>.Empty.WithComparer(System.StringComparer.OrdinalIgnoreCase)
                          : ImmutableHashSet.CreateRange(System.StringComparer.OrdinalIgnoreCase, items);

        /// <summary>A frozen copy of a set of values (enums); null gives an empty set.</summary>
        public static IReadOnlySet<T> Set<T>(IEnumerable<T> items) => items == null ? ImmutableHashSet<T>.Empty : ImmutableHashSet.CreateRange(items);
    }
}
