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
}

/// <summary>
/// A flyout with a search box over a scrollable list — for picking a speaker or a voice. With an
/// empty search it shows everything, so it doubles as "browse all".
/// </summary>
public static class SearchPicker
{
    /// <param name="create">
    /// Optional: offers "Add “typed text” as a new speaker" when the search doesn't name an existing item,
    /// and is called with the typed name when that row is picked.
    /// </param>
    public static void Show(FrameworkElement anchor, IReadOnlyList<PickerItem> items, string placeholder, Action<PickerItem> picked, Action<string>? create = null)
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        var search = new TextBox { PlaceholderText = placeholder, Margin = new Thickness(0, 0, 0, 8) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(search, placeholder);
        var list = new ListView { MaxHeight = 360, SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true };
        var empty = new TextBlock { Text = create is null ? "No matches" : "No matches. Type a name to add a new speaker.", TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["TextMutedBrush"], Visibility = Visibility.Collapsed, Margin = new Thickness(4) };
        const string NewId = "\u0001new";

        void Fill()
        {
            var q = search.Text.Trim();
            list.Items.Clear();
            foreach (var item in items.Where(i => q.Length == 0
                         || i.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                         || i.Detail.Contains(q, StringComparison.OrdinalIgnoreCase)
                         || i.Keywords.Contains(q, StringComparison.OrdinalIgnoreCase)))
                list.Items.Add(Row(item));
            if (create is not null && q.Length > 0 && !items.Any(i => i.Title.Equals(q, StringComparison.OrdinalIgnoreCase)))
            {
                var row = Row(new PickerItem { Id = NewId, Title = $"Add “{q}” as a new speaker", Detail = "Gets their own voice; change it on the Cast tab", Color = "#2A303A", Initials = "+", Keywords = q });
                list.Items.Add(row); // last, so Enter still picks the best existing match
            }
            empty.Visibility = list.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void Choose(PickerItem item)
        {
            flyout.Hide();
            if (item.Id == NewId) create?.Invoke(item.Keywords);
            else picked(item);
        }

        search.TextChanged += (_, _) => Fill();
        search.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && list.Items.Count > 0 && ((FrameworkElement)list.Items[0]).Tag is PickerItem first)
                Choose(first);
        };
        list.ItemClick += (_, e) =>
        {
            if (((FrameworkElement)e.ClickedItem).Tag is PickerItem item) Choose(item);
        };
        Fill();
        flyout.Content = new StackPanel { Width = 320, Children = { search, list, empty } };
        flyout.Opened += (_, _) => search.Focus(FocusState.Programmatic);
        flyout.ShowAt(anchor);
    }

    private static FrameworkElement Row(PickerItem item)
    {
        var grid = new Grid { ColumnSpacing = 10, MinHeight = 44, Tag = item, Padding = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (item.Color is not null)
            grid.Children.Add(new Avatar { Size = 28, Color = item.Color, Initials = item.Initials ?? "", VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = item.Title, FontWeight = FontWeights.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });
        if (item.Detail.Length > 0)
            text.Children.Add(new TextBlock { Text = item.Detail, FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextMutedBrush"], TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (item.IsCurrent)
        {
            var mark = new TextBlock { Text = "Current", FontSize = 12, Foreground = (Brush)Application.Current.Resources["AccentBrush"], VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(mark, 2);
            grid.Children.Add(mark);
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grid, item.Detail.Length > 0 ? $"{item.Title}, {item.Detail}" : item.Title);
        return grid;
    }
}
