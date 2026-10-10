using Microsoft.UI.Xaml.Controls.Primitives;

namespace Bilgames.App.Controls;

/// <summary>
/// A toggle that can be turned on by the user but not off: segmented controls and navigation
/// items are "one of N", so clicking the current item must keep it selected.
/// </summary>
public sealed partial class SegmentToggle : ToggleButton
{
    protected override void OnToggle()
    {
        if (IsChecked != true) base.OnToggle();
    }
}
