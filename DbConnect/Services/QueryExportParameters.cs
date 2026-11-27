using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DbConnect.Services;

/// <summary>Copies replayable parameter values without retaining caller-owned mutable containers.</summary>
internal static class QueryExportParameters
{
    public static bool CanCapture(object? value) => CanCapture(value, new HashSet<object>(ReferenceEqualityComparer.Instance));

    private static bool CanCapture(object? value, HashSet<object> visiting)
    {
        if (value is null or string or DBNull or bool or byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal or char or Guid or DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan or JsonElement
            || value.GetType().IsEnum) return true;
        if (value is not Array && !IsList(value)) return false;
        if (!visiting.Add(value)) return false;
        try { return ((IEnumerable)value).Cast<object?>().All(item => CanCapture(item, visiting)); }
        finally { visiting.Remove(value); }
    }

    public static object? Copy(object? value)
    {
        if (!CanCapture(value)) return value; // Ordinary execution may support values not safe to replay.
        if (value is JsonElement element) return element.Clone();
        if (value is Array array)
        {
            var copy = (Array)array.Clone();
            var indices = new int[array.Rank];
            void CopyDimension(int dimension)
            {
                for (var i = array.GetLowerBound(dimension); i <= array.GetUpperBound(dimension); i++)
                {
                    indices[dimension] = i;
                    if (dimension + 1 < array.Rank) CopyDimension(dimension + 1);
                    else copy.SetValue(Copy(array.GetValue(indices)), indices);
                }
            }
            CopyDimension(0);
            return copy;
        }
        if (value != null && IsList(value))
        {
            var copy = (IList)Activator.CreateInstance(value.GetType())!;
            foreach (var item in (IList)value) copy.Add(Copy(item));
            return copy;
        }
        return value;
    }

    private static bool IsList(object value) => value.GetType().IsGenericType && value.GetType().GetGenericTypeDefinition() == typeof(List<>);
}
