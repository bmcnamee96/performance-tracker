using PerformanceTracker.Core.Services;
using Xunit;

namespace PerformanceTracker.Tests;

public sealed class StartupCategorizerTests
{
    [Theory]
    [InlineData("Automatic service", @"C:\Program Files\Vendor\svc.exe", "Vendor", false, StartupCategory.ServicesAndTasks)]
    [InlineData("Logon task", @"C:\Program Files\Vendor\task.exe", "Vendor", false, StartupCategory.ServicesAndTasks)]
    [InlineData("Run", @"C:\Program Files\Realtek\rtk.exe", "Realtek", true, StartupCategory.WindowsAndDrivers)]
    [InlineData("Run", @"C:\Windows\System32\SecurityHealthSystray.exe", "Microsoft Corporation", false, StartupCategory.WindowsAndDrivers)]
    [InlineData("Run", @"C:\Users\me\AppData\Local\Discord\Update.exe", "GitHub", false, StartupCategory.YourApps)]
    [InlineData("Startup folder", @"C:\Program Files\Spotify\Spotify.exe", "Spotify AB", false, StartupCategory.YourApps)]
    [InlineData("Run", @"C:\Users\me\AppData\Local\Microsoft\Teams\ms-teams.exe", "Microsoft Corporation", false, StartupCategory.YourApps)]
    public void SortsEntriesIntoSections(string source, string location, string publisher, bool recommended, StartupCategory expected)
    {
        Assert.Equal(expected, StartupCategorizer.Categorize(source, location, publisher, recommended));
    }

    [Fact]
    public void EverySectionHasPlainTitleAndDescription()
    {
        foreach (StartupCategory category in Enum.GetValues<StartupCategory>())
        {
            Assert.False(string.IsNullOrWhiteSpace(StartupCategorizer.Title(category)));
            Assert.False(string.IsNullOrWhiteSpace(StartupCategorizer.Description(category)));
        }
    }
}
