using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Spark
{
    public partial class UpdateSparkControl : UserControl
    {
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
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }

private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
{
    CheckUpdateButton.IsEnabled = false;
    StatusText.Text = "Checking for updates...";
    UpdateDetailsText.Text += $"[{DateTime.Now}] Checking for updates...\n";

    try
    {
        using (var client = new WebClient())
        {
            client.Headers.Add("User-Agent", "Spark-Updater");
            client.Headers.Add("Accept", "application/vnd.github.v3+json");

            // URL for the specific 'ignore' tag
            string latestReleaseUrl = "https://api.github.com/repos/heisthecat31/Spark/releases/tags/ignore";
            
            // Download the JSON
            string json = await client.DownloadStringTaskAsync(latestReleaseUrl);

            // 1. CREATE the 'release' variable here
            var release = JObject.Parse(json);
            
            // 2. NOW you can use it. 
            // We try to grab the "name" (Release Title) first.
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
    }
    catch (Exception ex)
    {
        StatusText.Text = "Error: Tag not found or API error.";
        UpdateDetailsText.Text += $"[{DateTime.Now}] Error: {ex.Message}\n";
        
        if (ex.Message.Contains("404")) {
            UpdateDetailsText.Text += "Tip: Check that the tag 'ignore' exists in your GitHub Releases.\n";
        }
        
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
                var assets = release["assets"] as JArray;
                if (assets != null)
                {
                    foreach (var asset in assets)
                    {
                        string name = asset["name"]?.ToString();
                        string downloadUrl = asset["browser_download_url"]?.ToString();

                        // Look for Spark theme files but exclude SparkTTSCache.zip
                        if (name != null && downloadUrl != null && 
                            name.StartsWith("Spark", StringComparison.OrdinalIgnoreCase) && 
                            name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                            !name.Equals("SparkTTSCache.zip", StringComparison.OrdinalIgnoreCase))
                        {
                            string displayName = GetDisplayName(name);
                            _availableVersions.Add(new ColorVersion
                            {
                                DisplayName = displayName,
                                FileName = name,
                                DownloadUrl = downloadUrl
                            });
                        }
                    }
                }

                if (_availableVersions.Count > 0)
                {
                    Dispatcher.Invoke(() =>
                    {
                        ColorVersionDropdown.ItemsSource = _availableVersions;
                        ColorVersionDropdown.DisplayMemberPath = "DisplayName";
                        ColorVersionDropdown.SelectedValuePath = "DownloadUrl";

                        var defaultVersion = _availableVersions.FirstOrDefault(v =>
                            v.FileName.Equals("Spark.zip", StringComparison.OrdinalIgnoreCase));

                        if (defaultVersion != null)
                        {
                            ColorVersionDropdown.SelectedItem = defaultVersion;
                        }
                        else
                        {
                            ColorVersionDropdown.SelectedIndex = 0;
                        }

                        ColorVersionDropdown.IsEnabled = true;
                        DownloadUpdateButton.IsEnabled = true;

                        UpdateDetailsText.Text += $"[{DateTime.Now}] Found {_availableVersions.Count} theme version(s):\n";
                        foreach (var version in _availableVersions)
                        {
                            UpdateDetailsText.Text += $"[{DateTime.Now}]   • {version.DisplayName} ({version.FileName})\n";
                        }
                    });
                }
                else
                {
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = "No theme versions found in release";
                        ColorVersionDropdown.IsEnabled = false;
                        DownloadUpdateButton.IsEnabled = false;
                        UpdateDetailsText.Text += $"[{DateTime.Now}] Warning: No Spark*.zip theme files found in release\n";
                    });
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = $"Error loading versions: {ex.Message}";
                    UpdateDetailsText.Text += $"[{DateTime.Now}] Error loading versions: {ex.Message}\n";
                });
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
                if (string.IsNullOrWhiteSpace(themeName))
                    return "Default Theme";
                    
                return $"{themeName} Theme";
            }
            
            return baseName;
        }

        private void ColorVersionDropdown_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ColorVersionDropdown.SelectedItem != null)
            {
                var selectedVersion = (ColorVersion)ColorVersionDropdown.SelectedItem;
                _selectedDownloadUrl = selectedVersion.DownloadUrl;
                UpdateDetailsText.Text += $"[{DateTime.Now}] Selected: {selectedVersion.DisplayName}\n";
            }
        }

        private async void DownloadUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            if (ColorVersionDropdown.SelectedItem == null)
            {
                StatusText.Text = "Please select a theme version first";
                return;
            }

            var selectedVersion = (ColorVersion)ColorVersionDropdown.SelectedItem;

            try
            {
                DownloadUpdateButton.IsEnabled = false;
                CheckUpdateButton.IsEnabled = false;
                ColorVersionDropdown.IsEnabled = false;
                UpdateProgressBar.Visibility = Visibility.Visible;
                UpdateProgressBar.Value = 0;

                StatusText.Text = $"Downloading {selectedVersion.DisplayName}...";
                UpdateDetailsText.Text += $"[{DateTime.Now}] Starting download of {selectedVersion.DisplayName}...\n";

                string tempFilePath = Path.Combine(_tempFolder, selectedVersion.FileName);

                using (var client = new WebClient())
                {
                    client.Headers.Add("User-Agent", "Spark-Updater");
                    
                    client.DownloadProgressChanged += (s, args) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            UpdateProgressBar.Value = args.ProgressPercentage;
                            StatusText.Text = $"Downloading {selectedVersion.DisplayName}: {args.ProgressPercentage}%";
                        });
                    };

                    await client.DownloadFileTaskAsync(new Uri(_selectedDownloadUrl), tempFilePath);

                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = "Download complete. Extracting and installing...";
                        UpdateDetailsText.Text += $"[{DateTime.Now}] Download complete. Extracting...\n";
                    });

                    await Task.Run(() => InstallUpdate(tempFilePath, selectedVersion.FileName));
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = $"Error: {ex.Message}";
                    UpdateDetailsText.Text += $"[{DateTime.Now}] Error downloading {selectedVersion.DisplayName}: {ex.Message}\n";
                    ResetButtons();
                });
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

                Dispatcher.Invoke(() =>
                {
                    UpdateDetailsText.Text += $"[{DateTime.Now}] Target folder: {targetFolder}\n";
                    StatusText.Text = "Creating update script...";
                });

                string actualSourceFolder = FindActualSourceFolder(extractPath);

                string batchFile = Path.Combine(_tempFolder, "update_spark.bat");

                string batchContent = $@"
@echo off
echo ========================================
echo         SPARK UPDATE - {Path.GetFileNameWithoutExtension(originalFileName)}
echo ========================================
echo.
echo Current Spark folder: {targetFolder}
echo Source update files: {actualSourceFolder}
echo.
echo Step 1: Killing Spark...
taskkill /f /im Spark.exe >nul 2>&1
timeout /t 2 /nobreak >nul

echo Step 2: Copying ALL files...
echo FROM: {actualSourceFolder}
echo TO: {targetFolder}
echo.

REM Copy EVERYTHING from source to target
xcopy ""{actualSourceFolder}"" ""{targetFolder}"" /E /Y /I /H

echo Step 3: Cleaning up...
if exist ""{extractPath}"" rmdir /s /q ""{extractPath}""
if exist ""{zipFilePath}"" del ""{zipFilePath}"" >nul 2>&1

echo Step 4: Starting Spark...
cd /d ""{targetFolder}""
start """" Spark.exe

echo Step 5: Deleting this script...
timeout /t 1 /nobreak >nul
del ""{batchFile}"" >nul 2>&1

echo.
echo UPDATE COMPLETE! Launched {Path.GetFileNameWithoutExtension(originalFileName)} theme.
timeout /t 2 /nobreak >nul
exit
";

                File.WriteAllText(batchFile, batchContent);

                Dispatcher.Invoke(() =>
                {
                    UpdateDetailsText.Text += $"[{DateTime.Now}] Batch file created\n";
                    StatusText.Text = "Starting update...";
                });

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = batchFile,
                    WindowStyle = ProcessWindowStyle.Normal,
                    UseShellExecute = true,
                    WorkingDirectory = _tempFolder
                };

                Dispatcher.Invoke(() =>
                {
                    UpdateDetailsText.Text += $"[{DateTime.Now}] Launching update...\n";
                    
                    Process.Start(psi);
                    
                    Task.Delay(500).ContinueWith(t =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            Process.GetCurrentProcess().Kill();
                        });
                    });
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
            string[] possibleFolders = {
                Path.Combine(extractPath, "net6.0-windows10.0.17763.0"),
                Path.Combine(extractPath, "net6.0-windows"),
                extractPath
            };

            foreach (string folder in possibleFolders)
            {
                if (Directory.Exists(folder) && Directory.GetFiles(folder, "*.exe").Any())
                {
                    return folder;
                }
            }

            return extractPath;
        }

        private async void DownloadTTSCacheButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                DownloadTTSCacheButton.IsEnabled = false;
                TTSCacheStatus.Text = "Downloading TTS Cache...";

                string apiUrl = "https://api.github.com/repos/heisthecat31/Spark/releases/latest";
                using (var client = new WebClient())
                {
                    client.Headers.Add("User-Agent", "Spark-Updater");
                    string json = await client.DownloadStringTaskAsync(apiUrl);
                    var release = JObject.Parse(json);

                    var assets = release["assets"] as JArray;
                    string ttsCacheUrl = "";

                    if (assets != null)
                    {
                        foreach (var asset in assets)
                        {
                            string name = asset["name"]?.ToString();
                            if (name != null && name.Equals("SparkTTSCache.zip", StringComparison.OrdinalIgnoreCase))
                            {
                                ttsCacheUrl = asset["browser_download_url"]?.ToString();
                                break;
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(ttsCacheUrl))
                    {
                        throw new Exception("SparkTTSCache.zip not found in release assets");
                    }

                    // Use the custom cache folder configured in settings, or the default Spark directory fallback
                    string ttsCacheFolder = TTSController.CacheFolder;

                    if (Directory.Exists(ttsCacheFolder))
                    {
                        Directory.Delete(ttsCacheFolder, true);
                    }

                    Directory.CreateDirectory(ttsCacheFolder);

                    string tempTtsZip = Path.Combine(ttsCacheFolder, "SparkTTSCache.zip");

                    await client.DownloadFileTaskAsync(new Uri(ttsCacheUrl), tempTtsZip);

                    ZipFile.ExtractToDirectory(tempTtsZip, ttsCacheFolder);
                    File.Delete(tempTtsZip);

                    Dispatcher.Invoke(() =>
                    {
                        TTSCacheStatus.Text = "TTS Cache downloaded successfully!";
                        UpdateDetailsText.Text += $"[{DateTime.Now}] TTS Cache downloaded to: {ttsCacheFolder}\n";
                        new MessageBox($"Success: TTS Cache downloaded and extracted to:\n{ttsCacheFolder}").Show();
                    });
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    TTSCacheStatus.Text = $"Error: {ex.Message}";
                    UpdateDetailsText.Text += $"[{DateTime.Now}] TTS Cache error: {ex.Message}\n";
                    new MessageBox($"Error downloading TTS Cache: {ex.Message}").Show();
                });
            }
            finally
            {
                DownloadTTSCacheButton.IsEnabled = true;
            }
        }

        private async void DownloadHapticsFixButton_Click(object sender, RoutedEventArgs e)
        {
            try
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

                string url = "https://github.com/heisthecat31/EchoVR-Haptics/releases/download/haptics/HapticsFix.zip";
                string zipPath = Path.Combine(_tempFolder, "HapticsFix.zip");
                string extractPath = Path.Combine(_tempFolder, "HapticsFix_Extracted");

                using (WebClient client = new WebClient())
                {
                    client.Headers.Add("User-Agent", "Spark-Updater");
                    await client.DownloadFileTaskAsync(new Uri(url), zipPath);
                }

                UpdateDetailsText.Text += $"[{DateTime.Now}] Download complete. Extracting...\n";

                if (Directory.Exists(extractPath))
                    Directory.Delete(extractPath, true);
                
                ZipFile.ExtractToDirectory(zipPath, extractPath);

                // Copy files
                string[] filesToCopy = { "dbgcore.dll", "haptics_config.txt" };
                foreach (string fileName in filesToCopy)
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