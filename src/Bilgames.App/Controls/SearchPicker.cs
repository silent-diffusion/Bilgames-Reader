using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Bilgames.App.Controls;

public sealed class PickerItem
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    /// <summary>Avatar colour + initials (speakers); omitted for voices.</summary>
    public string? Color { get; init; }
    public string? Initials { get; init; }
    public bool IsCurrent { get; init; }
    /// <summary>Extra text matched by search (aliases, accent…).</summary>
    public string Keywords { get; init; } = "";
    /// <summary>
    /// Menu path for browsing, e.g. ["Female", "British"]. When items have paths, the empty-search view
    /// is a set of sub-menus; typing a search shows one flat list of matches.
    /// </summary>
    public IReadOnlyList<string>? Path { get; init; }
    /// <summary>A short pill on the right, e.g. "In use" (voices already given to someone).</summary>
    public string? Badge { get; init; }
    /// <summary>Who or what the badge refers to, e.g. "Davian, Wirr" (shown under the title).</summary>
    public string? BadgeDetail { get; init; }
}

/// <summary>
/// A flyout with a search box over a scrollable list, for picking a speaker or a voice. With an
/// empty search it is for browsing: everything in one list, or, when items carry a
/// <see cref="PickerItem.Path"/>, sub-menus (e.g. gender, then nationality). Typing searches everything.
/// </summary>
public static class SearchPicker
{
    /// <param name="create">
    /// Optional: offers "Add “typed text” as a new speaker" when the search doesn't name an existing item,
    /// and is called with the typed name when that row is picked.
    /// </param>
    /// <param name="preview">Optional: shows a ▶ on each item that plays a sample (voices) without choosing it.</param>
    public static void Show(FrameworkElement anchor, IReadOnlyList<PickerItem> items, string placeholder, Action<PickerItem> picked, Action<string>? create = null, Func<PickerItem, Task>? preview = null)
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        var search = new TextBox { PlaceholderText = placeholder, Margin = new Thickness(0, 0, 0, 8) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(search, placeholder);
        var list = new ListView { MaxHeight = 380, SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true };
        var empty = new TextBlock { Text = create is null ? "No matches" : "No matches. Type a name to add a new speaker.", TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["TextMutedBrush"], Visibility = Visibility.Collapsed, Margin = new Thickness(4) };
        const string NewId = "\u0001new", GroupId = "\u0001group", BackId = "\u0001back";

        // Browsing position in the menus; starts where the current item lives, so its neighbours are one click away.
        bool grouped = items.Any(i => i.Path is { Count: > 0 });
        var path = new List<string>(items.FirstOrDefault(i => i.IsCurrent)?.Path ?? Array.Empty<string>());

        bool Under(PickerItem i, int depth) => i.Path is { } p && p.Count >= depth && p.Take(depth).SequenceEqual(path.Take(depth));

        void Fill()
        {
            var q = search.Text.Trim();
            list.Items.Clear();
            if (q.Length == 0 && grouped)
            {
                // Sub-menus: a "back" row, then the next level's groups, or the items at the bottom level.
                if (path.Count > 0)
                    list.Items.Add(GroupRow(new PickerItem { Id = BackId, Title = "‹  " + (path.Count == 1 ? "All voices" : path[^2]), Detail = "Showing " + string.Join(" › ", path) }, back: true));
                var here = items.Where(i => Under(i, path.Count)).ToList();
                var deeper = here.Where(i => i.Path!.Count > path.Count).GroupBy(i => i.Path![path.Count]).ToList();
                foreach (var g in deeper)
                {
                    int inUse = g.Count(i => i.Badge is not null);
                    bool hasCurrent = g.Any(i => i.IsCurrent);
                    var detail = $"{g.Count()} {(g.Count() == 1 ? "voice" : "voices")}" + (inUse > 0 ? $" · {inUse} in use" : "") + (hasCurrent ? " · current voice here" : "");
                    list.Items.Add(GroupRow(new PickerItem { Id = GroupId, Title = g.Key, Detail = detail, Keywords = g.Key }, back: false));
                }
                foreach (var item in here.Where(i => i.Path!.Count == path.Count)) list.Items.Add(Row(item, preview));
            }
            else
            {
                foreach (var item in items.Where(i => Matches(i, q)))
                    list.Items.Add(Row(item, preview));
                if (create is not null && q.Length > 0 && !items.Any(i => i.Title.Equals(q, StringComparison.OrdinalIgnoreCase)))
                {
                    var row = Row(new PickerItem { Id = NewId, Title = $"Add “{q}” as a new speaker", Detail = "Gets their own voice; change it on the Cast tab", Color = "#2A303A", Initials = "+", Keywords = q });
                    list.Items.Add(row); // last, so Enter still picks the best existing match
                }
            }
            empty.Visibility = list.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void Choose(PickerItem item)
        {
            if (item.Id == BackId) { path.RemoveAt(path.Count - 1); Fill(); return; }
            if (item.Id == GroupId) { path.Add(item.Title); Fill(); return; }
            flyout.Hide();
            if (item.Id == NewId) create?.Invoke(item.Keywords);
            else picked(item);
        }

        search.TextChanged += (_, _) => Fill();
        search.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && list.Items.Count > 0 && ((FrameworkElement)list.Items[0]).Tag is PickerItem first)
                Choose(first);
            else if (e.Key == Windows.System.VirtualKey.Back && search.Text.Length == 0 && grouped && path.Count > 0)
            {
                path.RemoveAt(path.Count - 1);
                Fill();
            }
        };
        list.ItemClick += (_, e) =>
        {
            if (((FrameworkElement)e.ClickedItem).Tag is PickerItem item) Choose(item);
        };
        Fill();
        flyout.Content = new StackPanel { Width = 340, Children = { search, list, empty } };
        flyout.Opened += (_, _) => search.Focus(FocusState.Programmatic);
        flyout.ShowAt(anchor);
    }

    /// <summary>
    /// Every typed word must match the start of a word in the item ("male" finds Male voices, not
    /// Female ones; "brit fem" finds British female voices; "barm" finds "The Barman").
    /// </summary>
    public static bool Matches(PickerItem item, string query)
    {
        var wanted = Words(query);
        if (wanted.Count == 0) return true;
        var have = Words($"{item.Title} {item.Detail} {item.Keywords} {item.BadgeDetail} {string.Join(' ', item.Path ?? Array.Empty<string>())}");
        return wanted.All(w => have.Any(h => h.StartsWith(w, StringComparison.OrdinalIgnoreCase)));
    }

    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch)) { current.Append(ch); continue; }
            if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); }
        }
        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }

    /// <summary>A sub-menu row ("British ›") or the "‹ back" row.</summary>
    private static FrameworkElement GroupRow(PickerItem item, bool back)
    {
        var grid = new Grid { ColumnSpacing = 10, MinHeight = back ? 36 : 44, Tag = item, Padding = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = item.Title, FontWeight = back ? FontWeights.Normal : FontWeights.SemiBold, FontSize = back ? 13 : 14,
            Foreground = (Brush)Application.Current.Resources[back ? "AccentBrush" : "TextBrush"],
        });
        if (item.Detail.Length > 0)
            text.Children.Add(new TextBlock { Text = item.Detail, FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextMutedBrush"] });
        grid.Children.Add(text);
        if (!back)
        {
            var chevron = new TextBlock { Text = "›", FontSize = 20, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.Resources["TextMutedBrush"] };
            Grid.SetColumn(chevron, 1);
            grid.Children.Add(chevron);
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grid, back ? $"Back to {item.Title.TrimStart('‹', ' ')}" : $"{item.Title}, {item.Detail}. Open");
        return grid;
    }

    private static FrameworkElement Row(PickerItem item, Func<PickerItem, Task>? preview = null)
    {
        var grid = new Grid { ColumnSpacing = 10, MinHeight = 44, Tag = item, Padding = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (item.Color is not null)
            grid.Children.Add(new Avatar { Size = 28, Color = item.Color, Initials = item.Initials ?? "", VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = item.Title, FontWeight = FontWeights.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });
        var detail = item.Detail;
        if (detail.Length > 0)
            text.Children.Add(new TextBlock { Text = detail, FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextMutedBrush"], TextTrimming = TextTrimming.CharacterEllipsis });
        if (item.BadgeDetail is { Length: > 0 } who)
            text.Children.Add(new TextBlock { Text = who, FontSize = 12, Foreground = (Brush)Application.Current.Resources["WarningStrongBrush"], TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var marks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (item.Badge is { } badge)
            marks.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 2, 7, 2), VerticalAlignment = VerticalAlignment.Center,
                Background = (Brush)Application.Current.Resources["WarningBgBrush"], BorderBrush = (Brush)Application.Current.Resources["WarningBorderBrush"], BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = badge, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = (Brush)Application.Current.Resources["WarningStrongBrush"] },
            });
        if (item.IsCurrent)
            marks.Children.Add(new TextBlock { Text = "Current", FontSize = 12, Foreground = (Brush)Application.Current.Resources["AccentBrush"], VerticalAlignment = VerticalAlignment.Center });
        if (preview is not null)
        {
            // Plays a sample without picking the voice (the button takes the click, not the list).
            var icon = new StrokeIcon { Data = "M8 5v14l11-7z", Filled = true, Width = 12, Height = 12 };
            var ring = new ProgressRing { Width = 14, Height = 14, IsActive = false, Visibility = Visibility.Collapsed, Foreground = (Brush)Application.Current.Resources["AccentBrush"] };
            var play = new Button
            {
                Width = 32, Height = 32, MinWidth = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(16),
                Background = (Brush)Application.Current.Resources["SurfaceRaisedBrush"], BorderThickness = new Thickness(0),
                Content = new Grid { Children = { icon, ring } },
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(play, $"Play a sample of {item.Title}");
            ToolTipService.SetToolTip(play, "Hear this voice");
            play.Click += async (_, _) =>
            {
                if (ring.IsActive) return;
                icon.Visibility = Visibility.Collapsed; ring.Visibility = Visibility.Visible; ring.IsActive = true;
                try { await preview(item); }
                catch { /* a failed preview just stops */ }
                finally { ring.IsActive = false; ring.Visibility = Visibility.Collapsed; icon.Visibility = Visibility.Visible; }
            };
            marks.Children.Add(play);
        }
        if (marks.Children.Count > 0)
        {
            Grid.SetColumn(marks, 2);
            grid.Children.Add(marks);
        }

        var name = item.Title;
        if (detail.Length > 0) name += ", " + detail;
        if (item.BadgeDetail is { Length: > 0 }) name += ", " + item.BadgeDetail;
        if (item.IsCurrent) name += ", current";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grid, name);
        return grid;
    }
}
