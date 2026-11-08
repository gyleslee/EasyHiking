namespace System.Linq;

/// <summary>保持与较新运行时Order/OrderDescending相同的默认比较及稳定排序语义。</summary>
public static class LinqOrderExtensions
{
    public static IOrderedEnumerable<T> Order<T>(this IEnumerable<T> source) => source.OrderBy(value=>value);
    public static IOrderedEnumerable<T> Order<T>(this IEnumerable<T> source,IComparer<T>? comparer) => source.OrderBy(value=>value,comparer);
    public static IOrderedEnumerable<T> OrderDescending<T>(this IEnumerable<T> source) => source.OrderByDescending(value=>value);
    public static Dictionary<TKey,TValue> ToDictionary<TKey,TValue>(this IEnumerable<KeyValuePair<TKey,TValue>> source) where TKey:notnull
        =>source.ToDictionary(value=>value.Key,value=>value.Value);
}
