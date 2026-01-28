namespace FortniteFestival.LeaderboardScraper.MAUI;

public partial class AppShell : Shell
{
    private const double CompactWidthThreshold = 600;
    private bool _isCompactMode;
    private Grid? _bottomNavBar;
    private static readonly string LogPath = Path.Combine(Microsoft.Maui.Storage.FileSystem.AppDataDirectory, "maui_debug.txt");

    private static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n"); } catch { }
    }

    public AppShell()
    {
        InitializeComponent();
        Log("AppShell constructor");
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        Log("OnLoaded fired");
        
        // Hook into Window size changes instead of Shell
        var window = Application.Current?.Windows.FirstOrDefault();
        if (window != null)
        {
            Log($"Found window, hooking SizeChanged. Window size: {window.Width}x{window.Height}");
            window.SizeChanged += OnWindowSizeChanged;
            // Initial check with window size
            CheckCompactMode(window.Width);
        }
        else
        {
            Log("No window found!");
            UpdateLayoutMode();
        }
    }

    private void OnWindowSizeChanged(object? sender, EventArgs e)
    {
        var window = sender as Window;
        Log($"OnWindowSizeChanged - Width: {window?.Width}");
        CheckCompactMode(window?.Width ?? -1);
    }

    private void CheckCompactMode(double width)
    {
        var newIsCompact = width > 0 && width < CompactWidthThreshold;
        Log($"CheckCompactMode - Width: {width}, newIsCompact: {newIsCompact}, current: {_isCompactMode}");
        if (newIsCompact != _isCompactMode)
        {
            _isCompactMode = newIsCompact;
            UpdateLayoutMode();
        }
    }

    private void OnShellSizeChanged(object? sender, EventArgs e)
    {
        Log($"OnShellSizeChanged - Width: {Width}");
        var newIsCompact = Width > 0 && Width < CompactWidthThreshold;
        if (newIsCompact != _isCompactMode)
        {
            _isCompactMode = newIsCompact;
            UpdateLayoutMode();
        }
    }

    private void UpdateLayoutMode()
    {
        // Don't recalculate - use the _isCompactMode already set by CheckCompactMode
        Log($"UpdateLayoutMode - IsCompact: {_isCompactMode}");

        // In compact mode: disable flyout, show bottom nav
        // In wide mode: enable flyout, hide bottom nav
        FlyoutBehavior = _isCompactMode ? FlyoutBehavior.Disabled : FlyoutBehavior.Flyout;

        EnsureBottomNavBar();
        if (_bottomNavBar != null)
        {
            _bottomNavBar.IsVisible = _isCompactMode;
            Log($"BottomNavBar.IsVisible = {_bottomNavBar.IsVisible}");
        }
        else
        {
            Log("BottomNavBar is NULL!");
        }
    }

    private void EnsureBottomNavBar()
    {
        if (_bottomNavBar != null) return;

        // Create bottom nav bar programmatically
        _bottomNavBar = new Grid
        {
            BackgroundColor = Color.FromArgb("#4B0F63"),
            HeightRequest = 60,
            VerticalOptions = LayoutOptions.End,
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
            }
        };

        AddNavButton(_bottomNavBar, 0, "🎵", "Songs", "//songs");
        AddNavButton(_bottomNavBar, 1, "💡", "Tips", "//suggestions");
        AddNavButton(_bottomNavBar, 2, "📊", "Stats", "//statistics");
        AddNavButton(_bottomNavBar, 3, "⚙️", "Settings", "//settings");

        // Add to the Shell's visual tree by finding/creating a container
        // We need to add this as an overlay to the current page
        AddBottomNavToCurrentPage();
        
        // Re-add when navigation changes
        Navigated += (_, _) => AddBottomNavToCurrentPage();
    }

    private void AddBottomNavToCurrentPage()
    {
        if (_bottomNavBar == null || !_isCompactMode) return;
        
        Log($"AddBottomNavToCurrentPage - CurrentPage: {CurrentPage?.GetType().Name}");
        
        // Don't add bottom nav to detail/pushed pages (like SongInfoPage)
        // Only show on root shell pages
        if (CurrentPage is not ContentPage currentPage)
        {
            Log($"CurrentPage is not ContentPage, it's {CurrentPage?.GetType().Name}");
            return;
        }
        
        // Skip pages that are pushed onto navigation stack (not root pages)
        var pageType = currentPage.GetType().Name;
        if (pageType == "SongInfoPage")
        {
            Log($"Skipping bottom nav for detail page: {pageType}");
            // Hide bottom nav if it was visible
            _bottomNavBar.IsVisible = false;
            return;
        }
        
        Log($"currentPage.Content type: {currentPage.Content?.GetType().Name}");
        
        // Remove from previous parent if any
        if (_bottomNavBar.Parent is Layout oldParent)
        {
            try { oldParent.Children.Remove(_bottomNavBar); } catch { }
        }
        
        // Always wrap content in a grid with the nav bar at the bottom
        var existingContent = currentPage.Content;
        
        // Check if we've already wrapped this page
        if (existingContent is Grid existingGrid && existingGrid.Children.Contains(_bottomNavBar))
        {
            Log("Already wrapped this page");
            _bottomNavBar.IsVisible = true;
            return;
        }
        
        var wrapper = new Grid
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto),
            }
        };
        
        if (existingContent != null)
        {
            Grid.SetRow(existingContent, 0);
            wrapper.Children.Add(existingContent);
        }
        
        Grid.SetRow(_bottomNavBar, 1);
        wrapper.Children.Add(_bottomNavBar);
        currentPage.Content = wrapper;
        
        _bottomNavBar.IsVisible = true;
        Log("Added bottom nav to page");
    }

    private void AddNavButton(Grid parent, int column, string icon, string label, string route)
    {
        var stack = new VerticalStackLayout
        {
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Spacing = 2,
        };
        stack.Children.Add(new Label { Text = icon, FontSize = 20, HorizontalOptions = LayoutOptions.Center });
        stack.Children.Add(new Label { Text = label, FontSize = 10, TextColor = Colors.White, HorizontalOptions = LayoutOptions.Center });

        var container = new Grid { Padding = 8 };
        container.Children.Add(stack);
        container.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () => await GoToAsync(route))
        });

        Grid.SetColumn(container, column);
        parent.Children.Add(container);
    }
}
