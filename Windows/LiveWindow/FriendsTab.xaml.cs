using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Spark
{
    public partial class FriendsTab : UserControl
    {
        private readonly ObservableCollection<FriendViewModel> friends = new ObservableCollection<FriendViewModel>();
        private bool isRunning = true;
        private bool isPublic = true;
        private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        // ─── Bot API — same Ignite API server, same Discord OAuth token ──────

        private static string BotUrl => SecretKeys.FRIENDS_BOT_URL;

        private static bool BotConfigured =>
            DiscordOAuth.IsLoggedIn &&
            !string.IsNullOrEmpty(DiscordOAuth.oauthToken);

        private static HttpRequestMessage MakeRequest(HttpMethod method, string path, object body = null)
        {
            var req = new HttpRequestMessage(method, BotUrl + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DiscordOAuth.oauthToken);
            if (body != null)
                req.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
            return req;
        }

        private static async Task<JObject> SendAsync(HttpRequestMessage req)
        {
            var resp = await http.SendAsync(req);
            string content = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) throw new Exception(content);
            return JObject.Parse(content);
        }

        // ─── Lifecycle ─────────────────────────────────────────────────────────

        public FriendsTab()
        {
            InitializeComponent();
            FriendsItemsControl.ItemsSource = friends;
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            // React when the user logs into Discord
            DiscordOAuth.Authenticated += () => Dispatcher.Invoke(async () => await RefreshAll());

            // Initialise immediately if already logged in
            Dispatcher.BeginInvoke(new Action(async () => await RefreshAll()));
        }

        // ─── Auth state ────────────────────────────────────────────────────────

        private async Task RefreshAll()
        {
            if (!BotConfigured)
            {
                ShowNotLoggedIn();
                return;
            }

            ShowLoggedIn();
            SetStatus(null, "Connecting...");
            await RegisterWithBot();
            await PollFriends();
            StartPolling();
        }

        private void ShowNotLoggedIn()
        {
            NotLoggedInPanel.Visibility = Visibility.Visible;
            FriendsSection.Visibility = Visibility.Collapsed;
            MyFriendCodeSection.Visibility = Visibility.Collapsed;
            SetStatus(false, "Log in with Discord to use Friends");
        }

        private void ShowLoggedIn()
        {
            NotLoggedInPanel.Visibility = Visibility.Collapsed;
            FriendsSection.Visibility = Visibility.Visible;

            if (!string.IsNullOrEmpty(SparkSettings.instance.myFriendCode))
            {
                MyFriendCodeText.Text = FormatCode(SparkSettings.instance.myFriendCode);
                MyFriendCodeSection.Visibility = Visibility.Visible;
            }
        }

        // ─── Bot API calls ─────────────────────────────────────────────────────

        private async Task RegisterWithBot()
        {
            try
            {
                string echoUsername = null;
                try { if (Program.lastFrame != null) echoUsername = Program.lastFrame.client_name; } catch { }

                using var req = MakeRequest(HttpMethod.Post, "/friends/register", new { echo_username = echoUsername });
                var result = await SendAsync(req);

                string code = result["friend_code"]?.ToString();
                if (!string.IsNullOrEmpty(code))
                {
                    SparkSettings.instance.myFriendCode = code;
                    SparkSettings.instance.Save();

                    Dispatcher.Invoke(() =>
                    {
                        MyFriendCodeText.Text = FormatCode(code);
                        MyFriendCodeSection.Visibility = Visibility.Visible;
                    });
                }

                // Initial visibility
                try
                {
                    using var lookReq = MakeRequest(HttpMethod.Get, $"/friends/lookup/{code}");
                    var lookRes = await SendAsync(lookReq);
                    if (lookRes["is_public"] != null) isPublic = lookRes["is_public"].Value<int>() == 1;
                    UpdateVisibilityUI();
                } catch { }

                SetStatus(true, "Connected");
            }
            catch (Exception ex)
            {
                Console.WriteLine("FriendsTab: Register error: " + ex.Message);
                SetStatus(false, "Could not reach Friends bot");
            }
        }

        private void StartPolling()
        {
            if (!isRunning) return;
            Task.Run(async () =>
            {
                while (isRunning)
                {
                    await Task.Delay(20000);
                    try { await PollFriends(); } catch { }
                }
            });
        }

        private async Task PollFriends()
        {
            if (!BotConfigured) return;

            try
            {
                using var req = MakeRequest(HttpMethod.Get, "/friends/list");
                var result = await SendAsync(req);
                var friendList = result["friends"] as JArray;

                Dispatcher.Invoke(() =>
                {
                    // Update existing, add new, remove old
                    var incomingCodes = friendList.Select(f => f["friend_code"]?.ToString()).ToList();
                    
                    // Remove
                    var toRemove = friends.Where(f => !incomingCodes.Contains(f.FriendCode)).ToList();
                    foreach (var f in toRemove) friends.Remove(f);

                    // Add/Update
                    foreach (var fJson in friendList)
                    {
                        string code = fJson["friend_code"]?.ToString();
                        var friend = friends.FirstOrDefault(x => x.FriendCode == code);
                        if (friend == null)
                        {
                            friend = new FriendViewModel { FriendCode = code };
                            friends.Add(friend);
                        }

                        friend.Name = fJson["echo_username"]?.ToString() 
                                    ?? fJson["discord_username"]?.ToString() 
                                    ?? code;
                        
                        bool online = fJson["online"]?.Value<bool>() ?? false;
                        if (online)
                            friend.UpdateStatus(fJson["lobby_id"]?.ToString(), fJson["team"]?.ToString(), fJson["mode"]?.ToString());
                        else
                            friend.SetOffline();
                    }

                    UpdateOnlineCount();
                    UpdateEmptyHint();
                });
            }
            catch (Exception ex) { Console.WriteLine("PollFriends error: " + ex.Message); }
        }

        // ─── Friend Actions ───────────────────────────────────────────────────

        private void AddFriend()
        {
            string code = AddFriendCodeBox.Text.Trim().ToUpper().Replace("-", "").Replace(" ", "");
            if (string.IsNullOrEmpty(code) || code.Length != 8) return;

            Task.Run(async () =>
            {
                try
                {
                    using var req = MakeRequest(HttpMethod.Post, $"/friends/add/{code}");
                    await SendAsync(req);
                    Dispatcher.Invoke(() => AddFriendCodeBox.Text = "");
                    await PollFriends();
                }
                catch (Exception ex) 
                { 
                    Dispatcher.Invoke(() => new MessageBox("Error: " + ex.Message, "Error").Show()); 
                }
            });
        }

        private void RemoveFriendClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string code)
            {
                Task.Run(async () =>
                {
                    try
                    {
                        using var req = MakeRequest(HttpMethod.Delete, $"/friends/remove/{code}");
                        await SendAsync(req);
                        await PollFriends();
                    } catch { }
                });
            }
        }

        // ─── Other UI ─────────────────────────────────────────────────────────

        private async void VisibilityToggleClicked(object sender, RoutedEventArgs e)
        {
            if (!BotConfigured) return;
            bool newState = !isPublic;
            try
            {
                using var req = MakeRequest(HttpMethod.Post, "/friends/visibility", new { @public = newState });
                await SendAsync(req);
                isPublic = newState;
                UpdateVisibilityUI();
            } catch { }
        }

        private void UpdateVisibilityUI()
        {
            Dispatcher.Invoke(() =>
            {
                VisibilityDot.Fill = isPublic ? new SolidColorBrush(Color.FromRgb(76, 175, 80)) : new SolidColorBrush(Color.FromRgb(244, 67, 54));
                VisibilityButtonText.Text = isPublic ? "Visible — Click to Hide" : "Hidden — Click to Show";
                VisibilityDescription.Text = isPublic ? "You are visible to friends." : "You are hidden. Friends see you as offline.";
            });
        }

        private async void JoinFriendClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is FriendViewModel friend)
            {
                if (string.IsNullOrEmpty(friend.SessionId)) return;
                bool ok = await TryApiJoin(friend.SessionId);
                if (!ok) ShowTeamChooser(friend);
            }
        }

        private async Task<bool> TryApiJoin(string sessionId)
        {
            try
            {
                string echoIp = SparkSettings.instance.echoVRIP ?? "127.0.0.1";
                int echoPort = SparkSettings.instance.echoVRPort;
                using var joinReq = new HttpRequestMessage(HttpMethod.Post, $"http://{echoIp}:{echoPort}/join_session");
                joinReq.Content = new StringContent(JsonConvert.SerializeObject(new { session_id = sessionId.ToUpper(), password = "" }), Encoding.UTF8, "application/json");
                await http.SendAsync(joinReq);
                return true;
            } catch { return false; }
        }

        private void ShowTeamChooser(FriendViewModel friend)
        {
            var win = new Window { Title = $"Join {friend.Name}", Width = 280, Height = 220, WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = (Brush)new BrushConverter().ConvertFrom("#121212"), ResizeMode = ResizeMode.NoResize, Topmost = true };
            var stack = new StackPanel { Margin = new Thickness(20) };
            stack.Children.Add(new TextBlock { Text = $"Join {friend.Name} as:", Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14) });
            foreach (var (label, hex, arg) in new[] { ("Blue", "#3B82F6", "blue"), ("Orange", "#F59E0B", "orange"), ("Spectator", "#6B7280", "spectator") })
            {
                var teamArg = arg;
                var btn = new Button { Content = label, Height = 34, Margin = new Thickness(0, 0, 0, 8), Background = (SolidColorBrush)new BrushConverter().ConvertFrom(hex), Foreground = Brushes.White, FontWeight = FontWeights.Bold, BorderThickness = new Thickness(0) };
                btn.Click += (s, ev) => { LaunchEcho(friend.SessionId, teamArg); win.Close(); };
                stack.Children.Add(btn);
            }
            win.Content = stack;
            win.ShowDialog();
        }

        private void LaunchEcho(string sessionId, string team)
        {
            try
            {
                string lobbyId = sessionId.Split('.')[0];
                string args = team == "spectator" ? $"-lobbyid {lobbyId} -spectatorstream" : $"-lobbyid {lobbyId} -lobbyteam {team}";
                System.Diagnostics.Process.Start(SparkSettings.instance.echoVRPath, args);
            } catch { }
        }

        public static async Task PushLobbyUpdate(string lobbyId, string team, string mode)
        {
            if (!DiscordOAuth.IsLoggedIn || string.IsNullOrEmpty(DiscordOAuth.oauthToken)) return;
            try { using var req = MakeRequest(HttpMethod.Post, "/friends/lobby", new { lobby_id = lobbyId, team, mode }); await http.SendAsync(req); } catch { }
        }

        public static async Task PushOffline()
        {
            if (!DiscordOAuth.IsLoggedIn || string.IsNullOrEmpty(DiscordOAuth.oauthToken)) return;
            try { using var req = MakeRequest(HttpMethod.Post, "/friends/offline"); await http.SendAsync(req); } catch { }
        }

        private void AddFriendKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) AddFriend(); }
        private void AddFriendClicked(object sender, RoutedEventArgs e) => AddFriend();
        private void CopyCodeClicked(object sender, RoutedEventArgs e) { try { Clipboard.SetText(SparkSettings.instance.myFriendCode ?? ""); } catch { } }
        private void RefreshClicked(object sender, RoutedEventArgs e) => Task.Run(() => PollFriends());
        private void UpdateOnlineCount() { int n = friends.Count(f => f.IsOnline); OnlineCountBadge.Visibility = Visibility.Visible; OnlineCountText.Text = $"{n} Online"; }
        private void UpdateEmptyHint() { if (EmptyFriendsHint != null) EmptyFriendsHint.Visibility = friends.Count == 0 ? Visibility.Visible : Visibility.Collapsed; }
        private void SetStatus(bool? ok, string text) { Dispatcher.Invoke(() => { ApiStatusText.Text = text; ApiStatusDot.Fill = new SolidColorBrush(ok == null ? Color.FromRgb(255, 165, 0) : ok == true ? Color.FromRgb(76, 175, 80) : Color.FromRgb(244, 67, 54)); }); }
        private string FormatCode(string code) => code?.Length == 8 ? code.Substring(0, 4) + " " + code.Substring(4) : (code ?? "");
        public void Shutdown() => isRunning = false;
    }

    public class FriendViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        private string name = "", friendCode = "", statusColor = "#444444", teamColor = "#444444";
        private string modeText = "Offline", separatorText = "", sessionTypeText = "", statusBadgeText = "", sessionId = "", sessionIdText = "";
        private bool isOnline = false;
        private double opacity = 0.6;

        public string FriendCode  { get => friendCode;  set { friendCode  = value; Notify(); } }
        public string Name        { get => string.IsNullOrEmpty(name) ? FriendCode : name; set { name = value; Notify(); } }
        public string StatusColor { get => statusColor; set { statusColor = value; Notify(); } }
        public string TeamColor   { get => teamColor;   set { teamColor   = value; Notify(); } }
        public string ModeText    { get => modeText;    set { modeText    = value; Notify(); } }
        public string SeparatorText   { get => separatorText;   set { separatorText   = value; Notify(); } }
        public string SessionTypeText { get => sessionTypeText; set { sessionTypeText = value; Notify(); } }
        public string StatusBadgeText { get => statusBadgeText; set { statusBadgeText = value; Notify(); } }
        public string SessionId   { get => sessionId;   set { sessionId   = value; Notify(); } }
        public string SessionIdText { get => sessionIdText; set { sessionIdText = value; Notify(); } }
        public bool   IsOnline    { get => isOnline;    set { isOnline    = value; Notify(); NotifyMultiple("ShowJoinButton","ShowStatusBadge","IsJoinable"); } }
        public double Opacity     { get => opacity;     set { opacity     = value; Notify(); } }

        public Visibility ShowStatusBadge => IsOnline ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ShowJoinButton  => IsOnline ? Visibility.Visible : Visibility.Collapsed;
        public bool       IsJoinable      => IsOnline && !string.IsNullOrEmpty(SessionId);

        public void UpdateStatus(string lobbyId, string team, string mode)
        {
            IsOnline = true; Opacity = 1.0; StatusColor = "#4CAF50"; SessionId = lobbyId ?? "";
            string raw = (mode ?? "Unknown").Replace("_", " ");
            ModeText = raw.Length > 0 ? char.ToUpper(raw[0]) + raw.Substring(1).ToLower() : "Unknown";
            SeparatorText = "  •  "; SessionTypeText = "In Match";
            StatusBadgeText = (team ?? "Lobby").ToUpper();
            TeamColor = StatusBadgeText == "BLUE" ? "#3B82F6" : StatusBadgeText == "ORANGE" ? "#F59E0B" : "#444444";
            SessionIdText = "Session: " + (lobbyId?.Length > 12 ? lobbyId.Substring(0, 12) + "..." : lobbyId);
        }

        public void SetOffline()
        {
            IsOnline = false; Opacity = 0.6; StatusColor = "#444444"; TeamColor = "#444444";
            ModeText = "Offline"; SeparatorText = ""; SessionTypeText = ""; StatusBadgeText = ""; SessionId = ""; SessionIdText = "";
        }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        private void Notify([System.Runtime.CompilerServices.CallerMemberName] string p = null) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(p));
        private void NotifyMultiple(params string[] props) { foreach (var p in props) PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(p)); }
    }
}
