using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
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
        private readonly object stateLock = new object();
        private bool isRefreshing = false;
        private bool isPollingActive = false;

        // ─── Lifecycle ─────────────────────────────────────────────────────────

        public FriendsTab()
        {
            InitializeComponent();
            FriendsItemsControl.ItemsSource = friends;

            // React when the user logs into Discord - registered ONCE in constructor to avoid event handler leak
            DiscordOAuth.Authenticated += () => Task.Run(async () => await RefreshAll());
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            // Initialise immediately if already logged in
            Task.Run(async () => await RefreshAll());
        }

        // ─── Auth state ────────────────────────────────────────────────────────

        private async Task RefreshAll()
        {
            lock (stateLock)
            {
                if (isRefreshing) return;
                isRefreshing = true;
            }

            try
            {
                if (!FriendsPresence.Configured)
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
            finally
            {
                lock (stateLock)
                {
                    isRefreshing = false;
                }
            }
        }

        private void ShowNotLoggedIn()
        {
            Dispatcher.Invoke(() =>
            {
                NotLoggedInPanel.Visibility = Visibility.Visible;
                FriendsSection.Visibility = Visibility.Collapsed;
                MyFriendCodeSection.Visibility = Visibility.Collapsed;
                SetStatus(false, "Log in with Discord to use Friends");
            });
        }

        private void ShowLoggedIn()
        {
            Dispatcher.Invoke(() =>
            {
                NotLoggedInPanel.Visibility = Visibility.Collapsed;
                FriendsSection.Visibility = Visibility.Visible;

                if (!string.IsNullOrEmpty(SparkSettings.instance.myFriendCode))
                {
                    MyFriendCodeText.Text = FormatCode(SparkSettings.instance.myFriendCode);
                    MyFriendCodeSection.Visibility = Visibility.Visible;
                }
            });
        }

        // ─── Bot API calls ─────────────────────────────────────────────────────

        private async Task RegisterWithBot()
        {
            try
            {
                // Shared with the presence pusher, so opening the tab doesn't register a second time.
                string code = await FriendsPresence.EnsureRegisteredAsync();
                if (string.IsNullOrEmpty(code))
                {
                    // EnsureRegisteredAsync reports failure as a null code rather than throwing.
                    SetStatus(false, "Could not reach Friends bot");
                    return;
                }

                Dispatcher.Invoke(() =>
                {
                    MyFriendCodeText.Text = FormatCode(code);
                    MyFriendCodeSection.Visibility = Visibility.Visible;
                });

                // Initial visibility
                try
                {
                    JObject lookRes = await FriendsPresence.LookupAsync(code);
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
            lock (stateLock)
            {
                if (isPollingActive || !isRunning) return;
                isPollingActive = true;
            }

            Task.Run(async () =>
            {
                try
                {
                    while (isRunning)
                    {
                        await Task.Delay(2500);
                        try { await PollFriends(); } catch { }
                    }
                }
                finally
                {
                    lock (stateLock)
                    {
                        isPollingActive = false;
                    }
                }
            });
        }

        private async Task PollFriends()
        {
            if (!FriendsPresence.Configured) return;

            try
            {
                JArray friendList = await FriendsPresence.GetFriendsAsync();
                if (friendList == null) return;

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
                        {
                            friend.UpdateStatus(
                                fJson["lobby_id"]?.ToString(),
                                fJson["team"]?.ToString(),
                                fJson["mode"]?.ToString(),
                                fJson["session_type"]?.ToString());
                        }
                        else
                        {
                            friend.SetOffline();
                        }
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
                    await FriendsPresence.AddFriendAsync(code);
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
                        await FriendsPresence.RemoveFriendAsync(code);
                        await PollFriends();
                    } catch { }
                });
            }
        }

        // ─── Other UI ─────────────────────────────────────────────────────────

        private async void VisibilityToggleClicked(object sender, RoutedEventArgs e)
        {
            if (!FriendsPresence.Configured) return;
            bool newState = !isPublic;
            try
            {
                await FriendsPresence.SetVisibilityAsync(newState);
                isPublic = newState;
                UpdateVisibilityUI();
            } catch { }
        }

        private void UpdateVisibilityUI()
        {
            Dispatcher.Invoke(() =>
            {
                // SetResourceReference rather than FindResource: this keeps a live link to the theme
                // brush, so the dot recolours with everything else when the theme changes.
                VisibilityDot.SetResourceReference(Shape.FillProperty, isPublic ? "StatusGood" : "StatusBad");
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
            var win = new Window
            {
                Title = $"Join {friend.Name}",
                Width = 280,
                Height = 220,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true
            };
            win.SetResourceReference(Control.BackgroundProperty, "SurfaceGround");

            var stack = new StackPanel { Margin = new Thickness(20) };
            var heading = new TextBlock
            {
                Text = $"Join {friend.Name} as:",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 14)
            };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            stack.Children.Add(heading);

            foreach (var (label, brushKey, foregroundKey, arg) in new[]
                     {
                         ("Blue", "TeamBlue", "TeamBlueForeground", "blue"),
                         ("Orange", "TeamOrange", "TeamOrangeForeground", "orange"),
                         ("Spectator", "SurfaceRaised", "TextPrimary", "spectator"),
                     })
            {
                var teamArg = arg;
                var btn = new Button
                {
                    Content = label,
                    Height = 34,
                    Margin = new Thickness(0, 0, 0, 8),
                    FontWeight = FontWeights.Bold,
                    BorderThickness = new Thickness(0)
                };
                btn.SetResourceReference(Control.BackgroundProperty, brushKey);
                btn.SetResourceReference(Control.ForegroundProperty, foregroundKey);
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

        private void AddFriendKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) AddFriend(); }
        private void AddFriendClicked(object sender, RoutedEventArgs e) => AddFriend();
        private void CopyCodeClicked(object sender, RoutedEventArgs e) { try { Clipboard.SetText(SparkSettings.instance.myFriendCode ?? ""); } catch { } }
        private void RefreshClicked(object sender, RoutedEventArgs e) => Task.Run(() => PollFriends());
        private void UpdateOnlineCount() { int n = friends.Count(f => f.IsOnline); OnlineCountBadge.Visibility = Visibility.Visible; OnlineCountText.Text = $"{n} Online"; }
        private void UpdateEmptyHint() { if (EmptyFriendsHint != null) EmptyFriendsHint.Visibility = friends.Count == 0 ? Visibility.Visible : Visibility.Collapsed; }

        private void SetStatus(bool? ok, string text)
        {
            Dispatcher.Invoke(() =>
            {
                ApiStatusText.Text = text;
                ApiStatusDot.SetResourceReference(Shape.FillProperty, ok == null ? "StatusWarn" : ok == true ? "StatusGood" : "StatusBad");
            });
        }

        private string FormatCode(string code) => code?.Length == 8 ? code.Substring(0, 4) + " " + code.Substring(4) : (code ?? "");
        public void Shutdown() => isRunning = false;
    }

    /// <summary>
    /// A friend row. Colours aren't held here — the view maps <see cref="Accent"/> and
    /// <see cref="Presence"/> onto theme brushes with DynamicResource, so a theme change repaints
    /// the list without the tab having to hear about it.
    /// </summary>
    public class FriendViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        /// <summary>Which team-ish colour this row wears.</summary>
        public enum AccentKind { None, Blue, Orange }

        /// <summary>What the friend is doing, in as much detail as we can join on.</summary>
        public enum PresenceKind { Offline, Menu, Lobby, Match }

        private string name = "", friendCode = "";
        private string modeText = "Offline", separatorText = "", sessionTypeText = "", statusBadgeText = "", sessionId = "", sessionIdText = "";
        private AccentKind accent = AccentKind.None;
        private PresenceKind presence = PresenceKind.Offline;
        private double opacity = 0.6;

        public string FriendCode  { get => friendCode;  set { friendCode  = value; Notify(); } }
        public string Name        { get => string.IsNullOrEmpty(name) ? FriendCode : name; set { name = value; Notify(); } }
        public string ModeText    { get => modeText;    set { modeText    = value; Notify(); } }
        public string SeparatorText   { get => separatorText;   set { separatorText   = value; Notify(); } }
        public string SessionTypeText { get => sessionTypeText; set { sessionTypeText = value; Notify(); } }
        public string StatusBadgeText { get => statusBadgeText; set { statusBadgeText = value; Notify(); Notify(nameof(ShowStatusBadge)); } }
        public string SessionId   { get => sessionId;   set { sessionId   = value; Notify(); Notify(nameof(IsJoinable)); } }
        public string SessionIdText { get => sessionIdText; set { sessionIdText = value; Notify(); } }
        public AccentKind Accent  { get => accent;      set { accent      = value; Notify(); } }
        public double Opacity     { get => opacity;     set { opacity     = value; Notify(); } }

        public PresenceKind Presence
        {
            get => presence;
            set { presence = value; Notify(); NotifyMultiple("IsOnline", "ShowJoinButton", "ShowStatusBadge", "IsJoinable"); }
        }

        public bool IsOnline => Presence != PresenceKind.Offline;

        public Visibility ShowStatusBadge => IsOnline && !string.IsNullOrEmpty(StatusBadgeText) ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// Lobbies are joinable now that their session id comes out of the friend's log, so the
        /// button shows for anything with a session behind it — not just matches.
        /// </summary>
        public Visibility ShowJoinButton => IsOnline ? Visibility.Visible : Visibility.Collapsed;

        public bool IsJoinable => IsOnline && !string.IsNullOrEmpty(SessionId);

        public void UpdateStatus(string lobbyId, string team, string mode, string sessionType)
        {
            SessionId = lobbyId ?? "";

            Presence = sessionType switch
            {
                FriendsPresence.SessionTypeMatch => PresenceKind.Match,
                FriendsPresence.SessionTypeLobby => PresenceKind.Lobby,
                FriendsPresence.SessionTypeMenu => PresenceKind.Menu,
                // Older Spark builds don't send a session type; having a session id means a match.
                _ => string.IsNullOrEmpty(lobbyId) ? PresenceKind.Menu : PresenceKind.Match,
            };

            Opacity = 1.0;

            string raw = (mode ?? "Unknown").Replace("_", " ");
            ModeText = raw.Length > 0 ? char.ToUpper(raw[0]) + raw.Substring(1).ToLower() : "Unknown";
            SeparatorText = "  •  ";
            SessionTypeText = Presence switch
            {
                PresenceKind.Match => "In Match",
                PresenceKind.Lobby => string.IsNullOrEmpty(SessionId) ? "In Lobby" : "In Lobby — joinable",
                _ => "In Menu",
            };

            StatusBadgeText = Presence == PresenceKind.Match ? (team ?? "lobby").ToUpper() : Presence == PresenceKind.Lobby ? "LOBBY" : "";
            Accent = StatusBadgeText switch
            {
                "BLUE" => AccentKind.Blue,
                "ORANGE" => AccentKind.Orange,
                _ => AccentKind.None,
            };

            SessionIdText = string.IsNullOrEmpty(SessionId)
                ? ""
                : "Session: " + (SessionId.Length > 12 ? SessionId.Substring(0, 12) + "..." : SessionId);
        }

        public void SetOffline()
        {
            Presence = PresenceKind.Offline;
            Opacity = 0.6;
            Accent = AccentKind.None;
            ModeText = "Offline"; SeparatorText = ""; SessionTypeText = ""; StatusBadgeText = ""; SessionId = ""; SessionIdText = "";
        }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        private void Notify([System.Runtime.CompilerServices.CallerMemberName] string p = null) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(p));
        private void NotifyMultiple(params string[] props) { foreach (var p in props) PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(p)); }
    }
}
