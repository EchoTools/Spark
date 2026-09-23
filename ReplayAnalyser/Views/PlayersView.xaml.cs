using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Spark.ReplayAnalyser.ViewModels;

namespace Spark.ReplayAnalyser.Views;

public partial class PlayersView : UserControl
{
    public PlayersView() => InitializeComponent();

    /// <summary>A scoreboard row opens that player in the detail card, same as picking them from the list.</summary>
    private void PlayerRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PlayerVm player } && DataContext is ReplayAnalyserViewModel vm)
            vm.SelectedPlayer = player;
    }
}
