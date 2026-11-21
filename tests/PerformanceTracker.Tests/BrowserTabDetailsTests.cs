using PerformanceTracker.Core.Services;
using Xunit;

namespace PerformanceTracker.Tests;

public sealed class BrowserTabDetailsTests
{
    [Theory]
    [InlineData("  Inbox  ", "Inbox")]
    [InlineData(" ", "Untitled tab")]
    [InlineData("Site - 20 MB", "Site - 20 MB")]
    public void CleansTitlesWithoutRemovingPageText(string name, string expected) =>
        Assert.Equal(expected, BrowserTabDetails.CleanTitle(name));

    [Theory]
    [InlineData("Inbox - Memory usage - 250 MB", "Inbox", "250 MB")]
    [InlineData("Inbox - Memory usage - 1.2 GB", "Inbox", "1.2 GB")]
    [InlineData("Inbox - Memory usage - unknown", "Inbox - Memory usage - unknown", null)]
    public void ReadsChromiumMemorySuffix(string name, string title, string? memory) =>
        Assert.Equal((title, memory), BrowserTabDetails.ReadName(name));

    [Fact]
    public void MatchesBothIdentityAndTitle()
    {
        Assert.True(BrowserTabDetails.Matches("Inbox", [1, 2], "Inbox", [1, 2]));
        Assert.False(BrowserTabDetails.Matches("Inbox", [1, 2], "Inbox", [1, 3]));
        Assert.False(BrowserTabDetails.Matches("Inbox", [1, 2], "Other", [1, 2]));
        Assert.True(BrowserTabDetails.Matches("Inbox - Memory usage - 50 MB", [1, 2], "Inbox - Memory usage - 60 MB", [1, 2]));
    }

    [Fact]
    public void NumbersDistinctWindowsInHandleOrder()
    {
        var windows = BrowserTabDetails.NumberWindows([20, 10, 20]);
        Assert.Equal(2, windows.Count);
        Assert.Equal(1, windows[10]);
        Assert.Equal(2, windows[20]);
    }
}
