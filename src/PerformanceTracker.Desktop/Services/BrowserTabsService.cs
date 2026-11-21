using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using PerformanceTracker.Core.Services;

namespace PerformanceTracker.Desktop.Services;

internal sealed record BrowserTab(
    nint Window, int ProcessId, AutomationElement Element, int[] RuntimeId, string Name,
    bool Selected, bool CanClose, bool IsGroup, int WindowNumber, bool ShowWindowNumber)
{
    public string Title => IsGroup ? $"{BrowserTabDetails.CleanTitle(Name)} (collapsed group)" : BrowserTabDetails.CleanTitle(Name);
    public string Detail => string.Join(" · ", new[]
    {
        ShowWindowNumber ? $"Window {WindowNumber}" : null,
        IsGroup ? null : BrowserTabDetails.ReadName(Name).Memory,
        IsGroup ? "Tabs are hidden" : null
    }.OfType<string>());
    public string CloseHint => CanClose ? "Close this tab" : "The browser does not expose an available close button for this tab.";
}

internal sealed class BrowserTabsService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private static readonly Condition Buttons = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
    private readonly ConcurrentDictionary<nint, Task<BrowserTab[]>> _pending = new();

    public async Task<(BrowserTab[] Tabs, string Status)> ReadAsync(IReadOnlyList<int> processIds)
    {
        Dictionary<nint, int> windows = [];
        _ = EnumWindows((handle, _) =>
        {
            if (IsWindowVisible(handle) && processIds.Contains(WindowProcess(handle)))
            {
                char[] className = new char[64];
                int length = GetClassName(handle, className, className.Length);
                if (className.AsSpan(0, length).SequenceEqual("Chrome_WidgetWin_1"))
                {
                    windows.Add(handle, WindowProcess(handle));
                }
            }

            return true;
        }, 0);

        Dictionary<nint, int> numbers = BrowserTabDetails.NumberWindows(windows.Keys);
        BrowserTab[]?[] results = await Task.WhenAll(numbers.Select(async window =>
        {
            nint handle = window.Key;
            Task<BrowserTab[]> task = _pending.GetOrAdd(handle,
                _ => Task.Run(() => ReadWindow(handle, windows[handle])));
            try
            {
                BrowserTab[] tabs = await task.WaitAsync(Timeout).ConfigureAwait(false);
                return tabs.Length == 0 ? null : tabs.Select(tab => tab with
                {
                    WindowNumber = window.Value,
                    ShowWindowNumber = windows.Count > 1
                }).ToArray();
            }
            catch (Exception ex) when (ex is TimeoutException || IsUiaFailure(ex))
            {
                return null;
            }
            finally
            {
                if (task.IsCompleted)
                {
                    _pending.TryRemove(new KeyValuePair<nint, Task<BrowserTab[]>>(handle, task));
                }
            }
        })).ConfigureAwait(false);

        BrowserTab[] tabs = results.OfType<BrowserTab[]>().SelectMany(items => items).ToArray();
        return (tabs, tabs.Length == 0 ? "No open tabs were found. Minimized windows may hide their tabs."
            : results.Any(items => items is null) ? "Some browser windows did not expose their tabs." : string.Empty);
    }

    public static async Task<string> CloseAsync(BrowserTab tab)
    {
        using CancellationTokenSource deadline = new(Timeout);
        CancellationToken token = deadline.Token;
        Task<string> request = Task.Run(() =>
        {
            try
            {
                if (!IsWindowVisible(tab.Window) || WindowProcess(tab.Window) != tab.ProcessId ||
                    !IsInWindow(tab) ||
                    !BrowserTabDetails.Matches(tab.Name, tab.RuntimeId, tab.Element.Current.Name, tab.Element.GetRuntimeId()) ||
                    GetCloseButton(tab.Element) is not InvokePattern close ||
                    !BrowserTabDetails.Matches(tab.Name, tab.RuntimeId, tab.Element.Current.Name, tab.Element.GetRuntimeId()))
                {
                    return "The tab changed or its close button is unavailable. Refresh and try again.";
                }

                token.ThrowIfCancellationRequested();
                close.Invoke();
                return "Close requested. The browser may ask you to confirm.";
            }
            catch (Exception ex) when (IsUiaFailure(ex))
            {
                return "The browser could not close that tab.";
            }
        }, token);

        try
        {
            return await request.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return "The browser did not respond in time. Check whether the tab is still open.";
        }
    }

    private static BrowserTab[] ReadWindow(nint window, int pid)
    {
        AutomationElement root = AutomationElement.FromHandle(window);
        List<BrowserTab> tabs = [];
        // Walk browser chrome only. Web content can contain its own accessible Tab controls.
        VisitChrome(root, strip: false);
        return [.. tabs];

        void VisitChrome(AutomationElement parent, bool strip)
        {
            TreeWalker walker = TreeWalker.ControlViewWalker;
            for (AutomationElement? child = walker.GetFirstChild(parent); child is not null; child = walker.GetNextSibling(child))
            {
                ControlType type = child.Current.ControlType;
                if (type == ControlType.Document || type == ControlType.ToolBar)
                {
                    continue;
                }

                if (strip && type == ControlType.TabItem)
                {
                    bool selected = child.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object pattern) &&
                        ((SelectionItemPattern)pattern).Current.IsSelected;
                    tabs.Add(new BrowserTab(window, pid, child, child.GetRuntimeId(), child.Current.Name,
                        selected, GetCloseButton(child) is not null, false, 0, false));
                    continue;
                }

                if (strip && child.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object expand) &&
                    ((ExpandCollapsePattern)expand).Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
                {
                    tabs.Add(new BrowserTab(window, pid, child, child.GetRuntimeId(), child.Current.Name, false, false, true, 0, false));
                    continue;
                }

                if (type != ControlType.Button)
                {
                    VisitChrome(child, strip || type == ControlType.Tab);
                }
            }
        }
    }

    private static bool IsInWindow(BrowserTab tab)
    {
        TreeWalker walker = TreeWalker.ControlViewWalker;
        for (AutomationElement? parent = walker.GetParent(tab.Element); parent is not null; parent = walker.GetParent(parent))
        {
            if (parent.Current.NativeWindowHandle == (int)tab.Window)
            {
                return true;
            }
        }

        return false;
    }

    private static InvokePattern? GetCloseButton(AutomationElement tab)
    {
        foreach (AutomationElement button in tab.FindAll(TreeScope.Children, Buttons))
        {
            if (button.Current.IsEnabled && (button.Current.Name is "Close" or "Close tab") &&
                button.TryGetCurrentPattern(InvokePattern.Pattern, out object pattern))
            {
                return (InvokePattern)pattern;
            }
        }

        return null;
    }

    private static bool IsUiaFailure(Exception ex) =>
        ex is InvalidOperationException or COMException or UnauthorizedAccessException or ArgumentException;

    private static int WindowProcess(nint handle)
    {
        _ = GetWindowThreadProcessId(handle, out int pid);
        return pid;
    }

    private delegate bool EnumWindowsCallback(nint handle, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint handle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint handle, [Out] char[] name, int capacity);
}
