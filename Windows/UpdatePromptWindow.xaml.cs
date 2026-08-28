using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Windows;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;

namespace Spark
{
    public partial class UpdatePromptWindow : Window
    {
        private readonly string _latestVersion;
        private readonly string _changelog;
        private readonly string _downloadUrl;
        private readonly string _zipFileName;
        
        private readonly string _tempFolder;
        private readonly string _appFolder;
        private bool _updateStarted;

        /// <summary>
        /// Fired when the window closes without the update having been started (Later, or the
        /// window's own close button) — the caller uses this to surface a persistent footer badge
        /// so the update isn't lost, since this window won't be shown again on its own.
        /// </summary>
        public event Action Dismissed;

        public UpdatePromptWindow(string latestVersion, string changelog, string downloadUrl, string zipFileName)
        {
            InitializeComponent();

            _latestVersion = latestVersion;
            _changelog = changelog;
            _downloadUrl = downloadUrl;
            _zipFileName = zipFileName;

            _tempFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spark", "Temp");
            _appFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            if (!Directory.Exists(_tempFolder))
            {
                Directory.CreateDirectory(_tempFolder);
            }

            CurrentVersionText.Text = "v" + GetCurrentVersion();
            LatestVersionText.Text = "v" + _latestVersion;
            ChangelogText.Text = string.IsNullOrWhiteSpace(_changelog) ? "No release notes provided." : _changelog;

            Closed += (_, _) =>
            {
                if (!_updateStarted) Dismissed?.Invoke();
            };
        }

        private string GetCurrentVersion()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }

        private void LaterButton_Click(object sender, RoutedEventArgs e)
        {
            if (IgnoreUpdateCheckBox.IsChecked == true)
            {
                SparkSettings.instance.ignoredUpdateVersion = _latestVersion;
                SparkSettings.instance.Save();
            }
            Close();
        }

        private async void UpdateButton_Click(object sender, RoutedEventArgs e)
        {
            ActionButtons.Visibility = Visibility.Collapsed;
            ChangelogBorder.Visibility = Visibility.Collapsed;
            ProgressArea.Visibility = Visibility.Visible;
            
            StatusText.Text = "Starting download...";
            DownloadProgressBar.Value = 0;

            try
            {
                string tempFilePath = Path.Combine(_tempFolder, _zipFileName);

                await FetchUtils.DownloadFileAsync(_downloadUrl, tempFilePath, percentage =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        DownloadProgressBar.Value = percentage;
                        StatusText.Text = $"Downloading update: {percentage}%";
                    });
                });

                StatusText.Text = "Download complete. Launching installer...";

                LaunchInstaller(tempFilePath);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Update failed: {ex.Message}", "Error Updating", MessageBoxButton.OK, MessageBoxImage.Error);
                
                ActionButtons.Visibility = Visibility.Visible;
                ChangelogBorder.Visibility = Visibility.Visible;
                ProgressArea.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Launches the downloaded .msi directly instead of hand-rolling a kill/replace/relaunch
        /// batch script — the WiX installer already handles that via its MajorUpgrade element
        /// (IgniteBot.Installer/Product.wxs), including an option to relaunch Spark on finish.
        /// </summary>
        private void LaunchInstaller(string msiPath)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = msiPath,
                    UseShellExecute = true
                });
                _updateStarted = true;

                // Give the installer's own window a moment to come up before this process exits,
                // since the installer needs Spark.exe to not be running to replace its files.
                Task.Delay(500).ContinueWith(t =>
                {
                    Dispatcher.Invoke(() => Process.GetCurrentProcess().Kill());
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    System.Windows.MessageBox.Show($"Failed to launch installer: {ex.Message}", "Installation Error", MessageBoxButton.OK, MessageBoxImage.Error);

                    ActionButtons.Visibility = Visibility.Visible;
                    ChangelogBorder.Visibility = Visibility.Visible;
                    ProgressArea.Visibility = Visibility.Collapsed;
                });
            }
        }
    }
}
