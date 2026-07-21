using System;
using System.IO;
using System.IO.Compression;
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

                StatusText.Text = "Download complete. Extracting and installing...";
                
                await Task.Run(() => InstallUpdate(tempFilePath, _zipFileName));
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Update failed: {ex.Message}", "Error Updating", MessageBoxButton.OK, MessageBoxImage.Error);
                
                ActionButtons.Visibility = Visibility.Visible;
                ChangelogBorder.Visibility = Visibility.Visible;
                ProgressArea.Visibility = Visibility.Collapsed;
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

                string currentExe = Process.GetCurrentProcess().MainModule.FileName;
                string targetFolder = Path.GetDirectoryName(currentExe);

                string actualSourceFolder = FindActualSourceFolder(extractPath);
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

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = batchFile,
                    WindowStyle = ProcessWindowStyle.Normal,
                    UseShellExecute = true,
                    WorkingDirectory = _tempFolder
                };

                Dispatcher.Invoke(() =>
                {
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
                    System.Windows.MessageBox.Show($"Installation failed: {ex.Message}", "Installation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    
                    ActionButtons.Visibility = Visibility.Visible;
                    ChangelogBorder.Visibility = Visibility.Visible;
                    ProgressArea.Visibility = Visibility.Collapsed;
                });
            }
        }

        private string FindActualSourceFolder(string extractPath)
        {
            try
            {
                string[] files = Directory.GetFiles(extractPath, "Spark.exe", SearchOption.AllDirectories);
                if (files.Length > 0)
                {
                    string folder = Path.GetDirectoryName(files[0]);
                    if (!string.IsNullOrEmpty(folder))
                    {
                        return folder;
                    }
                }
            }
            catch
            {
            }

            return extractPath;
        }
    }
}
