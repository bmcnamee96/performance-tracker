using PerformanceTracker.Core.Services;
using PerformanceTracker.Infrastructure.Services;

namespace PerformanceTracker.Desktop;

/// <summary>Display wrapper for one startup entry in the Startup Apps tab.</summary>
public sealed class StartupRowViewModel(StartupEntry entry)
{
    public StartupEntry Entry { get; } = entry;

    public StartupCategory Category { get; } =
        StartupCategorizer.Categorize(entry.Source, entry.Location, entry.Publisher, entry.Recommended);

    public string GroupTitle => StartupCategorizer.Title(Category);

    public string GroupDescription => StartupCategorizer.Description(Category);

    public string Name => Entry.Name;

    public string Explanation => Entry.Explanation;

    public bool Recommended => Entry.Recommended;

    public bool? Enabled => Entry.Enabled;

    public bool CanChange => Entry.CanChange;

    public bool IsRunning => Entry.Running.StartsWith("Running", StringComparison.OrdinalIgnoreCase);

    public string RunningText => IsRunning ? Entry.Running : "Not running";

    public string SourceLine
    {
        get
        {
            string publisher = string.IsNullOrWhiteSpace(Entry.Publisher) || Entry.Publisher == "Unknown" ? "Unknown publisher" : Entry.Publisher;
            return $"{publisher} · {FriendlySource(Entry.Source)}";
        }
    }

    public string LocationToolTip => $"Starts from: {Entry.Location}";

    public string StateText => Entry.Enabled switch
    {
        true => "On",
        false => "Off",
        _ => "Unknown"
    };

    public string ToggleToolTip => CanChange
        ? (Entry.Enabled == true ? "Click to stop it starting with Windows." : "Click to let it start with Windows again.")
        : Entry.ChangeNote ?? "This item can't be changed here.";

    private static string FriendlySource(string source) => source switch
    {
        "Run" or "Run32" => "Startup list",
        "RunOnce" => "Runs once at next sign-in",
        "Startup folder" => "Startup folder",
        _ => source
    };
}
