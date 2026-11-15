namespace GitWork.Core;

public static class SearchMatcher
{
    public static bool Matches(string query, params string?[] fields)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return terms.All(term => fields.Any(field => field?.Contains(term, StringComparison.OrdinalIgnoreCase) == true));
    }
}
