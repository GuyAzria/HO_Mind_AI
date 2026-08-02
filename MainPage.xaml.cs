// [ADDED v2.87.0 | 2026-05-12] Purpose: Added static volume override mechanisms (ApplyCustomVolume/RestoreCustomVolume) and Settings UI logic to force volume levels during TTS/beeps, even when device is muted.
// [ADDED v2.86.0 | 2026-05-07] Purpose: Added live chunk processing for continuous meeting transcription. Each phrase captured by STT is sent live to the UI as a distinct line with a dash to simulate turn-taking.
// [ADDED v2.85.0 | 2026-05-05] Purpose: Added static Action OnVoiceSessionStarted to stop active TTS when ghost screen opens or resets.
// [ADDED v2.84.0 | 2026-05-04] Purpose: Added static ResetKeyboardMargin() called from MainActivity ViewTreeObserver when keyboard closes natively — the only reliable method under LayoutNoLimits + Translucent window.
// [ADDED v2.83.0 | 2026-05-04] Purpose: Triple Unfocused margin reset (immediate + 200ms + 600ms) and SizeChanged watchdog to guarantee full-screen layout is always restored when Android keyboard closes.
// [ADDED v2.82.0 | 2026-05-04] Purpose: Added stop-word detection (Hebrew/English/Russian/Spanish/French) to cancel TTS immediately without sending to HA. Delayed margin reset so it fires AFTER the Android keyboard animation finishes (~350ms), restoring the full-screen layout correctly.
// [ADDED v2.81.0 | 2026-05-04] Purpose: Implemented an explicit dynamic bottom margin on the ChatLayer to aggressively fix the Android keyboard overlap issue, ensuring the input field is never hidden.
// [ADDED v2.80.0 | 2026-05-04] Purpose: Separated camera and chat environments by toggling background opacity. Solid backgrounds re-enable Android's native keyboard 'Resize' functionality, solving the overlapping issue natively without translation hacks.
// [ADDED v2.79.0 | 2026-05-04] Purpose: Reordered top bars so Camera is on top and selection bar is below it. Replaced failed native keyboard resize with manual TranslationY shift. Removed the 'Copied' alert for silent copying.
// [MODIFIED v2.78.0 | 2026-05-04] Purpose: Changed Android specific soft input adjust mode to 'Pan' to fix keyboard overlapping on transparent windows, and bound UI visibility logic to Shell.TitleView buttons.
// [MODIFIED v2.77.0 | 2026-05-04] Purpose: Resolved 'ambiguous reference' compiler error by explicitly fully qualifying all references to Microsoft.Maui.Controls.Application throughout the codebase.
// dotnet publish -f net10.0-android -c Release -p:AndroidPackageFormat=apk

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Storage;
using Microsoft.Maui.ApplicationModel;
using Plugin.BLE;
using Plugin.BLE.Abstractions.Contracts;
using Path = System.IO.Path;

namespace HO_Mind_AI
{
    public partial class MainPage : ContentPage
    {
        public static Action<string>? OnDeepLinkReceived;
        public static Action<string>? OnFloatingVoiceResult;

        // [ADDED v2.85.0 | 2026-05-05] Purpose: Stop TTS when floating window is triggered or restarted.
        public static Action? OnVoiceSessionStarted;

        // [ADDED v2.86.0 | 2026-05-07] Purpose: Actions for live streaming transcription
        public static Action? OnTranscriptionSessionStarted;
        public static Action<string>? OnTranscriptionChunkReceived;

        private string _currentContext = "chat";
        private bool _isProcessing = false;
        private bool _isFrontCamera = false;
        private bool _isFlashOn = false;
        private bool _useAiBg = true;
        private float _currentZoom = 0f;
        private string _pendingBase64Image = "";
        private bool _isFirstLaunch = true;
        public class ChatEntry { public string Sender { get; set; } = ""; public string Message { get; set; } = ""; public bool IsUser { get; set; } }
        private List<ChatEntry> _history = new();
        private readonly string _historyPath = System.IO.Path.Combine(FileSystem.AppDataDirectory, "chat_history.json");

        private HashSet<Border> _selectedMessages = new();
        private Dictionary<Border, string> _messageData = new();
        private Dictionary<Border, Microsoft.Maui.Graphics.Color> _originalColors = new();

       // private readonly HttpClient _chatClient;

        private BlePinManager? _bleManager;
        private string _haConversationId = "";
        private CancellationTokenSource? _ttsCts;

        // [ADDED v2.84.0 | 2026-05-04] Purpose: Static reference so MainActivity can call ResetKeyboardMargin() from ViewTreeObserver without needing a MAUI dependency.
        public static MainPage? Current { get; private set; }

        // [ADDED v2.84.0 | 2026-05-04] Purpose: Called by MainActivity.OnGlobalLayout() when it detects the keyboard has closed. Resets ChatLayer margin to restore full-screen layout.
        public static void ResetKeyboardMargin()
        {
            if (Current?.ChatLayer != null)
                Current.ChatLayer.Margin = new Thickness(0);
        }

        // [ADDED v2.87.0 | 2026-05-12] Purpose: Force custom volume and unmute logic for cross-app sound output (used during TTS and Beeps).
#if ANDROID
        private static int _originalVolume = -1;
        private static global::Android.Media.RingerMode _originalRingerMode = global::Android.Media.RingerMode.Normal;
        private static bool _isVolumeOverridden = false;

        public static void ApplyCustomVolume()
        {
            try
            {
                var context = global::Android.App.Application.Context;
                var audioManager = (global::Android.Media.AudioManager?)context.GetSystemService(global::Android.Content.Context.AudioService);
                if (audioManager == null) return;

                if (!_isVolumeOverridden)
                {
                    _originalVolume = audioManager.GetStreamVolume(global::Android.Media.Stream.Music);
                    _originalRingerMode = audioManager.RingerMode;
                    _isVolumeOverridden = true;
                }

                // Unmute device if necessary
                if (audioManager.RingerMode != global::Android.Media.RingerMode.Normal)
                {
                    audioManager.RingerMode = global::Android.Media.RingerMode.Normal;
                }

                int maxVolume = audioManager.GetStreamMaxVolume(global::Android.Media.Stream.Music);
                double userVolumePct = Preferences.Get("app_custom_volume", 100.0) / 100.0;
                int targetVolume = (int)Math.Round(maxVolume * userVolumePct);

                audioManager.SetStreamVolume(global::Android.Media.Stream.Music, targetVolume, 0);
            }
            catch { }
        }

        public static void RestoreCustomVolume()
        {
            try
            {
                if (!_isVolumeOverridden) return;

                var context = global::Android.App.Application.Context;
                var audioManager = (global::Android.Media.AudioManager?)context.GetSystemService(global::Android.Content.Context.AudioService);
                if (audioManager != null)
                {
                    audioManager.SetStreamVolume(global::Android.Media.Stream.Music, _originalVolume, 0);
                    try { audioManager.RingerMode = _originalRingerMode; } catch { }
                }
                _isVolumeOverridden = false;
            }
            catch { }
        }
#endif

        // [ADDED v2.82.0 | 2026-05-04] Purpose: Stop words in Hebrew, English, Russian, Spanish and French to cancel TTS without sending to HA.
        private static readonly HashSet<string> _stopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "עצור", "stop", "стоп", "detener", "arrêt", "arret"
        };

        private HttpMessageHandler GetVpnAwareHandler()
        {
#if ANDROID
            var handler = new Xamarin.Android.Net.AndroidMessageHandler();
            handler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true;
            return handler;
#else
            return new HttpClientHandler { ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true };
#endif
        }

        public MainPage()
        {
            Current = this;
          //  _chatClient = new HttpClient(GetVpnAwareHandler()) { Timeout = TimeSpan.FromSeconds(15) };

            // [ADDED v2.85.0 | 2026-05-05] Purpose: Register action to cancel TTS.
            OnVoiceSessionStarted = () =>
            {
                if (_ttsCts != null)
                {
                    _ttsCts.Cancel();
                    _ttsCts.Dispose();
                    _ttsCts = null;
                }
            };

            // [ADDED v2.86.0 | 2026-05-07] Purpose: UI Implementation for live transcription chunks
            OnTranscriptionSessionStarted = () =>
            {
                Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
                {
                    AddChatMessage("System", "--- 🎙️ Transcription Started ---", false);
                });
            };

            OnTranscriptionChunkReceived = (text) =>
            {
                Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
                {
                    // Add as a distinct line with a dash to simulate turn-taking
                    AddChatMessage("🗣️", $"- {text}", false);
                });
            };

            InitializeComponent();
            ApplyBranding();

            if (ManualInput != null)
            {
                // [MODIFIED v2.84.0 | 2026-05-04] Purpose: On Focused push layout up so keyboard never hides input. Reset is handled natively by MainActivity ViewTreeObserver.
                ManualInput.Focused += async (s, e) =>
                {
#if ANDROID
                    if (ChatLayer != null) ChatLayer.Margin = new Thickness(0, 0, 0, 350);
#endif
                    await Task.Delay(100);
                    await ScrollToBottomAsync();
                };

                // [MODIFIED v2.84.0 | 2026-05-04] Purpose: Unfocused still resets as a secondary safety net in case ViewTreeObserver fires before Unfocused.
                ManualInput.Unfocused += (s, e) =>
                {
#if ANDROID
                    if (ChatLayer != null) ChatLayer.Margin = new Thickness(0);
#endif
                };
            }

            LoadSettings();
            if (barcodeView != null) { barcodeView.BarcodeDetected += OnBarcodeDetected; barcodeView.PhotoCaptured += OnPhotoCaptured; }
            ApplyContext("chat", "Home Organizer Camera", "Ready to capture", "📸 Send to Chat");
            OnDeepLinkReceived = ProcessPayload;
            LoadChatHistory();
            SwitchToChat_Clicked(this, EventArgs.Empty);
            OnFloatingVoiceResult = (spokenText) => { Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() => ProcessInput(spokenText, isVoice: true)); };


        }

        // [ADDED v2.91.0 | 2026-08-02] Purpose: Applies the personal branding text to the application's main user interface
        private void ApplyBranding()
        {
            this.Title = "HO-Mind-AI by Guy Azria";
        }

        private async Task ScrollToBottomAsync()
        {
            if (ChatContainer != null && ChatContainer.Children.Count > 0 && ChatScroll != null)
            {
                var lastChild = ChatContainer.Children.LastOrDefault() as View;
                if (lastChild != null)
                {
                    await ChatScroll.ScrollToAsync(lastChild, ScrollToPosition.End, true);
                }
            }
        }

        private void ToggleSelection(Border border)
        {
            if (_selectedMessages.Contains(border))
            {
                _selectedMessages.Remove(border);
                border.BackgroundColor = _originalColors.ContainsKey(border) ? _originalColors[border] : Microsoft.Maui.Graphics.Colors.White;
            }
            else
            {
                _selectedMessages.Add(border);
                border.BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#ADD8E6");
            }

            if (SelectionBar != null) SelectionBar.IsVisible = _selectedMessages.Count > 0;
        }

        private void ClearAllSelections()
        {
            foreach (var b in _selectedMessages)
            {
                b.BackgroundColor = _originalColors.ContainsKey(b) ? _originalColors[b] : Microsoft.Maui.Graphics.Colors.White;
            }
            _selectedMessages.Clear();
            if (SelectionBar != null) SelectionBar.IsVisible = false;
        }

        private void OnClearSelectionClicked(object? sender, EventArgs e)
        {
            ClearAllSelections();
        }

        private async void OnCopySelectionClicked(object? sender, EventArgs e)
        {
            if (_selectedMessages.Count == 0) return;

            var sb = new System.Text.StringBuilder();
            foreach (var b in _selectedMessages)
            {
                if (_messageData.TryGetValue(b, out string m))
                {
                    sb.AppendLine(m);
                }
            }

            string finalCopy = sb.ToString().TrimEnd();
            await Clipboard.Default.SetTextAsync(finalCopy);

            ClearAllSelections();
        }

#if ANDROID
        private void StartFloatingService(string action = "")
        {
            var context = Android.App.Application.Context;
            if (!Android.Provider.Settings.CanDrawOverlays(context))
            {
                var intent = new Android.Content.Intent(Android.Provider.Settings.ActionManageOverlayPermission, Android.Net.Uri.Parse("package:" + context.PackageName));
                intent.AddFlags(Android.Content.ActivityFlags.NewTask);
                context.StartActivity(intent);
                Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() => DisplayAlert("Permission Required", "Please allow 'Appear on top' to use the floating assistant, then try again.", "OK"));
                return;
            }
            var serviceIntent = new Android.Content.Intent(context, typeof(HO_Mind_AI.Platforms.Android.FloatingVoiceService));
            if (!string.IsNullOrEmpty(action)) serviceIntent.SetAction(action);
            if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.O) context.StartForegroundService(serviceIntent);
            else context.StartService(serviceIntent);
        }
#endif

        private void UpdateBleManagerStatus()
        {
            bool useBtMic = Preferences.Get("use_bt_mic", false);
            if (useBtMic)
            {
                if (_bleManager == null)
                {
                    _bleManager = new BlePinManager();
                    _bleManager.OnPinPressed = () => {
#if ANDROID
                        Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() => StartFloatingService("START_LISTENING"));
#endif
                    };
                }
                _ = Task.Run(() => _bleManager.StartScanning());
            }
            else { if (_bleManager != null) { _bleManager.Stop(); _bleManager = null; } }
        }

        private void CloseOrBackgroundApp()
        {
#if ANDROID
            var activity = Platform.CurrentActivity;
            activity?.MoveTaskToBack(true);
#else
            Microsoft.Maui.Controls.Application.Current?.Quit();
#endif
        }

        private void SendLocalNotification(string title, string message)
        {
#if ANDROID
            try
            {
                var context = Android.App.Application.Context;
                if (context == null) return;
                var notificationManager = (Android.App.NotificationManager?)context.GetSystemService(Android.Content.Context.NotificationService);
                if (notificationManager == null) return;
                string channelId = "ho_ai_chat_channel";
                Android.App.Notification.Builder builder;
                if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.O)
                {
                    var channel = new Android.App.NotificationChannel(channelId, "HO AI Messages", Android.App.NotificationImportance.High);
                    notificationManager.CreateNotificationChannel(channel);
                    builder = new Android.App.Notification.Builder(context, channelId);
                }
                else
                {
#pragma warning disable CA1422
                    builder = new Android.App.Notification.Builder(context);
#pragma warning restore CA1422
                }
                var intent = new Android.Content.Intent(context, typeof(MainActivity));
                intent.AddFlags(Android.Content.ActivityFlags.ClearTop | Android.Content.ActivityFlags.SingleTop);
                var pendingIntentFlags = Android.App.PendingIntentFlags.UpdateCurrent | Android.App.PendingIntentFlags.Immutable;
                var pendingIntent = Android.App.PendingIntent.GetActivity(context, 0, intent, pendingIntentFlags);
                builder.SetContentTitle(title).SetContentText(message).SetStyle(new Android.App.Notification.BigTextStyle().BigText(message)).SetSmallIcon(global::Android.Resource.Drawable.IcDialogInfo).SetAutoCancel(true).SetContentIntent(pendingIntent);
                notificationManager.Notify(1001, builder.Build());
            }
            catch { }
#endif
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            try { await Permissions.RequestAsync<Permissions.Microphone>(); } catch { }
            try { await Permissions.RequestAsync<Permissions.Bluetooth>(); } catch { }
            UpdateBleManagerStatus();

            bool triggerListening = false;
            bool triggerBackground = _isFirstLaunch;
            _isFirstLaunch = false;

#if ANDROID
            try
            {
                var intent = Platform.CurrentActivity?.Intent;
                bool shouldAutoVoice = intent?.GetBooleanExtra("auto_voice", false) ?? false;
                if (shouldAutoVoice) triggerListening = true;
                if (intent?.Data != null)
                {
                    string contextVal = intent.Data.GetQueryParameter("context") ?? "chat";
                    string jsonPayload = JsonSerializer.Serialize(new { ctx = contextVal });
                    ProcessPayload(jsonPayload);
                }
            }
            catch { }
#endif

            if (triggerListening)
            {
                Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(async () => {
                    await Task.Delay(500);
#if ANDROID
                    StartFloatingService("START_LISTENING");
                    CloseOrBackgroundApp();
#endif
                });
            }
            else if (triggerBackground)
            {
                Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(async () => {
                    await Task.Delay(500);
#if ANDROID
                    StartFloatingService();
#endif
                });
            }
        }

        private string GetCurrentCultureCode()
        {
            string savedLang = Preferences.Get("voice_lang", "English (US)");
            return savedLang switch { "Hebrew (Israel)" => "he-IL", "French (France)" => "fr-FR", "Russian (Russia)" => "ru-RU", "Arabic (SA)" => "ar-SA", _ => "en-US" };
        }

        private async Task SpeakTextAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            try
            {
                if (_ttsCts != null) _ttsCts.Cancel();
                _ttsCts = new CancellationTokenSource();
                var token = _ttsCts.Token;
                var locales = await TextToSpeech.Default.GetLocalesAsync();
                string langCode = GetCurrentCultureCode().Split('-')[0];
                var locale = locales.FirstOrDefault(l => l.Language.Equals(langCode, StringComparison.OrdinalIgnoreCase) || (langCode == "he" && l.Language.Equals("iw", StringComparison.OrdinalIgnoreCase)));

                // [ADDED v2.87.0] Override volume before TTS
#if ANDROID
                ApplyCustomVolume();
#endif

                float volumeSetting = (float)(Preferences.Get("app_custom_volume", 100.0) / 100.0);
                await TextToSpeech.Default.SpeakAsync(text, new SpeechOptions { Locale = locale, Volume = volumeSetting }, cancelToken: token);
            }
            catch { }
            finally
            {
                // [ADDED v2.87.0] Restore volume after TTS
#if ANDROID
                RestoreCustomVolume();
#endif
            }
        }

        private void OnVoiceClicked(object? sender, EventArgs e)
        {
#if ANDROID
            StartFloatingService("START_LISTENING");
#endif
        }

       // [MODIFIED v2.88.0 | 2026-05-13] Purpose: Added check to ignore empty inputs and prevent false UI transitions.
        private async void ProcessInput(string text, bool isVoice)
        {
            string trimmed = text?.Trim() ?? "";

            // If the microphone captured nothing (e.g. device was sleeping and mic failed), DO NOT proceed to Chat.
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return;
            }

            if (_stopWords.Contains(trimmed))
            {
                if (_ttsCts != null) _ttsCts.Cancel();
                Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() => { if (ManualInput != null) ManualInput.Text = ""; });
                return;
            }

            if (_ttsCts != null) _ttsCts.Cancel();
            Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() => { if (ManualInput != null) ManualInput.Text = ""; });
            AddChatMessage(isVoice ? "You (Voice)" : "You", text, true);
            await SendToHAConversation(text);
        }

        private void OnSendTextClicked(object? sender, EventArgs e)
        {
            if (ManualInput == null || string.IsNullOrWhiteSpace(ManualInput.Text)) return;
            string txt = ManualInput.Text;
            ManualInput.Text = "";
            ProcessInput(txt, isVoice: false);
        }

        // [MODIFIED v2.90.0 | 2026-05-13] Purpose: Increased HttpClient timeout from 15s to 60s in SendToHAConversation. Operations like 'Send to WhatsApp' involve external API calls on the HA side and take significantly longer than local intents like 'Add to list', causing premature client-side socket closures (Timeouts).
        private async Task SendToHAConversation(string text)
        {
            DeviceDisplay.Current.KeepScreenOn = true;
            int maxRetries = 2; // We will try max 2 times

            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    string haUrl = Preferences.Get("ha_url", "").TrimEnd('/');
                    string token = Preferences.Get("ha_token", "");
                    string lang = GetCurrentCultureCode().Split('-')[0];
                    string agentId = Preferences.Get("ha_agent_id", "conversation.ho_ai_agent").Trim();
                    string notifyId = Preferences.Get("ha_notify_id", "").Trim();

                    var handler = GetVpnAwareHandler();

                    // שינוי קריטי: הגדלת ה-Timeout ל-60 שניות כדי לאפשר לשרת לסיים פעולות ארוכות כמו וואטסאפ
                    using var localClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
                    localClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                    var payload = new Dictionary<string, object> { { "text", text }, { "language", lang } };
                    if (!string.IsNullOrEmpty(agentId)) payload.Add("agent_id", agentId);
                    if (!string.IsNullOrEmpty(_haConversationId)) payload.Add("conversation_id", _haConversationId);
                    if (!string.IsNullOrEmpty(notifyId)) payload.Add("device_id", notifyId);

                    var response = await localClient.PostAsJsonAsync($"{haUrl}/api/conversation/process", payload);

                    if (response.IsSuccessStatusCode)
                    {
                        var data = await response.Content.ReadFromJsonAsync<JsonElement>();
                        if (data.TryGetProperty("error", out JsonElement errorElement)) { AddChatMessage("System", $"HA Agent Error: {errorElement.GetString()}", false); return; }
                        if (data.TryGetProperty("conversation_id", out JsonElement convIdElem)) _haConversationId = convIdElem.GetString() ?? "";

                        string reply = data.GetProperty("response").GetProperty("speech").GetProperty("plain").GetProperty("speech").GetString() ?? "";
                        reply = ProcessExternalLinks(reply, out string? urlToLaunch);

                        AddChatMessage("HO AI", reply, false);
                        SendLocalNotification("HO Assistant", reply);

                        if (urlToLaunch != null)
                        {
                            _ = SpeakTextAsync(reply);
                            await Microsoft.Maui.ApplicationModel.MainThread.InvokeOnMainThreadAsync(async () => { await Task.Delay(500); try { await Launcher.Default.OpenAsync(urlToLaunch); } catch { } });
                        }
                        else { await SpeakTextAsync(reply); }
                    }
                    else { AddChatMessage("System", $"HA Server Error: {response.StatusCode}", false); }

                    break; // Success, break the retry loop
                }
                catch (HttpRequestException ex) // Catches Socket closed / Timeout errors
                {
                    if (i == maxRetries - 1)
                    {
                        AddChatMessage("System", "HA API Error (Network/Timeout): " + ex.Message, false);
                    }
                    else
                    {
                        await Task.Delay(500); // Wait half a second and retry
                    }
                }
                catch (TaskCanceledException ex) // Catches explicit Timeout cancellations
                {
                    if (i == maxRetries - 1)
                    {
                        AddChatMessage("System", "HA API Error (Timeout): Request took too long to complete.", false);
                    }
                    else
                    {
                        await Task.Delay(500);
                    }
                }
                catch (Exception ex)
                {
                    AddChatMessage("System", "HA API Error: " + ex.Message, false);
                    break; // Non-network error, no need to retry
                }
                finally
                {
                    DeviceDisplay.Current.KeepScreenOn = false;
                }
            }
        }


        private string ProcessExternalLinks(string rawMessage, out string? urlToLaunch)
        {
            string formattedMessage = rawMessage;
            urlToLaunch = null;
            string platform = "";
            var waMatch = Regex.Match(rawMessage, @"https?://(wa\.me|api\.whatsapp\.com|web\.whatsapp\.com)/[^\s\n\r\t]+");
            if (waMatch.Success) { urlToLaunch = waMatch.Value; platform = "WhatsApp"; }
            else
            {
                var tgMatch = Regex.Match(rawMessage, @"https?://(t\.me|telegram\.me)/[^\s\n\r\t]+");
                if (tgMatch.Success) { urlToLaunch = tgMatch.Value; platform = "Telegram"; }
                else
                {
                    var emailMatch = Regex.Match(rawMessage, @"mailto:[^\s\n\r\t]+");
                    if (emailMatch.Success) { urlToLaunch = emailMatch.Value; platform = "Email"; }
                }
            }
            if (urlToLaunch != null) formattedMessage = $"✅ Sent to {platform}";
            return formattedMessage;
        }

        private string FormatMessageForUI(string rawText)
        {
            if (string.IsNullOrEmpty(rawText)) return "";
            string html = rawText.Replace("\r\n", "<br>").Replace("\n", "<br>");
            html = Regex.Replace(html, @"\*\*(.*?)\*\*", "<b>$1</b>");
            html = Regex.Replace(html, @"\*(.*?)\*", "<i>$1</i>");
            return html;
        }

        private void LoadChatHistory()
        {
            if (!File.Exists(_historyPath)) return;
            try
            {
                var json = File.ReadAllText(_historyPath);
                _history = JsonSerializer.Deserialize<List<ChatEntry>>(json) ?? new();
                foreach (var e in _history) RenderMessage(e.Sender, e.Message, e.IsUser, false);

                Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(async () =>
                {
                    await Task.Delay(200);
                    await ScrollToBottomAsync();
                });
            }
            catch { }
        }

        private void AddChatMessage(string sender, string msg, bool isUser)
        {
            _history.Add(new ChatEntry { Sender = sender, Message = msg, IsUser = isUser });
            if (_history.Count > 50) _history.RemoveAt(0);
            File.WriteAllText(_historyPath, JsonSerializer.Serialize(_history));
            RenderMessage(sender, msg, isUser, true);
        }

        private void RenderMessage(string sender, string msg, bool isUser, bool scroll)
        {
            Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
            {
                // [MODIFIED v2.86.0 | 2026-05-07] Purpose: Stylize "🗣️" messages distinctively (Light gray to indicate external transcript)
                var border = new Border
                {
                    BackgroundColor = isUser ? Microsoft.Maui.Graphics.Color.FromArgb("#DCF8C6") : (sender == "🗣️" ? Microsoft.Maui.Graphics.Color.FromArgb("#F0F0F0") : Microsoft.Maui.Graphics.Colors.White),
                    HorizontalOptions = isUser ? LayoutOptions.End : LayoutOptions.Start,
                    Margin = new Thickness(isUser ? 50 : 0, 5, isUser ? 0 : 50, 5),
                    Padding = new Thickness(10),
                    StrokeThickness = 0,
                    StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(10) },
                    Content = new Label { Text = isUser ? $"{sender}: {msg}" : $"<b>{sender}:</b><br>{FormatMessageForUI(msg)}", TextType = TextType.Html, TextColor = Microsoft.Maui.Graphics.Colors.Black }
                };

                _originalColors[border] = border.BackgroundColor;
                _messageData[border] = msg;

                var singleTap = new TapGestureRecognizer { NumberOfTapsRequired = 1 };
                singleTap.Tapped += (s, e) =>
                {
                    ToggleSelection(border);
                };
                border.GestureRecognizers.Add(singleTap);

                if (ChatContainer != null) ChatContainer.Children.Add(border);

                if (scroll && ChatScroll != null && ChatContainer != null)
                {
                    Task.Delay(100).ContinueWith(t =>
                    {
                        Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
                        {
                            ChatScroll.ScrollToAsync(border, ScrollToPosition.End, true);
                        });
                    });
                }
            });
        }

        private void SwitchToChat_Clicked(object? sender, EventArgs e)
        {
            if (NormalUiContainer != null) NormalUiContainer.IsVisible = true;
            if (ChatLayer != null) ChatLayer.IsVisible = true;
            if (CameraControlsLayer != null) CameraControlsLayer.IsVisible = false;
            if (barcodeView != null) barcodeView.IsVisible = false;

            this.BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#ECE5DD");
            if (NormalUiContainer != null) NormalUiContainer.BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#ECE5DD");
        }

        private void SwitchToCamera_Clicked(object? sender, EventArgs e)
        {
            if (NormalUiContainer != null) NormalUiContainer.IsVisible = true;
            if (ChatLayer != null) ChatLayer.IsVisible = false;
            if (CameraControlsLayer != null) CameraControlsLayer.IsVisible = true;
            if (barcodeView != null) barcodeView.IsVisible = true;

            this.BackgroundColor = Microsoft.Maui.Graphics.Colors.Transparent;
            if (NormalUiContainer != null) NormalUiContainer.BackgroundColor = Microsoft.Maui.Graphics.Colors.Black;

            ApplyContext(_currentContext, "Camera Mode", "Ready", "");
        }

        private void ProcessPayload(string jsonPayload)
        {
            Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() => {
                try
                {
                    if (DebugLabel != null) DebugLabel.Text = $"INTENT: {jsonPayload}";
                    var data = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonPayload);
                    if (data != null)
                    {
                        string ctx = data.ContainsKey("ctx") ? data["ctx"] : "chat";
                        if (ctx == "chat") SwitchToChat_Clicked(this, EventArgs.Empty); else SwitchToCamera_Clicked(this, EventArgs.Empty);
                        ApplyContext(ctx, data.GetValueOrDefault("m", ""), data.GetValueOrDefault("s", ""), data.GetValueOrDefault("b", ""));
                    }
                }
                catch (Exception ex) { if (DebugLabel != null) DebugLabel.Text = $"ERR: {ex.Message}"; SwitchToCamera_Clicked(this, EventArgs.Empty); ApplyContext("chat", "", "", "📸 Capture"); }
            });
        }

        private void ToggleDebug_Clicked(object? sender, EventArgs e) { if (DebugLabel != null) DebugLabel.IsVisible = !DebugLabel.IsVisible; }
        private void TestItem_Clicked(object? sender, EventArgs e) => ApplyContext("update", "Capture Item", "Take a clear picture", "📸 Capture");
        private void TestBarcode_Clicked(object? sender, EventArgs e) => ApplyContext("barcode", "Barcode Scanner", "Scan barcodes", "");
        private void TestReceipt_Clicked(object? sender, EventArgs e) => ApplyContext("invoice", "Receipt Scanner", "Ensure text is readable", "📸 Scan Receipt");
        private void TestStylist_Clicked(object? sender, EventArgs e) => ApplyContext("stylist", "Stylist Mode", "Coming Soon", "📸 Stylist");

        private void CycleCamera_Clicked(object? sender, EventArgs e) { _currentZoom = _currentZoom >= 0.8f ? 0f : _currentZoom + 0.4f; if (barcodeView != null) barcodeView.SetZoom(_currentZoom); }
        private void SwitchCam_Clicked(object? sender, EventArgs e) { _isFrontCamera = !_isFrontCamera; _currentZoom = 0f; if (barcodeView != null) barcodeView.IsFrontCamera = _isFrontCamera; }
        private void FlashButton_Clicked(object? sender, EventArgs e) { _isFlashOn = !_isFlashOn; if (barcodeView != null) barcodeView.SetFlash(_isFlashOn); if (FlashButton != null) FlashButton.BackgroundColor = _isFlashOn ? Microsoft.Maui.Graphics.Color.FromArgb("#03a9f4") : Microsoft.Maui.Graphics.Color.FromArgb("#88000000"); }
        private void AiBgButton_Clicked(object? sender, EventArgs e) { _useAiBg = !_useAiBg; if (AiBgButton != null) AiBgButton.BackgroundColor = _useAiBg ? Microsoft.Maui.Graphics.Color.FromArgb("#03a9f4") : Microsoft.Maui.Graphics.Color.FromArgb("#88000000"); if (barcodeView != null) barcodeView.SetUseAiBg(_useAiBg); }

        private void ApplyContext(string context, string m, string s, string b)
        {
            _currentContext = context.Trim().ToLowerInvariant();
            if (ModeTitleLabel != null) ModeTitleLabel.Text = _currentContext == "barcode" ? "📷 Mode: Barcode Scan" : (_currentContext == "invoice" ? "🧾 Mode: Receipt Scan" : "📸 Mode: Item Image");
            if (ScannedBarcodeLabel != null) ScannedBarcodeLabel.Text = "";
            if (InstructionFrame != null) InstructionFrame.IsVisible = !string.IsNullOrWhiteSpace(m + s);
            if (InstructionLabel != null) InstructionLabel.Text = $"{m}\n{s}".Trim();
            if (barcodeView != null) { barcodeView.CameraMode = _currentContext == "barcode" ? CameraMode.Barcode : CameraMode.Photo; barcodeView.SetUseAiBg(_useAiBg); barcodeView.CameraContext = _currentContext; barcodeView.SetZoom(_currentZoom); }
            if (_currentContext == "barcode") { if (BarcodeOverlay != null) BarcodeOverlay.IsVisible = true; if (AiBgButton != null) AiBgButton.IsVisible = false; if (CaptureButton != null) CaptureButton.IsVisible = false; _currentZoom = 0.4f; }
            else { if (BarcodeOverlay != null) BarcodeOverlay.IsVisible = false; if (CaptureButton != null) { CaptureButton.IsVisible = true; CaptureButton.Text = string.IsNullOrWhiteSpace(b) ? "📸 Capture" : b; } _currentZoom = 0f; }
        }

        private void OnBarcodeDetected(string value)
        {
            if (_isProcessing || _currentContext != "barcode") return;
            _isProcessing = true;
            if (DebugLabel != null) DebugLabel.Text = $"Detected: {value}";
            if (ScannedBarcodeLabel != null) ScannedBarcodeLabel.Text = value;
            if (LoadingOverlay != null) LoadingOverlay.IsVisible = true;
            try { HapticFeedback.Perform(HapticFeedbackType.LongPress); } catch { }
            _ = SendDataToHA(value, isBarcode: true);
        }

        private void CaptureButton_Clicked(object? sender, EventArgs e)
        {
            if (_isProcessing) return;
            _isProcessing = true;
            if (LoadingLabel != null) LoadingLabel.Text = _useAiBg ? "Removing Background..." : "Processing...";
            if (LoadingOverlay != null) LoadingOverlay.IsVisible = true;
            if (barcodeView != null) barcodeView.TakePhoto();
        }

        private void OnPhotoCaptured(string base64DataUrl)
        {
            if (string.IsNullOrEmpty(base64DataUrl)) { _isProcessing = false; Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() => { if (LoadingOverlay != null) LoadingOverlay.IsVisible = false; }); return; }
            _pendingBase64Image = base64DataUrl;
            Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() => {
                if (LoadingOverlay != null) LoadingOverlay.IsVisible = false;
                try
                {
                    string cleanBase64 = base64DataUrl.Replace("data:image/jpeg;base64,", "").Replace("data:image/png;base64,", "");
                    byte[] imageBytes = Convert.FromBase64String(cleanBase64);
                    if (PreviewImage != null) PreviewImage.Source = ImageSource.FromStream(() => new MemoryStream(imageBytes));
                    if (PreviewOverlay != null) PreviewOverlay.IsVisible = true;
                }
                catch { _isProcessing = false; }
            });
        }

        private void Retake_Clicked(object? sender, EventArgs e) { _pendingBase64Image = ""; if (PreviewImage != null) PreviewImage.Source = null; if (PreviewOverlay != null) PreviewOverlay.IsVisible = false; _isProcessing = false; }
        private void Send_Clicked(object? sender, EventArgs e) { if (PreviewOverlay != null) PreviewOverlay.IsVisible = false; if (LoadingOverlay != null) LoadingOverlay.IsVisible = true; _ = SendDataToHA(_pendingBase64Image, isBarcode: false); }

        private async Task SendDataToHA(string payloadData, bool isBarcode)
        {
            try
            {
                string haUrl = Preferences.Get("ha_url", "") ?? ""; string token = Preferences.Get("ha_token", "") ?? ""; if (string.IsNullOrEmpty(haUrl)) return; haUrl = haUrl.Trim().TrimEnd('/');

                var handler = GetVpnAwareHandler();

                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

                object payload = isBarcode ? new { barcode_data = payloadData, context = _currentContext } : new { image_data = payloadData, context = _currentContext, apply_ai_bg = _useAiBg };
                var response = await client.PostAsJsonAsync($"{haUrl}/api/home_organizer/ext_camera_upload", payload);
                if (response.IsSuccessStatusCode) Microsoft.Maui.Controls.Application.Current?.Quit();
            }
            catch { }
            finally { _isProcessing = false; if (LoadingOverlay != null) LoadingOverlay.IsVisible = false; }
        }

        private Task ShowAlertAsync(string title, string message, string cancel) => Microsoft.Maui.ApplicationModel.MainThread.InvokeOnMainThreadAsync(() => this.DisplayAlertAsync(title, message, cancel));
        private async void TestSTT_Clicked(object sender, EventArgs e) { string toSpeak = "Testing STT removed from this mode."; if (TestInput != null) TestInput.Text = toSpeak; }
        private async void TestTTS_Clicked(object sender, EventArgs e) { string toSpeak = "Testing voice audio output."; if (TestInput != null && !string.IsNullOrWhiteSpace(TestInput.Text) && TestInput.Text != "Listening...") toSpeak = TestInput.Text; await SpeakTextAsync(toSpeak); }
        private void OpenSettings_Clicked(object? sender, EventArgs e) { if (SettingsModal != null) SettingsModal.IsVisible = true; }
        private void Close_Clicked(object? sender, EventArgs e) => Microsoft.Maui.Controls.Application.Current?.Quit();

        private void LoadSettings()
        {
            if (ServerUrlEntry != null) ServerUrlEntry.Text = Preferences.Get("ha_url", "");
            if (TokenEntry != null) TokenEntry.Text = Preferences.Get("ha_token", "");
            if (AgentIdEntry != null) AgentIdEntry.Text = Preferences.Get("ha_agent_id", "conversation.ho_ai_agent");
            if (NotifyIdEntry != null) NotifyIdEntry.Text = Preferences.Get("ha_notify_id", "");
            if (UseBtMicSwitch != null) UseBtMicSwitch.IsToggled = Preferences.Get("use_bt_mic", false);
            if (UseShakeMicSwitch != null) UseShakeMicSwitch.IsToggled = Preferences.Get("use_shake_mic", false);
            if (EnableLoggingSwitch != null) EnableLoggingSwitch.IsToggled = Preferences.Get("enable_file_logging", false);
            if (LanguagePicker != null) { string savedLang = Preferences.Get("voice_lang", "English (US)"); int index = LanguagePicker.Items.IndexOf(savedLang); LanguagePicker.SelectedIndex = index >= 0 ? index : 0; }
            if (ActiveAgentLabel != null) ActiveAgentLabel.Text = $"Agent: {Preferences.Get("ha_agent_id", "conversation.ho_ai_agent")}";

            // [ADDED v2.87.0] Load volume slider
            if (VolumeSlider != null)
            {
                VolumeSlider.Value = Preferences.Get("app_custom_volume", 100.0);
                if (VolumeLabel != null) VolumeLabel.Text = $"{(int)VolumeSlider.Value}%";
            }
        }

        // [ADDED v2.87.0] Event handler for the volume slider label
        private void VolumeSlider_ValueChanged(object sender, ValueChangedEventArgs e)
        {
            if (VolumeLabel != null) VolumeLabel.Text = $"{(int)e.NewValue}%";
        }

        private void SaveSettings_Clicked(object? sender, EventArgs e)
        {
            Preferences.Set("ha_url", ServerUrlEntry?.Text?.Trim() ?? "");
            Preferences.Set("ha_token", TokenEntry?.Text?.Trim() ?? "");
            Preferences.Set("ha_agent_id", AgentIdEntry?.Text?.Trim() ?? "conversation.ho_ai_agent");
            Preferences.Set("ha_notify_id", NotifyIdEntry?.Text?.Trim() ?? "");
            if (UseBtMicSwitch != null) Preferences.Set("use_bt_mic", UseBtMicSwitch.IsToggled);
            if (UseShakeMicSwitch != null) Preferences.Set("use_shake_mic", UseShakeMicSwitch.IsToggled);
            if (EnableLoggingSwitch != null) Preferences.Set("enable_file_logging", EnableLoggingSwitch.IsToggled);
            if (LanguagePicker?.SelectedIndex >= 0) Preferences.Set("voice_lang", LanguagePicker.Items[LanguagePicker.SelectedIndex]);
            if (ActiveAgentLabel != null) ActiveAgentLabel.Text = $"Agent: {AgentIdEntry?.Text?.Trim() ?? "conversation.ho_ai_agent"}";

            // [ADDED v2.87.0] Save volume
            if (VolumeSlider != null) Preferences.Set("app_custom_volume", VolumeSlider.Value);

            if (SettingsModal != null) SettingsModal.IsVisible = false;
            UpdateBleManagerStatus();
        }

        private async void ViewLogs_Clicked(object sender, EventArgs e)
        {
            string logPath = Path.Combine(FileSystem.CacheDirectory, "FVS_Logs.txt");
            if (File.Exists(logPath))
            {
                try
                {
                    string logs = await File.ReadAllTextAsync(logPath);
                    if (string.IsNullOrWhiteSpace(logs)) logs = "Log file is empty.";

                    if (logs.Length > 2000) logs = "... " + logs.Substring(logs.Length - 2000);

                    bool copyToClipboard = await DisplayAlert("FVS Logs", logs, "Copy to Clipboard", "Close");

                    if (copyToClipboard)
                    {
                        await Clipboard.Default.SetTextAsync(logs);
                        await DisplayAlert("Copied", "Logs successfully copied to clipboard.", "OK");
                    }
                }
                catch (Exception ex) { await DisplayAlert("Error", ex.Message, "OK"); }
            }
            else
            {
                await DisplayAlert("FVS Logs", "No logs found yet.", "Close");
            }
        }

        private void ClearLogs_Clicked(object sender, EventArgs e)
        {
            string logPath = Path.Combine(FileSystem.CacheDirectory, "FVS_Logs.txt");
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
                DisplayAlert("Logs", "Logs cleared successfully.", "OK");
            }
            else
            {
                DisplayAlert("Logs", "No log file to clear.", "OK");
            }
        }
    }

    public class BlePinManager
    {
        private readonly IAdapter _adapter;
        private IDevice? _xiaoDevice;
        public Action? OnPinPressed;
        private const string TargetDeviceName = "HO_PIN";

        public BlePinManager()
        {
            _adapter = CrossBluetoothLE.Current.Adapter;
            _adapter.DeviceDiscovered += async (s, e) => {
                if (e.Device.Name == TargetDeviceName)
                {
                    await _adapter.StopScanningForDevicesAsync();
                    _xiaoDevice = e.Device;
                    try
                    {
                        await _adapter.ConnectToDeviceAsync(_xiaoDevice);
                        var services = await _xiaoDevice.GetServicesAsync();
                        var chars = await services[0].GetCharacteristicsAsync();
                        chars[0].ValueUpdated += (send, args) => OnPinPressed?.Invoke();
                        await chars[0].StartUpdatesAsync();
                    }
                    catch { }
                }
            };
        }
        public async Task StartScanning() { if (!_adapter.IsScanning) await _adapter.StartScanningForDevicesAsync(); }
        public void Stop() { try { if (_adapter.IsScanning) _adapter.StopScanningForDevicesAsync(); if (_xiaoDevice != null) _adapter.DisconnectDeviceAsync(_xiaoDevice); } catch { } }
    }
}