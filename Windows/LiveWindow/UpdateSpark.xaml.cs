using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Spark
{
    public partial class UpdateSparkControl : UserControl
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        private string _latestVersion = "";
        private string _currentVersion = "";
        private string _tempFolder = "";
        private string _appFolder = "";
        private List<ColorVersion> _availableVersions = new List<ColorVersion>();
        private string _selectedDownloadUrl = "";

        public class ColorVersion
        {
            public string DisplayName { get; set; }
            public string FileName { get; set; }
            public string DownloadUrl { get; set; }
        }

        public UpdateSparkControl()
        {
            InitializeComponent();

            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Spark-Updater");
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

            Loaded += OnLoaded;
            ColorVersionDropdown.SelectionChanged += ColorVersionDropdown_SelectionChanged;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _currentVersion = GetCurrentVersion();
            CurrentVersionText.Text = _currentVersion;

            _tempFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spark", "Temp");
            _appFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            if (!Directory.Exists(_tempFolder))
            {
                Directory.CreateDirectory(_tempFolder);
            }

            ColorVersionDropdown.IsEnabled = false;
            DownloadUpdateButton.IsEnabled = false;
        }

        private string GetCurrentVersion()
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }

        private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            CheckUpdateButton.IsEnabled = false;
            StatusText.Text = "Checking for updates...";
            UpdateDetailsText.Text += $"[{DateTime.Now}] Checking for updates...\n";

            try
            {
                // URL for the specific tag 'ignore' tag, download it
                string json = await _httpClient.GetStringAsync("https://api.github.com/repos/heisthecat31/Spark/releases/tags/ignore");

                // CREATE the 'release' variable here
                JObject release = JObject.Parse(json);

                string titleName = release["name"]?.ToString();
                // If the title is empty, we fall back to the "tag_name"
                if (string.IsNullOrWhiteSpace(titleName))
                {
                    titleName = release["tag_name"]?.ToString();
                }

                // Remove 'v' prefix if it exists (just in case)
                _latestVersion = titleName?.TrimStart('v') ?? "Unknown";
                LatestVersionText.Text = _latestVersion;
                StatusText.Text = $"Version found: {_latestVersion}";

                string releaseNotes = release["body"]?.ToString();
                UpdateDetailsText.Text += $"[{DateTime.Now}] Update found: {_latestVersion}\n";

                if (!string.IsNullOrEmpty(releaseNotes))
                {
                    UpdateDetailsText.Text += $"Release Notes:\n{releaseNotes}\n";
                }

                await LoadAvailableVersions(release);
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("404"))
            {
        StatusText.Text = "Error: Tag not found or API error.";
                UpdateDetailsText.Text += $"[{DateTime.Now}] Error: {ex.Message}\n";
                UpdateDetailsText.Text += "Tip: Check that the tag 'ignore' exists in your GitHub Releases.\n";
                ColorVersionDropdown.IsEnabled = false;
                DownloadUpdateButton.IsEnabled = false;
            }
            catch (Exception ex)
            {
                StatusText.Text = "Error checking for updates.";
                UpdateDetailsText.Text += $"[{DateTime.Now}] Error: {ex.Message}\n";
                ColorVersionDropdown.IsEnabled = false;
                DownloadUpdateButton.IsEnabled = false;
            }
            finally
            {
                CheckUpdateButton.IsEnabled = true;
            }
        }

        private async Task LoadAvailableVersions(JObject release)
        {
            _availableVersions.Clear();
            ColorVersionDropdown.ItemsSource = null;

            try
            {
                JArray assets = release["assets"] as JArray;
                if (assets != null)
                {
                    foreach (JToken asset in assets)
                    {
                        string name = asset["name"]?.ToString();
                        string downloadUrl = asset["browser_download_url"]?.ToString();

                        // Look for Spark theme files but exclude SparkTTSCache.zip
                        if (name != null && downloadUrl != null &&
                            name.StartsWith("Spark", StringComparison.OrdinalIgnoreCase) &&
                            name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                            !name.Equals("SparkTTSCache.zip", StringComparison.OrdinalIgnoreCase))
                        {
                            _availableVersions.Add(new ColorVersion
                            {
                                DisplayName = GetDisplayName(name),
                                FileName = name,
                                DownloadUrl = downloadUrl
                            });
                        }
                    }
                }

                if (_availableVersions.Count > 0)
                {
                    ColorVersionDropdown.ItemsSource = _availableVersions;
                    ColorVersionDropdown.DisplayMemberPath = "DisplayName";
                    ColorVersionDropdown.SelectedValuePath = "DownloadUrl";

                    ColorVersion defaultVersion = _availableVersions.FirstOrDefault(v =>
                        v.FileName.Equals("Spark.zip", StringComparison.OrdinalIgnoreCase));

                    ColorVersionDropdown.SelectedItem = defaultVersion ?? _availableVersions[0];
                    ColorVersionDropdown.IsEnabled = true;
                    DownloadUpdateButton.IsEnabled = true;

                    UpdateDetailsText.Text += $"[{DateTime.Now}] Found {_availableVersions.Count} theme version(s):\n";
                    foreach (ColorVersion version in _availableVersions)
                    {
                        UpdateDetailsText.Text += $"[{DateTime.Now}]   • {version.DisplayName} ({version.FileName})\n";
                    }
                }
                else
                {
                    StatusText.Text = "No theme versions found in release.";
                    ColorVersionDropdown.IsEnabled = false;
                    DownloadUpdateButton.IsEnabled = false;
                    UpdateDetailsText.Text += $"[{DateTime.Now}] Warning: No Spark*.zip theme files found in release\n";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error loading versions: {ex.Message}";
                UpdateDetailsText.Text += $"[{DateTime.Now}] Error loading versions: {ex.Message}\n";
            }
        }

        private string GetDisplayName(string fileName)
        {
            if (fileName.Equals("Spark.zip", StringComparison.OrdinalIgnoreCase))
                return "Default Theme";

            string baseName = Path.GetFileNameWithoutExtension(fileName);

            if (baseName.StartsWith("Spark", StringComparison.OrdinalIgnoreCase))
            {
                string themeName = baseName.Substring(5);
                return string.IsNullOrWhiteSpace(themeName) ? "Default Theme" : $"{themeName} Theme";
            }

            return baseName;
        }

        private void ColorVersionDropdown_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ColorVersionDropdown.SelectedItem is ColorVersion selected)
            {
                _selectedDownloadUrl = selected.DownloadUrl;
                UpdateDetailsText.Text += $"[{DateTime.Now}] Selected: {selected.DisplayName}\n";
            }
        }

        private async void DownloadUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            if (ColorVersionDropdown.SelectedItem is not ColorVersion selectedVersion)
            {
                StatusText.Text = "Please select a theme version first";
                return;
            }

            DownloadUpdateButton.IsEnabled = false;
            CheckUpdateButton.IsEnabled = false;
            ColorVersionDropdown.IsEnabled = false;
            UpdateProgressBar.Visibility = Visibility.Visible;
            UpdateProgressBar.Value = 0;

            StatusText.Text = $"Downloading {selectedVersion.DisplayName}...";
            UpdateDetailsText.Text += $"[{DateTime.Now}] Starting download of {selectedVersion.DisplayName}...\n";

            string tempFilePath = Path.Combine(_tempFolder, selectedVersion.FileName);

            try
            {
                await DownloadFileWithProgressAsync(selectedVersion.DownloadUrl, tempFilePath, (pct) =>
                {
                    UpdateProgressBar.Value = pct;
                    StatusText.Text = $"Downloading {selectedVersion.DisplayName}: {pct}%";
                });

                StatusText.Text = "Download complete. Extracting and installing...";
                UpdateDetailsText.Text += $"[{DateTime.Now}] Download complete. Extracting...\n";

                await Task.Run(() => InstallUpdate(tempFilePath, selectedVersion.FileName));
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
                UpdateDetailsText.Text += $"[{DateTime.Now}] Error downloading {selectedVersion.DisplayName}: {ex.Message}\n";
                ResetButtons();
            }
        }

        private async Task DownloadFileWithProgressAsync(string url, string destPath, Action<int> onProgress, CancellationToken ct = default)
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;

            await using Stream contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using FileStream fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            byte[] buffer = new byte[8192];
            long bytesRead = 0;
            int read;

            while ((read = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                bytesRead += read;

                if (totalBytes.HasValue)
                {
                    int pct = (int)(bytesRead * 100 / totalBytes.Value);
                    Dispatcher.Invoke(() => onProgress(pct));
                }
            }
        }

        private void InstallUpdate(string zipFilePath, string originalFileName)
        {
            try
            {
                string extractPath = Path.Combine(_tempFolder, "Spark_Extracted");

                if (Directory.Exists(extractPath))
                    Directory.Delete(extractPath, true);

                ZipFile.ExtractToDirectory(zipFilePath, extractPath);

                Dispatcher.Invoke(() =>
                {
                    UpdateDetailsText.Text += $"[{DateTime.Now}] Extracted to: {extractPath}\n";
                    StatusText.Text = "Finding actual files...";
                });

                string currentExe = Process.GetCurrentProcess().MainModule.FileName;
                string targetFolder = Path.GetDirectoryName(currentExe);
                string actualSourceFolder = FindActualSourceFolder(extractPath);

                Dispatcher.Invoke(() =>
                {
                    UpdateDetailsText.Text += $"[{DateTime.Now}] Target folder: {targetFolder}\n";
                    StatusText.Text = "Creating update script...";
                });

                string batchFile = Path.Combine(_tempFolder, "update_spark.bat");
                string batchContent = $@"
@echo off
setlocal enabledelayedexpansion
title Spark Updater

echo.
echo ============================================================
echo           SPARK UPDATE - {Path.GetFileNameWithoutExtension(originalFileName)}
echo ============================================================
echo.
echo Current folder: {targetFolder}
echo Source files:   {actualSourceFolder}
echo.

echo [1/4] Killing Spark...
taskkill /f /im Spark.exe >nul 2>&1
timeout /t 2 /nobreak >nul

echo [2/4] Cleaning old files...
cd /d ""{targetFolder}""
if errorlevel 1 (
    echo Error: Could not access target folder.
    pause
    exit
)

REM Delete all subdirectories except Temp (just in case)
for /d %%i in (*) do (
    if /i ""%%i"" neq ""Temp"" (
        echo   Deleting folder: %%i
        rmdir /s /q ""%%i"" >nul 2>&1
    )
)

REM Delete all files
echo   Deleting files...
del /f /q /s * >nul 2>&1

echo [3/4] Moving new files...
robocopy ""{actualSourceFolder}"" ""{targetFolder}"" /E /MOVE /IS /IT /MT:8 /R:3 /W:1 >nul

echo [4/4] Starting Spark...
cd /d ""{targetFolder}""
start """" Spark.exe

echo.
echo ============================================================
echo UPDATE COMPLETE!
echo ============================================================
timeout /t 2 /nobreak >nul

REM Delete this script and the temp extraction
if exist ""{extractPath}"" rmdir /s /q ""{extractPath}""
(goto) 2>nul & del ""%~f0""
exit
";
                File.WriteAllText(batchFile, batchContent);

                Dispatcher.Invoke(() =>
                {
                    UpdateDetailsText.Text += $"[{DateTime.Now}] Batch file created\n";
                    StatusText.Text = "Starting update...";

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = batchFile,
                        WindowStyle = ProcessWindowStyle.Normal,
                        UseShellExecute = true,
                        WorkingDirectory = _tempFolder
                    });

                    
                    Program.Quit();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = $"Installation failed: {ex.Message}";
                    UpdateDetailsText.Text += $"[{DateTime.Now}] Installation failed: {ex.Message}\n";
                    UpdateDetailsText.Text += $"[{DateTime.Now}] Stack: {ex.StackTrace}\n";
                    ResetButtons();
                });
            }
        }

        private string FindActualSourceFolder(string extractPath)
        {
            try
            {
                // Recursively search for Spark.exe to find the root folder of the application
                string[] files = Directory.GetFiles(extractPath, "Spark.exe", SearchOption.AllDirectories);
                if (files.Length > 0)
                {
                    string folder = Path.GetDirectoryName(files[0]);
                    if (!string.IsNullOrEmpty(folder))
                        return folder;
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    UpdateDetailsText.Text += $"[{DateTime.Now}] Error finding source folder: {ex.Message}\n";
                });
            }

            return extractPath;
        }

        private async void DownloadTTSCacheButton_Click(object sender, RoutedEventArgs e)
        {
            DownloadTTSCacheButton.IsEnabled = false;
            TTSCacheStatus.Text = "Downloading TTS Cache...";

            try
            {
                string json = await _httpClient.GetStringAsync("https://api.github.com/repos/heisthecat31/Spark/releases/latest");
                JObject release = JObject.Parse(json);

                JArray assets = release["assets"] as JArray;
                string ttsCacheUrl = assets?
                    .FirstOrDefault(a => a["name"]?.ToString().Equals("SparkTTSCache.zip", StringComparison.OrdinalIgnoreCase) == true)
                    ?["browser_download_url"]?.ToString();

                if (string.IsNullOrEmpty(ttsCacheUrl))
                    throw new Exception("SparkTTSCache.zip not found in release assets.");

                // Use the custom cache folder configured in settings, or the default Spark directory fallback
                string ttsCacheFolder = TTSController.CacheFolder;

                if (Directory.Exists(ttsCacheFolder))
                    Directory.Delete(ttsCacheFolder, true);

                Directory.CreateDirectory(ttsCacheFolder);

                string tempTtsZip = Path.Combine(ttsCacheFolder, "SparkTTSCache.zip");

                await DownloadFileWithProgressAsync(ttsCacheUrl, tempTtsZip, _ => { });

                ZipFile.ExtractToDirectory(tempTtsZip, ttsCacheFolder);
                File.Delete(tempTtsZip);

                TTSCacheStatus.Text = "TTS Cache downloaded successfully!";
                UpdateDetailsText.Text += $"[{DateTime.Now}] TTS Cache downloaded to: {ttsCacheFolder}\n";
                new MessageBox($"Success: TTS Cache downloaded and extracted to:\n{ttsCacheFolder}").Show();
            }
            catch (Exception ex)
            {
                TTSCacheStatus.Text = $"Error: {ex.Message}";
                UpdateDetailsText.Text += $"[{DateTime.Now}] TTS Cache error: {ex.Message}\n";
                new MessageBox($"Error downloading TTS Cache: {ex.Message}").Show();
            }
            finally
            {
                DownloadTTSCacheButton.IsEnabled = true;
            }
        }

        private async void DownloadHapticsFixButton_Click(object sender, RoutedEventArgs e)
        {
            if (SparkSettings.instance == null || string.IsNullOrEmpty(SparkSettings.instance.echoVRPath))
            {
                new MessageBox("Error: EchoVR Path is not set in Spark Settings. Please set it in the main settings first.").Show();
                return;
            }

            string echoDir = Path.GetDirectoryName(SparkSettings.instance.echoVRPath);
            if (!Directory.Exists(echoDir))
            {
                new MessageBox($"Error: EchoVR directory not found at:\n{echoDir}").Show();
                return;
            }

            DownloadHapticsFixButton.IsEnabled = false;
            HapticsFixStatus.Text = "Downloading Haptics Fix...";
            UpdateDetailsText.Text += $"[{DateTime.Now}] Starting Haptics Fix download...\n";

            try
            {
                string zipPath = Path.Combine(_tempFolder, "HapticsFix.zip");
                string extractPath = Path.Combine(_tempFolder, "HapticsFix_Extracted");

                await DownloadFileWithProgressAsync(
                    "https://github.com/heisthecat31/EchoVR-Haptics/releases/download/haptics/HapticsFix.zip",
                    zipPath,
                    _ => { });

                UpdateDetailsText.Text += $"[{DateTime.Now}] Download complete. Extracting...\n";

                if (Directory.Exists(extractPath))
                    Directory.Delete(extractPath, true);

                ZipFile.ExtractToDirectory(zipPath, extractPath);

                // Copy files
                foreach (string fileName in new[] { "dbgcore.dll", "haptics_config.txt" })
                {
                    string sourceFile = Path.Combine(extractPath, fileName);
                    string destFile = Path.Combine(echoDir, fileName);

                    if (File.Exists(sourceFile))
                    {
                        UpdateDetailsText.Text += $"[{DateTime.Now}] Copying {fileName} to {destFile}\n";
                        File.Copy(sourceFile, destFile, true);
                    }
                    else
                    {
                        UpdateDetailsText.Text += $"[{DateTime.Now}] Warning: {fileName} not found in zip.\n";
                    }
                }

                // Cleanup
                if (File.Exists(zipPath)) File.Delete(zipPath);
                if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);

                HapticsFixStatus.Text = "Haptics Fix installed successfully!";
                UpdateDetailsText.Text += $"[{DateTime.Now}] Haptics Fix installed successfully.\n";
                new MessageBox("Success: EchoVR Haptics Fix installed successfully!").Show();
            }
            catch (Exception ex)
            {
                HapticsFixStatus.Text = "Error installing fix.";
                UpdateDetailsText.Text += $"[{DateTime.Now}] Error installing Haptics Fix: {ex.Message}\n";
                new MessageBox($"Error installing Haptics Fix: {ex.Message}").Show();
            }
            finally
            {
                DownloadHapticsFixButton.IsEnabled = true;
            }
        }

        private void OpenTempFolderButton_Click(object sender, RoutedEventArgs e)
        {
            if (Directory.Exists(_tempFolder))
            {
                Process.Start("explorer.exe", _tempFolder);
            }
        }

        private void ResetButtons()
        {
            DownloadUpdateButton.IsEnabled = true;
            CheckUpdateButton.IsEnabled = true;
            ColorVersionDropdown.IsEnabled = true;
            UpdateProgressBar.Visibility = Visibility.Collapsed;
        }
    }
}