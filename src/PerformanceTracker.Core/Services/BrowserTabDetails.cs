using System.Text.RegularExpressions;

namespace PerformanceTracker.Core.Services;

public static partial class BrowserTabDetails
{
    [GeneratedRegex(@" - Memory usage - (?<amount>[0-9]+(?:[.,][0-9]+)? (?:KB|MB|GB))$",
        RegexOptions.CultureInvariant)]
    private static partial Regex MemorySuffix();

    public static (string Title, string? Memory) ReadName(string name)
    {
        string title = name.Trim();
        Match memory = MemorySuffix().Match(title);
        if (memory.Success)
        {
            title = title[..memory.Index].Trim();
        }

        return (title.Length == 0 ? "Untitled tab" : title, memory.Success ? memory.Groups["amount"].Value : null);
    }

    public static string CleanTitle(string name) => ReadName(name).Title;

    public static bool Matches(string expectedName, IReadOnlyList<int> expectedId, string name, IReadOnlyList<int> id) =>
        string.Equals(CleanTitle(expectedName), CleanTitle(name), StringComparison.Ordinal) && expectedId.SequenceEqual(id);

    public static Dictionary<nint, int> NumberWindows(IEnumerable<nint> handles) =>
        handles.Distinct().Order().Select((handle, index) => (handle, Number: index + 1))
            .ToDictionary(window => window.handle, window => window.Number);
}
