using Bilgames.App.Controls;
using Bilgames.App.Services;
using Bilgames.Core.Model;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Bilgames.App.Views;

/// <summary>The "Add speaker" and "Merge speakers" dialogs.</summary>
public static class SpeakerDialogs
{
    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>Asks for a new speaker's name and (for picking a voice) whether they're a woman or a man.</summary>
    public static async Task<(string Name, Gender Gender)?> AskNewSpeakerAsync(string initialName = "")
    {
        var name = new TextBox { Header = "Name", Text = initialName, PlaceholderText = "e.g. Ferguson", MaxLength = 80 };
        var gender = new RadioButtons { Header = "Voice" };
        gender.Items.Add("Woman's voice");
        gender.Items.Add("Man's voice");
        gender.Items.Add("Either (pick any unused voice)");
        gender.SelectedIndex = 2;
        var panel = new StackPanel
        {
            Spacing = 14,
            Width = 380,
            Children =
            {
                new TextBlock { Text = "Add a character the speaker search missed. They get their own colour and an unused voice, and you can give them lines from Review or from another speaker's lines.", TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 20, Foreground = Res("TextSecondaryBrush") },
                name,
                gender,
            },
        };
        var result = await Dialogs.ShowContentAsync("Add speaker", panel, "Add", "Cancel", d =>
        {
            d.IsPrimaryButtonEnabled = name.Text.Trim().Length > 0;
            name.TextChanged += (_, _) => d.IsPrimaryButtonEnabled = name.Text.Trim().Length > 0;
            d.Opened += (_, _) => { name.Focus(FocusState.Programmatic); name.SelectAll(); };
        });
        if (result != ContentDialogResult.Primary || name.Text.Trim().Length == 0) return null;
        return (name.Text.Trim(), gender.SelectedIndex switch { 0 => Gender.Female, 1 => Gender.Male, _ => Gender.Unknown });
    }

    /// <summary>
    /// Pick two or more speakers who are really the same character, and which name to keep. Returns
    /// the ids to fold away and the id to keep.
    /// </summary>
    public static async Task<(List<string> From, string Into)?> AskMergeAsync(CastSheet cast, string? startWith)
    {
        var members = cast.Members.Where(m => !m.IsNarrator)
            .OrderByDescending(m => m.Id == startWith).ThenByDescending(m => m.LineCount).ThenBy(m => m.Name).ToList();
        var checks = new List<(CheckBox Box, CastMember Member)>();
        var list = new StackPanel { Spacing = 2 };
        var search = new TextBox { PlaceholderText = "Search speakers" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(search, "Search speakers");
        var keep = new ComboBox { Header = "Keep the name", HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = false };
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Res("TextMutedBrush") };
        ContentDialog? dialog = null;
        string? chosen = null; // the name the user picked to keep; until then, the one with the most lines
        bool updating = false;
        keep.SelectionChanged += (_, _) => { if (!updating) chosen = (keep.SelectedItem as ComboBoxItem)?.Tag as string; };

        void Update()
        {
            var picked = checks.Where(c => c.Box.IsChecked == true).Select(c => c.Member).ToList();
            updating = true;
            keep.Items.Clear();
            foreach (var m in picked.OrderByDescending(m => m.LineCount))
                keep.Items.Add(new ComboBoxItem { Content = m.Name, Tag = m.Id });
            keep.IsEnabled = picked.Count >= 2;
            if (keep.Items.Count > 0)
                keep.SelectedItem = keep.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == chosen) ?? keep.Items[0];
            updating = false;
            int lines = picked.Sum(m => m.LineCount);
            var names = picked.Select(m => m.Name).ToList();
            var list = names.Count <= 1 ? "" : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
            summary.Text = picked.Count < 2
                ? "Tick every name that belongs to the same character."
                : $"{list} ({lines:N0} {(lines == 1 ? "line" : "lines")}) become one speaker. The other names are remembered, so finding speakers again keeps them together.";
            if (dialog is not null) dialog.IsPrimaryButtonEnabled = picked.Count >= 2;
        }

        foreach (var m in members)
        {
            var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new Avatar { Size = 28, Color = m.Color, Initials = m.Initials, VerticalAlignment = VerticalAlignment.Center });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = m.Name, FontWeight = FontWeights.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });
            var detail = $"{m.LineCount:N0} {(m.LineCount == 1 ? "line" : "lines")}" + (m.Aliases.Count > 0 ? " · also " + string.Join(", ", m.Aliases.Take(3)) : "");
            text.Children.Add(new TextBlock { Text = detail, FontSize = 12, Foreground = Res("TextMutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var box = new CheckBox { Content = row, IsChecked = m.Id == startWith, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, $"{m.Name}, {detail}");
            box.Checked += (_, _) => Update();
            box.Unchecked += (_, _) => Update();
            checks.Add((box, m));
            list.Children.Add(box);
        }

        search.TextChanged += (_, _) =>
        {
            var q = search.Text.Trim();
            foreach (var (box, m) in checks)
                box.Visibility = q.Length == 0 || m.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || m.Aliases.Any(a => a.Contains(q, StringComparison.OrdinalIgnoreCase))
                    ? Visibility.Visible : Visibility.Collapsed;
        };

        var panel = new StackPanel
        {
            Spacing = 12,
            Width = 420,
            Children =
            {
                search,
                new ScrollViewer { Content = list, MaxHeight = 300, Padding = new Thickness(0, 0, 12, 0) },
                keep,
                summary,
            },
        };
        Update();
        var result = await Dialogs.ShowContentAsync("Merge speakers", panel, "Merge", "Cancel", d =>
        {
            dialog = d;
            d.IsPrimaryButtonEnabled = checks.Count(c => c.Box.IsChecked == true) >= 2;
            d.Opened += (_, _) => search.Focus(FocusState.Programmatic);
        });
        if (result != ContentDialogResult.Primary || (keep.SelectedItem as ComboBoxItem)?.Tag is not string into) return null;
        var from = checks.Where(c => c.Box.IsChecked == true && c.Member.Id != into).Select(c => c.Member.Id).ToList();
        return from.Count == 0 ? null : (from, into);
    }
}
