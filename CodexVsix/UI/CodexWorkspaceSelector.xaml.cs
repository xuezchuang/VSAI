using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace CodexVsix.UI;

public partial class CodexWorkspaceSelector : UserControl
{
    public CodexWorkspaceSelector()
    {
        InitializeComponent();
    }

    private void OnOpenMenu(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.ContextMenu is ContextMenu menu)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Top;
            menu.IsOpen = true;
        }
    }
}
