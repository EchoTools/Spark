using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Spark.Controllers;

namespace Spark
{
	/// <summary>
	/// Code-behind for the Media Controller settings tab.
	/// Owns the <see cref="EchoVRButtonDetector"/> lifecycle and bridges its
	/// status events to the UI.
	/// </summary>
	public partial class MediaControllerSettings : UserControl
	{
		private EchoVRButtonDetector _detector;

		public MediaControllerSettings()
		{
			InitializeComponent();
		}

		// ── Lifecycle ─────────────────────────────────────────────────────────

		private void UserControl_Loaded(object sender, RoutedEventArgs e)
		{
			// Show warning if Controller Clipping is already active
			if (SparkSettings.instance.mediaControllerCustomEnabled)
				MediaExclusivityWarning.Visibility = Visibility.Visible;

			// Reflect stored "enabled" state immediately
			if (SparkSettings.instance.mediaControllerEnabled)
				StartDetector();
		}

		private void UserControl_Unloaded(object sender, RoutedEventArgs e)
		{
			StopDetector();
		}

		// ── Enable / disable ──────────────────────────────────────────────────

		private void EnableChecked(object sender, RoutedEventArgs e)
		{
			// Block enabling if Controller Clipping is active
			if (SparkSettings.instance.mediaControllerCustomEnabled)
			{
				SparkSettings.instance.mediaControllerEnabled = false;
				EnableCheckBox.IsChecked = false;
				MediaExclusivityWarning.Visibility = Visibility.Visible;
				return;
			}

			MediaExclusivityWarning.Visibility = Visibility.Collapsed;
			SparkSettings.instance.mediaControllerEnabled = true;
			SparkSettings.instance.Save();
			StartDetector();
		}

		private void EnableUnchecked(object sender, RoutedEventArgs e)
		{
			SparkSettings.instance.mediaControllerEnabled = false;
			SparkSettings.instance.Save();
			StopDetector();
			SetStatus(connected: false, action: "");
		}

		// ── Detector management ───────────────────────────────────────────────

		private void StartDetector()
		{
			if (_detector != null) return;

			_detector = new EchoVRButtonDetector();
			_detector.StatusChanged += OnStatusChanged;
			_detector.Start();
		}

		private void StopDetector()
		{
			if (_detector == null) return;
			_detector.StatusChanged -= OnStatusChanged;
			_detector.Stop();
			_detector.Dispose();
			_detector = null;
		}

		// ── Status callback (arrives on background thread) ────────────────────

		private void OnStatusChanged(string message)
		{
			Dispatcher.InvokeAsync(() =>
			{
				bool connected = message.StartsWith("Connected",
					StringComparison.OrdinalIgnoreCase);

				if (message.Equals("Connected", StringComparison.OrdinalIgnoreCase) ||
				    message.Equals("Disconnected", StringComparison.OrdinalIgnoreCase))
				{
					SetStatus(connected, "");
				}
				else
				{
					// Action feedback (e.g. "Next Track (4 clicks)")
					bool isConn = _detector?.IsConnected == true;
					SetStatus(isConn, message);

					// Clear action text after 3 seconds
					var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
					timer.Tick += (s, _) => { timer.Stop(); if (ActionText.Text.Contains(message)) ActionText.Text = ""; };
					timer.Start();
				}
			});
		}

		private void SetStatus(bool connected, string action)
		{
			StatusDot.Fill  = new SolidColorBrush(connected
				? Color.FromRgb(0x27, 0xae, 0x60)   // green
				: Color.FromRgb(0xc0, 0x39, 0x2b));  // red

			StatusText.Text = connected ? "EchoVR: Connected" : "EchoVR: Disconnected";
			ActionText.Text = string.IsNullOrEmpty(action) ? "" : $"│  {action}";
		}

		// ── Test buttons ──────────────────────────────────────────────────────

		private void PrevTrackClick(object sender, RoutedEventArgs e)
		{
			EchoVRButtonDetector.SendKey(MediaAction.PrevTrack);
			ShowActionFeedback("⏮ Prev Track");
		}

		private void PlayPauseClick(object sender, RoutedEventArgs e)
		{
			EchoVRButtonDetector.SendKey(MediaAction.PlayPause);
			ShowActionFeedback("⏯ Play/Pause");
		}

		private void NextTrackClick(object sender, RoutedEventArgs e)
		{
			EchoVRButtonDetector.SendKey(MediaAction.NextTrack);
			ShowActionFeedback("⏭ Next Track");
		}

		private void ShowActionFeedback(string text)
		{
			bool connected = _detector?.IsConnected == true;
			SetStatus(connected, $"Test: {text}");

			// Clear after 2 seconds
			var timer = new System.Windows.Threading.DispatcherTimer
			{
				Interval = TimeSpan.FromSeconds(2)
			};
			timer.Tick += (s, _) =>
			{
				timer.Stop();
				ActionText.Text = "";
			};
			timer.Start();
		}

		// ── Numeric box validation (prevent non-numeric input) ────────────────

		private void NumericBox_TextChanged(object sender, TextChangedEventArgs e)
		{
			SparkSettings.instance.Save();
		}
	}
}
