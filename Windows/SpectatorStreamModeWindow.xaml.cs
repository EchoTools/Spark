using System.Windows;

namespace Spark
{
	/// <summary>
	/// Asks whether to spectate Echo Arena or Echo Combat before launching the spectator stream.
	/// </summary>
	public partial class SpectatorStreamModeWindow : Window
	{
		public bool Combat { get; private set; }

		public SpectatorStreamModeWindow()
		{
			InitializeComponent();
		}

		private void ArenaClicked(object sender, RoutedEventArgs e)
		{
			Combat = false;
			DialogResult = true;
		}

		private void CombatClicked(object sender, RoutedEventArgs e)
		{
			Combat = true;
			DialogResult = true;
		}
	}
}
