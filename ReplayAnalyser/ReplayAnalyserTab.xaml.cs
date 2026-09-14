using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Spark.ReplayAnalyser.ViewModels;

namespace Spark
{
	/// <summary>
	/// The Replay Analyser, as a Spark tab: the standalone app's library, match breakdown, player
	/// heatmaps, team shapes, coaching and career pages, over the same analysis code.
	/// </summary>
	public partial class ReplayAnalyserTab : UserControl
	{
		private ReplayAnalyserViewModel viewModel;
		private int currentPage;

		public ReplayAnalyserTab()
		{
			InitializeComponent();
		}

		/// <summary>
		/// The view model is made the first time the tab is shown rather than with the window, so
		/// reading the index and the replay folder never adds to Spark's own startup.
		/// </summary>
		private async void OnLoaded(object sender, RoutedEventArgs e)
		{
			if (viewModel != null) return;

			viewModel = new ReplayAnalyserViewModel();
			viewModel.PropertyChanged += ViewModelPropertyChanged;
			DataContext = viewModel;

			try
			{
				await viewModel.InitialiseAsync();
			}
			catch (Exception ex)
			{
				Logger.LogRow(Logger.LogType.Error, $"Replay Analyser couldn't load the replay library.\n{ex}");
				viewModel.LibraryStatus = $"Couldn't load the replay library: {ex.Message}";
			}
		}

		private void ViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
		{
			// Opening a replay from the Library should land on the match itself.
			if (e.PropertyName == nameof(ReplayAnalyserViewModel.HasMatch) && viewModel.HasMatch && currentPage == 0)
			{
				GoToPage(1);
			}
		}

		private void OnPageChecked(object sender, RoutedEventArgs e)
		{
			// Fires for the initially checked page while the XAML is still loading, before Pages exists.
			if (Pages == null) return;
			if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out int index))
			{
				ShowPage(index);
			}
		}

		/// <summary>Switch page as if its button had been clicked, so the navigation stays in step.</summary>
		private void GoToPage(int index)
		{
			RadioButton button = Nav.Children.OfType<RadioButton>().FirstOrDefault(b => (string)b.Tag == index.ToString());
			if (button != null) button.IsChecked = true;
		}

		private void ShowPage(int index)
		{
			currentPage = index;
			foreach (UIElement child in Pages.Children)
			{
				child.Visibility = child is FrameworkElement { Tag: string tag } && tag == index.ToString()
					? Visibility.Visible
					: Visibility.Collapsed;
			}
		}
	}
}
