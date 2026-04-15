using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Microsoft.Win32;
namespace Spark
{
    public partial class DownloadEchoVRTab : UserControl
    {
        private string pnsovrDllPath;
        private const string DllLinkPlaceholder = "Paste DLL download link here";

        public DownloadEchoVRTab()
        {
            InitializeComponent();
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                e.Handled = true;
            }
            catch (Exception ex)
            {
                Logger.LogRow(Logger.LogType.Error, ex.ToString());
            }
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                InitialDirectory = DownloadPathTextBox.Text
            };

            if (dialog.ShowDialog() == true)
            {
                DownloadPathTextBox.Text = dialog.FolderName;
            }
        }

        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            DownloadButton.IsEnabled = false;
            DownloadProgressBar.Visibility = Visibility.Visible;
            DownloadStatusTextBlock.Text = "Starting download...";

            try
            {
                string downloadUrl = "https://files.echovr.de/ready-at-dawn-echo-arena.zip";
                string downloadFolder = DownloadPathTextBox.Text;
                Directory.CreateDirectory(downloadFolder);
                string zipPath = Path.Combine(downloadFolder, "ready-at-dawn-echo-arena.zip");

                long existingLength = 0;
                if (File.Exists(zipPath))
                {
                    existingLength = new FileInfo(zipPath).Length;
                }

                using (var client = new HttpClient())
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl))
                    {
                        if (existingLength > 0)
                        {
                            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingLength, null);
                            DownloadStatusTextBlock.Text = $"Resuming download from {existingLength / 1024 / 1024:F2} MB...";
                        }

                        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                        if (response.StatusCode == HttpStatusCode.OK)
                        {
                            existingLength = 0; // Server doesn't support range, starting from scratch
                        }
                        else if (response.StatusCode != HttpStatusCode.PartialContent && existingLength > 0)
                        {
                            existingLength = 0; // Server didn't honor range request, starting from scratch
                            if(File.Exists(zipPath)) File.Delete(zipPath);
                        }
                
                        response.EnsureSuccessStatusCode();

                        long totalBytes = response.Content.Headers.ContentLength.HasValue ? 
                                          response.Content.Headers.ContentLength.Value + existingLength : 
                                          -1L;
                
                        if (response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.Length != null)
                        {
                            totalBytes = response.Content.Headers.ContentRange.Length.Value;
                        }

                        var canReportProgress = totalBytes != -1;

                        var fileMode = existingLength > 0 ? FileMode.Append : FileMode.Create;
                        using (var fileStream = new FileStream(zipPath, fileMode, FileAccess.Write, FileShare.None, 8192, true))
                        using (var stream = await response.Content.ReadAsStreamAsync())
                        {
                            var totalBytesRead = existingLength;
                            var buffer = new byte[8192];
                            var isMoreToRead = true;

                            do
                            {
                                var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                                if (bytesRead == 0)
                                {
                                    isMoreToRead = false;
                                    continue;
                                }

                                await fileStream.WriteAsync(buffer, 0, bytesRead);

                                totalBytesRead += bytesRead;

                                if (canReportProgress)
                                {
                                    var progressPercentage = (double)totalBytesRead / totalBytes * 100;
                                    DownloadProgressBar.Value = progressPercentage;
                                    DownloadStatusTextBlock.Text = $"Downloading... {totalBytesRead / 1024 / 1024:F2} MB / {totalBytes / 1024 / 1024:F2} MB";
                                }
                                else
                                {
                                    DownloadStatusTextBlock.Text = $"Downloading... {totalBytesRead / 1024 / 1024:F2} MB";
                                }
                            } while (isMoreToRead);
                        }
                    }
                }

                DownloadStatusTextBlock.Text = "Download complete. Extracting...";
                await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, downloadFolder, true));

                DownloadStatusTextBlock.Text = "Extraction complete.";

                if (DeleteZipCheckBox.IsChecked == true)
                {
                    File.Delete(zipPath);
                    DownloadStatusTextBlock.Text += " ZIP file deleted.";
                }
            }
            catch (Exception ex)
            {
                DownloadStatusTextBlock.Text = $"An error occurred: {ex.Message}";
                System.Windows.MessageBox.Show($"An error occurred: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                DownloadButton.IsEnabled = true;
                DownloadProgressBar.Visibility = Visibility.Collapsed;
            }
        }

        private void SelectDllButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "DLL files (*.dll)|*.dll|All files (*.*)|*.*",
                Title = "Select pnsovr.dll"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                if (Path.GetFileName(openFileDialog.FileName).ToLower() == "pnsovr.dll")
                {
                    pnsovrDllPath = openFileDialog.FileName;
                    PatchStatusTextBlock.Text = $"Selected: {pnsovrDllPath}";
                    PatchButton.IsEnabled = true;
                }
                else
                {
                    System.Windows.MessageBox.Show("Please select a file named 'pnsovr.dll'.", "Invalid File", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private async void DownloadDllButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(DllLinkTextBox.Text) || DllLinkTextBox.Text == DllLinkPlaceholder)
            {
                System.Windows.MessageBox.Show("Please paste a valid download link for the DLL.", "Invalid Link", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DownloadDllButton.IsEnabled = false;
            PatchStatusTextBlock.Text = "Downloading DLL...";

            try
            {
                string tempDllPath = Path.Combine(Path.GetTempPath(), "pnsovr.dll");
                using (var client = new HttpClient())
                {
                    var response = await client.GetAsync(DllLinkTextBox.Text);
                    response.EnsureSuccessStatusCode();
                    using (var fs = new FileStream(tempDllPath, FileMode.Create))
                    {
                        await response.Content.CopyToAsync(fs);
                    }
                }
                pnsovrDllPath = tempDllPath;
                PatchStatusTextBlock.Text = "DLL downloaded successfully.";
                PatchButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                PatchStatusTextBlock.Text = $"DLL download failed: {ex.Message}";
                System.Windows.MessageBox.Show($"DLL download failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                DownloadDllButton.IsEnabled = true;
            }
        }

        private void PatchButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string echoVrFolder = DownloadPathTextBox.Text;
                string win10Folder = Path.Combine(echoVrFolder, "ready-at-dawn-echo-arena", "bin", "win10");

                if (!Directory.Exists(win10Folder))
                {
                    System.Windows.MessageBox.Show($"The folder '{win10Folder}' does not exist. Make sure EchoVR was extracted correctly.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                string destDllPath = Path.Combine(win10Folder, "pnsovr.dll");
                File.Copy(pnsovrDllPath, destDllPath, true);

                PatchStatusTextBlock.Text = "Game patched successfully!";
                System.Windows.MessageBox.Show("Game patched successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                PatchStatusTextBlock.Text = $"Patching failed: {ex.Message}";
                System.Windows.MessageBox.Show($"Patching failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShortcutButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string echoVrFolder = DownloadPathTextBox.Text;
                string exePath = Path.Combine(echoVrFolder, "ready-at-dawn-echo-arena", "bin", "win10", "echovr.exe");

                if (!File.Exists(exePath))
                {
                    System.Windows.MessageBox.Show($"Executable not found at '{exePath}'. Please ensure EchoVR is downloaded and extracted correctly.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                string shortcutName = "Echo VR (Community)";
                AddShortcutsAll(exePath, shortcutName);

                ShortcutStatusTextBlock.Text = "Shortcuts created successfully!";
                System.Windows.MessageBox.Show("Shortcuts created successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ShortcutStatusTextBlock.Text = $"Shortcut creation failed: {ex.Message}";
                System.Windows.MessageBox.Show($"Shortcut creation failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void AddShortcutsAll(string exePath, string shortcutName)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string startMenu = Path.Combine(appData, "Microsoft", "Windows", "Start Menu", "Programs");
            string desktop = Path.Combine(userProfile, "Desktop");

            CreateWindowsShortcut(exePath, Path.Combine(startMenu, shortcutName + ".lnk"));
            CreateWindowsShortcut(exePath, Path.Combine(desktop, shortcutName + ".lnk"));
        }

        private void CreateWindowsShortcut(string exePath, string shortcutPath)
        {
            string psScript = $@"
$WshShell = New-Object -ComObject WScript.Shell
$Shortcut = $WshShell.CreateShortcut(""{shortcutPath.Replace("\"", "\"\"")}"")
$Shortcut.TargetPath = ""{exePath.Replace("\"", "\"\"")}""
$Shortcut.Save()
";
            string tempPs1 = Path.GetTempFileName() + ".ps1";
            File.WriteAllText(tempPs1, psScript);

            var processStartInfo = new ProcessStartInfo("powershell.exe", $"-ExecutionPolicy Bypass -File \"{tempPs1}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var process = Process.Start(processStartInfo))
            {
                process.WaitForExit();
            }

            File.Delete(tempPs1);
        }

        private void DllLinkTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (DllLinkTextBox.Text == DllLinkPlaceholder)
            {
                DllLinkTextBox.Text = "";
                DllLinkTextBox.SetResourceReference(Control.ForegroundProperty, "ControlDefaultForeground");
            }
        }

        private void DllLinkTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(DllLinkTextBox.Text))
            {
                DllLinkTextBox.Text = DllLinkPlaceholder;
                DllLinkTextBox.SetResourceReference(Control.ForegroundProperty, "ControlDisabledGlythColour");
            }
        }
    }
}