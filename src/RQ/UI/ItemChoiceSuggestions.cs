using Franthropy.Dalamud.UI.Inputs;

namespace RQ.UI;

/// <summary>Item-name suggestions for a <see cref="DalamudSuggestionInputRenderer"/>, rebuilt only when the query changes.</summary>
internal sealed class ItemChoiceSuggestions(Func<string, int, IReadOnlyList<ItemChoice>> searchItems, int limit, int minimumLength = 2)
{
    private string query = string.Empty;
    private IReadOnlyList<DalamudSuggestion<ItemChoice>> suggestions = [];

    public IReadOnlyList<DalamudSuggestion<ItemChoice>> For(string text)
    {
        var needle = text.Trim();
        if (string.Equals(needle, query, StringComparison.Ordinal))
            return suggestions;
        query = needle;
        suggestions = needle.Length < minimumLength
            ? []
            : searchItems(needle, limit).Select(choice => new DalamudSuggestion<ItemChoice>(choice, choice.Label)).ToArray();
        return suggestions;
    }
}
