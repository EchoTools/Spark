using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;

namespace Spark
{
    /// <summary>
    /// Interaction logic for Portal.xaml
    /// </summary>
    public partial class Portal : UserControl
    {
        public Portal()
        {
            InitializeComponent();
            InitializeAsync();
        }

        async void InitializeAsync()
        {
            try
            {
                // Use a persistent User Data Folder to save login sessions/cookies
                string userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IgniteVR", "Spark", "WebView");
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);

                await PortalWebView.EnsureCoreWebView2Async(env);

                // Inject theme colors whenever the page finishes navigating
                PortalWebView.NavigationCompleted += PortalWebView_NavigationCompleted;

                // Navigate to the portal URL
                PortalWebView.Source = new Uri("https://echovrce.com/");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error initializing Portal WebView: {ex.Message}");
            }
        }

        private async void PortalWebView_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.IsSuccess)
            {
                await ApplyCurrentThemeToPage();
            }
        }

        /// <summary>
        /// Reads the current WPF theme resources and injects CSS into the WebView to match.
        /// Also aggressively monitors the page to replace the profile picture.
        /// </summary>
        private async Task ApplyCurrentThemeToPage()
        {
            try
            {
                // 1. Retrieve colors from the active ResourceDictionary
                string bgHex = GetThemeColorHex("BackgroundColour") ?? "#232323";
                string fgHex = GetThemeColorHex("ControlDefaultForeground") ?? "#EBEBEB";
                string accentHex = GetThemeColorHex("ControlPrimaryDefaultBackground") 
                                ?? GetThemeColorHex("ControlPrimaryColourBackground") 
                                ?? "#E85720"; 

                // 2. Get Discord Info (PFP and Username)
                string pfpUrl = "";
                string username = "";

                if (DiscordOAuth.discordUserData != null)
                {
                    if (DiscordOAuth.discordUserData.ContainsKey("avatar"))
                    {
                        pfpUrl = DiscordOAuth.DiscordPFPURL;
                        // Fix extension if missing
                        if (!string.IsNullOrEmpty(pfpUrl) && !pfpUrl.Contains("."))
                        {
                            pfpUrl += ".png";
                        }
                    }
                    
                    if (DiscordOAuth.discordUserData.ContainsKey("username"))
                    {
                        username = DiscordOAuth.DiscordUsername;
                    }
                }

                // 3. Javascript Injection
                // We use a MutationObserver to watch the DOM. This catches the PFP 
                // the moment it is rendered by the website's React/Vue/JS framework.
                string jsInjection = $@"
                    (function() {{
                        // --- PART A: Theme Injection ---
                        const styleId = 'spark-theme-override';
                        if (!document.getElementById(styleId)) {{
                            const style = document.createElement('style');
                            style.id = styleId;
                            style.innerHTML = `
                                body, .bg-\\[\\#292b32\\] {{
                                    background-color: {bgHex} !important;
                                    color: {fgHex} !important;
                                }}
                                .text-white {{ color: {fgHex} !important; }}
                                a {{ color: {accentHex} !important; }}
                            `;
                            document.head.appendChild(style);
                        }}

                        // --- PART B: Profile Picture Fixer ---
                        const targetPfp = '{pfpUrl}';
                        const targetUser = '{username}';

                        function fixProfileImages() {{
                            if (!targetPfp) return;

                            // 1. Try finding by ALT text (Most reliable for this specific site)
                            if (targetUser) {{
                                const imgByAlt = document.querySelector(`img[alt='${{targetUser}}']`);
                                if (imgByAlt && imgByAlt.src !== targetPfp) {{
                                    imgByAlt.src = targetPfp;
                                    // Optional: Force a small re-layout to ensure it renders
                                    imgByAlt.style.display = 'none';
                                    imgByAlt.offsetHeight; 
                                    imgByAlt.style.display = '';
                                }}
                            }}

                            // 2. Fallback: Try finding by the specific Tailwind classes 
                            // (Only matches if it looks like a user avatar)
                            const imgs = document.querySelectorAll('img.w-8.h-8.rounded-full');
                            imgs.forEach(img => {{
                                // Only replace if it looks like the broken default discord avatar
                                if (img.src.includes('cdn.discordapp.com/embed/avatars') && img.src !== targetPfp) {{
                                    img.src = targetPfp;
                                }}
                            }});
                        }}

                        // Run once immediately
                        fixProfileImages();

                        // Run continuously as the page changes
                        const observer = new MutationObserver((mutations) => {{
                            fixProfileImages();
                        }});
                        
                        observer.observe(document.body, {{ childList: true, subtree: true }});

                    }})();
                ";

                await PortalWebView.CoreWebView2.ExecuteScriptAsync(jsInjection);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to apply theme/fix PFP: {ex.Message}");
            }
        }

        private string GetThemeColorHex(string resourceKey)
        {
            try
            {
                if (Application.Current.Resources.Contains(resourceKey) && 
                    Application.Current.Resources[resourceKey] is SolidColorBrush brush)
                {
                    Color c = brush.Color;
                    return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                }
            }
            catch { }
            return null;
        }
    }
}