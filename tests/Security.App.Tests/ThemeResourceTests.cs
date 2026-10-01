using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Xml;
using Security.Core.Paths;
using Xunit;

namespace Security.App.Tests;

/// <summary>
/// Guards the theme dictionary itself.
///
/// <c>StaticResource</c> resolves strictly top to bottom, so a Style whose
/// <c>BasedOn</c> names a key defined further down the same file cannot be
/// built. That one mistake silently truncated everything after it: the app
/// launched, showed a plausible window, and threw an unhandled UI exception on
/// every single layout pass (3,704 times in one 20-second launch) while the
/// camera page's status badges, the checkbox style, and everything in between
/// simply did not exist.
///
/// Nothing in the unit suites touches XAML, so without this test the whole
/// class of bug is invisible until somebody runs the program by hand.
/// </summary>
public class ThemeResourceTests
{
    /// <summary>Styles that were unreachable when the failure above existed.</summary>
    private static readonly string[] MustBePresent =
    {
        "Badge",
        "StatusBadge",
        "StatusBadge.Live",
        "StatusBadge.Connecting",
        "StatusBadge.Offline",
        "StatusBadge.Error",
        "StatusBadge.Caption",
        "StatusBadge.Dot",
        "Input.CheckBox",
        "StatusDot",
        "Overlay.Box",
    };

    private static string ThemePath()
    {
        var root = AppPaths.RepositoryRootDirectory;
        Assert.False(string.IsNullOrWhiteSpace(root), "repository root could not be resolved");

        var path = Path.Combine(root!, "src", "Security.App", "Styles", "DarkTheme.xaml");
        Assert.True(File.Exists(path), $"theme file is missing: {path}");
        return path;
    }

    /// <summary>
    /// Load the theme exactly the way WPF does. A forward reference throws
    /// here, and a dictionary that failed part-way comes back missing the
    /// keys it never reached.
    /// </summary>
    private static ResourceDictionary LoadTheme()
    {
        ResourceDictionary? loaded = null;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                using var reader = XmlReader.Create(ThemePath());
                loaded = XamlReader.Load(reader) as ResourceDictionary
                         ?? throw new InvalidOperationException(
                             "DarkTheme.xaml did not produce a ResourceDictionary.");
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "parsing DarkTheme.xaml timed out");
        Assert.True(failure is null, $"DarkTheme.xaml failed to parse:\n{failure}");
        Assert.NotNull(loaded);
        return loaded!;
    }

    [Fact]
    public void Theme_parses_from_start_to_finish()
        => _ = LoadTheme();

    [Fact]
    public void Every_style_defined_past_the_historical_failure_point_is_reachable()
    {
        var theme = LoadTheme();

        foreach (var key in MustBePresent)
            Assert.True(theme.Contains(key), $"DarkTheme.xaml is missing '{key}' — the dictionary was truncated part-way.");
    }

    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("Views/DashboardView.xaml")]
    [InlineData("Views/CameraView.xaml")]
    [InlineData("Views/FaceProfileView.xaml")]
    [InlineData("Views/EventsView.xaml")]
    [InlineData("Views/SettingsView.xaml")]
    [InlineData("Views/AboutView.xaml")]
    public void A_view_never_asks_for_a_resource_that_does_not_exist(string relative)
    {
        var root = AppPaths.RepositoryRootDirectory!;
        var viewPath = Path.Combine(root, "src", "Security.App", relative);
        Assert.True(File.Exists(viewPath), $"missing view: {viewPath}");

        var view = File.ReadAllText(viewPath);
        var app = File.ReadAllText(Path.Combine(root, "src", "Security.App", "App.xaml"));

        // Theme keys, plus anything the view or App.xaml contributes itself.
        var available = new HashSet<string>(
            LoadTheme().Keys.Cast<object>().Select(k => k.ToString()!),
            StringComparer.Ordinal);
        available.UnionWith(Keys(view));
        available.UnionWith(Keys(app));

        foreach (var used in StaticResources(view))
            Assert.Contains(used, available);
    }

    private static IEnumerable<string> Keys(string xaml)
        => Matches(xaml, "x:Key=\"([^\"]+)\"");

    private static IEnumerable<string> StaticResources(string xaml)
        => Matches(xaml, "\\{(?:Static|Dynamic)Resource ([^}]+)\\}")
            .Select(k => k.Trim())
            .Distinct(StringComparer.Ordinal);

    private static IEnumerable<string> Matches(string text, string pattern)
        => System.Text.RegularExpressions.Regex.Matches(text, pattern)
            .Select(m => m.Groups[1].Value);
}
