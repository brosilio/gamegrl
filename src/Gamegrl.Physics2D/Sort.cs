// Ported from Box2D (https://github.com/erincatto/box2d), Copyright (c) 2022 Erin Catto, under the MIT License;
// see THIRD_PARTY_NOTICES.md.
using System.Numerics;

namespace Gamegrl.Physics2D;

/// <summary>In-place heapsort: O(n log n), no allocation. (The BCL's span sorts allocate under NativeAOT.)</summary>
internal static class Sort
{
    public static void Heap<T>(Span<T> a) where T : struct, IComparisonOperators<T, T, bool>
    {
        for (int i = a.Length / 2 - 1; i >= 0; i--) Down(a, i, a.Length);
        for (int end = a.Length - 1; end > 0; end--)
        {
            (a[0], a[end]) = (a[end], a[0]);
            Down(a, 0, end);
        }
    }

    private static void Down<T>(Span<T> a, int i, int n) where T : struct, IComparisonOperators<T, T, bool>
    {
        while (true)
        {
            int child = 2 * i + 1;
            if (child >= n) return;
            if (child + 1 < n && a[child + 1] > a[child]) child++;
            if (!(a[child] > a[i])) return;
            (a[i], a[child]) = (a[child], a[i]);
            i = child;
        }
    }
}
