using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace Spark
{
    public partial class WebBrowserTab : UserControl
    {
        public WebBrowserTab()
        {
            InitializeComponent();
            InitializeWebView();
        }

        private async void InitializeWebView()
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IgniteVR", "Spark", "WebViewBrowser");
                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, path);
                await WebView.EnsureCoreWebView2Async(env);
                
                string homeUrl = SparkSettings.instance.webBrowserHomeURL;
                
                // If it's still the old default (Google) or empty, update it to the new default
                if (string.IsNullOrEmpty(homeUrl) || homeUrl == "https://www.google.com")
                {
                    homeUrl = "https://ignitevr.gg/spark";
                    SparkSettings.instance.webBrowserHomeURL = homeUrl;
                    SparkSettings.instance.Save();
                }

                WebView.Source = new Uri(homeUrl);
                AddressBar.Text = homeUrl;

                WebView.CoreWebView2.SourceChanged += (s, e) =>
                {
                    if (WebView.Source != null)
                    {
                        AddressBar.Text = WebView.Source.ToString();
                    }
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error initializing WebView2: " + ex.Message);
            }
        }

        private void GoClicked(object sender, RoutedEventArgs e)
        {
            Navigate();
        }

        private void AddressBarKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                Navigate();
            }
        }

        private void Navigate()
        {
            string url = AddressBar.Text;
            if (string.IsNullOrWhiteSpace(url)) return;

            if (!url.Contains(".") && !url.Contains("://"))
            {
                // Probably a search query
                url = "https://www.google.com/search?q=" + Uri.EscapeDataString(url);
            }
            else if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            {
                url = "https://" + url;
            }

            try
            {
                WebView.Source = new Uri(url);
            }
            catch (Exception)
            {
                // Fallback to search
                WebView.Source = new Uri("https://www.google.com/search?q=" + Uri.EscapeDataString(AddressBar.Text));
            }
        }

        private void BackClicked(object sender, RoutedEventArgs e)
        {
            if (WebView.CanGoBack) WebView.GoBack();
        }

        private void ForwardClicked(object sender, RoutedEventArgs e)
        {
            if (WebView.CanGoForward) WebView.GoForward();
        }

        private void PresetClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string url)
            {
                try
                {
                    WebView.Source = new Uri(url);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error navigating to preset: " + ex.Message);
                }
            }
        }

        private void InfoLinkClicked(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Documents.Hyperlink link && link.Tag is string url)
            {
                try
                {
                    WebView.Source = new Uri(url);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error navigating to info link: " + ex.Message);
                }
            }
        }

        private void SaveHomeClicked(object sender, RoutedEventArgs e)
        {
            if (WebView.Source != null)
            {
                SparkSettings.instance.webBrowserHomeURL = WebView.Source.ToString();
                SparkSettings.instance.Save();
                new MessageBox("Default browser page saved!", "Success").Show();
            }
        }
    }
}
