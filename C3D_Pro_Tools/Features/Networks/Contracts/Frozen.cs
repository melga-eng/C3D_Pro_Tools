using System.Collections.Generic;
using System.Collections.Immutable;

namespace C3DProTools.Features.Networks.Contracts
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
    }
}
