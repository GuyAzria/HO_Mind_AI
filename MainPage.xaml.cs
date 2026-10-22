// [MODIFIED v2026.9.24 | 2026-09-24] Purpose: Token moved from plaintext
//   Preferences to the device secure store; blanket acceptance of any TLS
//   certificate removed; a failed upload is now reported instead of
//   swallowed; untrusted text is escaped before it reaches the HTML label;
//   the duplicated Home Assistant client construction is replaced by
//   HaCredentials, and the duplicated voice-language mapping by
//   VoiceLanguage. Cleartext HTTP over the VPN is unchanged and still works.
//   Also adds the PDF receipt button and its handler, and the redesigned
//   header: chat stays the screen the app opens on, settings moved behind a
//   three-dot button, and receipt and barcode became the two actions. A scan
//   started in the app goes to the new app_scan endpoint, which scans inside
//   Home Assistant, so it no longer needs a browser with the panel open.
//   [2026-09-26] A barcode now completes: the lookup only suggests a name, so
//   the confirmed name is sent back through RESOLVE_BARCODE, which is the step
//   that actually creates the pending item.
//   [2026-09-26] Settings hold a household rather than one phone: a name and a
//   device id per person. A command naming one of them is aimed at that phone,
//   and the chat says so, because the match is a name in a sentence and cannot
//   tell "remind Yulia" from "remind me to call Yulia". Names take several
//   forms separated by commas, and are compared on composed Unicode, so the
//   same person matches whichever language the sentence was spoken in.
//   Sending a typed message now closes the keyboard and gives the chat the
//   whole screen back, which it did not.
// [MODIFIED v2026.10.21 | 2026-10-21] Purpose: a phone's device id is picked
//   from the phones Home Assistant knows instead of being copied out of a
//   desktop browser's address bar. Each phone field is a dropdown: tapped
//   after the address and token are entered, it lists the devices with their
//   owner, and the one chosen is shown by name while its id is kept hidden;
//   its last option still takes a typed id. Load household adds everyone's in
//   one tap - adding rows only, never changing or removing one already there.
//   See PhoneDirectory.cs and the integration's GET /api/home_organizer/phones.
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

        // [ADDED v2026.9.24] Which of the two routes this scan belongs to.
        //
        // false: the panel launched this app through its deep link, so the
        //        result must go back the way it always has - ext_camera_upload
        //        fires an event and the open panel finishes the job.
        // true:  the user started the scan inside this app, where there may be
        //        no panel open at all, so it goes to app_scan and Home
        //        Assistant does the work itself.
        //
        // ProcessPayload clears it, so a deep link always wins over whatever
        // the user pressed here before.
        private bool _startedInApp = false;
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

        // [MODIFIED v2026.9.24] GetVpnAwareHandler() was here. It set
        // ServerCertificateCustomValidationCallback to a lambda returning
        // true for EVERY certificate, on a client that carries the Home
        // Assistant token. It now lives in HaCredentials.CreateHandler()
        // without that callback. Plain HTTP is unaffected: no certificate is
        // presented over http://, so nothing validates and nothing changes.

        public MainPage()
        {
            Current = this;

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

        // [MODIFIED v2026.9.24] Delegates to VoiceLanguage, which is now the
        // one owner of the mapping. The identical switch expression also lived
        // in FloatingVoiceService, so a new language had to be added twice.
        // Kept as a method so both of its callers are unchanged.
        private string GetCurrentCultureCode() => VoiceLanguage.CultureCode();

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

        // [FIXED v2026.9.26] Sending used to leave the keyboard up.
        //
        // The text was cleared but the entry kept the focus, so Android had no
        // reason to close the keyboard - and because the keyboard never closed,
        // Unfocused never fired and the 350px lift on ChatLayer stayed. The chat
        // sat squeezed into the top half of the screen with a keyboard under it
        // and nothing left to type.
        //
        // Three things, because this file records a long fight with this keyboard
        // (see the v2.83 and v2.84 entries): drop the focus, ask Android directly,
        // and put the margin back without waiting to be told.
        private void OnSendTextClicked(object? sender, EventArgs e)
        {
            if (ManualInput == null || string.IsNullOrWhiteSpace(ManualInput.Text)) return;
            string txt = ManualInput.Text;
            ManualInput.Text = "";

            DismissKeyboard();
            ProcessInput(txt, isVoice: false);
        }

        // Also used by the voice button, so a command spoken while the keyboard
        // happened to be open does not leave it there either.
        private void DismissKeyboard()
        {
            try { ManualInput?.Unfocus(); } catch (Exception) { }

#if ANDROID
            HideSoftKeyboardNative();
#endif

            // Reset now rather than waiting for Unfocused or for MainActivity to
            // notice the keyboard has gone. Both still fire and both set it to the
            // same value, so an extra reset cannot put the layout wrong.
            if (ChatLayer != null) ChatLayer.Margin = new Thickness(0);

            // The keyboard animates out over roughly a third of a second; scrolling
            // before it has gone lands short of the newest message.
            Task.Delay(400).ContinueWith(_ =>
            {
                Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (ChatLayer != null) ChatLayer.Margin = new Thickness(0);
                    _ = ScrollToBottomAsync();
                });
            });
        }

#if ANDROID
        // MAUI's Unfocus() is usually enough and on this screen it has not been:
        // the window is translucent and runs under LayoutNoLimits, which is the
        // configuration every keyboard workaround in this file exists for. Asking
        // the input manager directly is the part that actually closes it.
        private static void HideSoftKeyboardNative()
        {
            try
            {
                var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
                if (activity == null) return;
                var imm = (global::Android.Views.InputMethods.InputMethodManager?)
                    activity.GetSystemService(global::Android.Content.Context.InputMethodService);
                if (imm == null) return;
                var focused = activity.CurrentFocus;
                var token = focused?.WindowToken ?? activity.Window?.DecorView?.WindowToken;
                if (token != null)
                {
                    imm.HideSoftInputFromWindow(token, global::Android.Views.InputMethods.HideSoftInputFlags.None);
                }
                focused?.ClearFocus();
            }
            catch (Exception)
            {
            }
        }
#endif

        // [MODIFIED v2.90.0 | 2026-05-13] Purpose: Increased HttpClient timeout from 15s to 60s in SendToHAConversation. Operations like 'Send to WhatsApp' involve external API calls on the HA side and take significantly longer than local intents like 'Add to list', causing premature client-side socket closures (Timeouts).
        private async Task SendToHAConversation(string text)
        {
            DeviceDisplay.Current.KeepScreenOn = true;

            // [ADDED v2026.9.26] Who this one is for. Resolved once, before the
            // retry loop, so a retry cannot announce the same thing twice.
            //
            // Shown in the chat whenever it is not the main phone. The match is a
            // name appearing in the sentence and nothing cleverer, so "remind me to
            // call Yulia" will be aimed at her phone - wrong, and impossible to tell
            // apart without reading the sentence, which is Home Assistant's job. The
            // app therefore never does this silently.
            var addressee = UserRoster.Addressee(text);
            if (addressee != null)
            {
                AddChatMessage("System", $"\u2192 {addressee.Name}", false);
            }

            int maxRetries = 2; // We will try max 2 times

            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    string haUrl = HaCredentials.GetUrl();
                    string lang = GetCurrentCultureCode().Split('-')[0];
                    string agentId = Preferences.Get("ha_agent_id", "conversation.ho_ai_agent").Trim();
                    string notifyId = addressee?.DeviceId ?? UserRoster.PrimaryDeviceId();

                    using var localClient = await HaCredentials.CreateClientAsync(TimeSpan.FromSeconds(60));

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
            // [MODIFIED v2026.9.24] Nothing has been sent at this point. The
            // launcher only OPENS the other app with the message prefilled;
            // the user still has to press send there. "Sent" was a claim
            // this app could not back up.
            if (urlToLaunch != null) formattedMessage = $"↗️ Opening {platform}...";
            return formattedMessage;
        }

        // [MODIFIED v2026.9.24] Escapes before it formats. The reply comes from
        // a language model, and once orders are imported a product name comes
        // from a shop page. Both are untrusted text going into a Label whose
        // TextType is Html, so "<" has to stay text. The order matters:
        // escape first, then add markup, or the <b> tags below become visible.
        private static string EscapeHtml(string raw) => raw
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");

        private string FormatMessageForUI(string rawText)
        {
            if (string.IsNullOrEmpty(rawText)) return "";
            string html = EscapeHtml(rawText);
            html = html.Replace("\r\n", "<br>").Replace("\n", "<br>");
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
                    Content = new Label { Text = isUser ? $"{EscapeHtml(sender)}: {EscapeHtml(msg)}" : $"<b>{EscapeHtml(sender)}:</b><br>{FormatMessageForUI(msg)}", TextType = TextType.Html, TextColor = Microsoft.Maui.Graphics.Colors.Black }
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
                        // [ADDED v2026.9.24] The panel launched us, so the result
                        // goes back the way it always has.
                        _startedInApp = false;
                        string ctx = data.ContainsKey("ctx") ? data["ctx"] : "chat";
                        if (ctx == "chat") SwitchToChat_Clicked(this, EventArgs.Empty); else SwitchToCamera_Clicked(this, EventArgs.Empty);
                        ApplyContext(ctx, data.GetValueOrDefault("m", ""), data.GetValueOrDefault("s", ""), data.GetValueOrDefault("b", ""));
                    }
                }
                catch (Exception ex) { if (DebugLabel != null) DebugLabel.Text = $"ERR: {ex.Message}"; SwitchToCamera_Clicked(this, EventArgs.Empty); ApplyContext("chat", "", "", "📸 Capture"); }
            });
        }

        private void ToggleDebug_Clicked(object? sender, EventArgs e) { if (DebugLabel != null) DebugLabel.IsVisible = !DebugLabel.IsVisible; }
        // [ADDED v2026.9.24] The two things this app does, reachable from the
        // screen it opens on. They replace the Item / Barcode / Receipt / Stylist
        // row, whose handlers were called Test*_Clicked because that is what they
        // were: debug shortcuts that became the only way to choose a mode.
        //
        // ApplyContext takes the same strings the panel sends over its deep link,
        // so both routes land the app in exactly the same state.
        // [ADDED v2026.9.26] Leaving the chat closes the keyboard with it, so the
        // camera never opens behind a keyboard and a lifted layout.
        private void TabChat_Clicked(object? sender, EventArgs e)
        {
            DismissKeyboard();
            SwitchToChat_Clicked(sender, e);
            UpdateTabState("chat");
        }

        private void TabReceipt_Clicked(object? sender, EventArgs e)
        {
            _startedInApp = true;
            ApplyContext("invoice", "Receipt Scanner", "Ensure text is readable", "📸 Scan Receipt");
            SwitchToCamera_Clicked(sender, e);
            UpdateTabState("invoice");
        }

        private void TabBarcode_Clicked(object? sender, EventArgs e)
        {
            _startedInApp = true;
            ApplyContext("barcode", "Barcode Scanner", "Scan barcodes", "");
            SwitchToCamera_Clicked(sender, e);
            UpdateTabState("barcode");
        }

        // Which action is current, shown by filling one pill and leaving the
        // other two outlined. There is no hover on a phone, so the state has to
        // be visible without touching anything.
        private void UpdateTabState(string context)
        {
            var active = Microsoft.Maui.Graphics.Color.FromArgb("#25D366");
            var idle = Microsoft.Maui.Graphics.Color.FromArgb("#128C7E");
            if (TabChatButton != null) TabChatButton.BackgroundColor = context == "chat" ? active : idle;
            if (TabReceiptButton != null) TabReceiptButton.BackgroundColor = context == "invoice" ? active : idle;
            if (TabBarcodeButton != null) TabBarcodeButton.BackgroundColor = context == "barcode" ? active : idle;
        }

        private void CycleCamera_Clicked(object? sender, EventArgs e) { _currentZoom = _currentZoom >= 0.8f ? 0f : _currentZoom + 0.4f; if (barcodeView != null) barcodeView.SetZoom(_currentZoom); }
        private void SwitchCam_Clicked(object? sender, EventArgs e) { _isFrontCamera = !_isFrontCamera; _currentZoom = 0f; if (barcodeView != null) barcodeView.IsFrontCamera = _isFrontCamera; }
        private void FlashButton_Clicked(object? sender, EventArgs e) { _isFlashOn = !_isFlashOn; if (barcodeView != null) barcodeView.SetFlash(_isFlashOn); if (FlashButton != null) FlashButton.BackgroundColor = _isFlashOn ? Microsoft.Maui.Graphics.Color.FromArgb("#03a9f4") : Microsoft.Maui.Graphics.Color.FromArgb("#88000000"); }
        private void AiBgButton_Clicked(object? sender, EventArgs e) { _useAiBg = !_useAiBg; if (AiBgButton != null) AiBgButton.BackgroundColor = _useAiBg ? Microsoft.Maui.Graphics.Color.FromArgb("#03a9f4") : Microsoft.Maui.Graphics.Color.FromArgb("#88000000"); if (barcodeView != null) barcodeView.SetUseAiBg(_useAiBg); }

        private void ApplyContext(string context, string m, string s, string b)
        {
            _currentContext = context.Trim().ToLowerInvariant();
            if (ModeTitleLabel != null) ModeTitleLabel.Text = _currentContext == "barcode" ? "📷 Mode: Barcode Scan" : (_currentContext == "invoice" ? "🧾 Mode: Receipt Scan" : "📸 Mode: Item Image");
            if (ScannedBarcodeLabel != null) ScannedBarcodeLabel.Text = "";
            // [ADDED v2026.9.24] Sending an existing PDF only makes sense for a
            // receipt, so the button follows the receipt context and nothing else.
            if (PdfButton != null) PdfButton.IsVisible = _currentContext == "invoice";
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
                string haUrl = HaCredentials.GetUrl();
                if (string.IsNullOrEmpty(haUrl))
                {
                    await ShowAlertAsync("Not configured", "Set the Home Assistant address in Settings first.", "OK");
                    return;
                }

                // [MODIFIED v2026.9.24] A scan the AI has to read takes far longer
                // than handing a photo to an open panel, so the app_scan route is
                // given room to finish instead of closing the socket under it.
                using var client = await HaCredentials.CreateClientAsync(
                    TimeSpan.FromSeconds(_startedInApp ? 180 : 30));

                // [ADDED v2026.9.24] Two routes, two endpoints.
                //
                // ext_camera_upload does not scan: it fires an event and the open
                // panel does the work. That is right when the panel launched us and
                // useless when nothing is listening - it answers 200 either way.
                // app_scan runs the scan inside Home Assistant and answers with what
                // actually happened, which is what a scan started here needs.
                string url;
                object payload;
                if (_startedInApp)
                {
                    url = $"{haUrl}/api/home_organizer/app_scan";
                    payload = isBarcode
                        ? (object)new { kind = "barcode", barcode = payloadData, language = VoiceLanguage.LanguageCode() }
                        : new { kind = "receipt", image_data = payloadData, mime_type = MimeOf(payloadData), language = VoiceLanguage.LanguageCode() };
                }
                else
                {
                    url = $"{haUrl}/api/home_organizer/ext_camera_upload";
                    payload = isBarcode
                        ? (object)new { barcode_data = payloadData, context = _currentContext }
                        : new { image_data = payloadData, context = _currentContext, apply_ai_bg = _useAiBg };
                }

                var response = await client.PostAsJsonAsync(url, payload);
                // [MODIFIED v2026.9.24] A failed upload used to look exactly
                // like one that worked: catch {} swallowed the exception, a
                // non-success status was ignored, and the overlay closed
                // either way. The photograph was gone and nothing said so.
                if (response.IsSuccessStatusCode)
                {
                    // [MODIFIED v2026.9.24] Quitting is right only when the panel sent
                    // us here: the user is looking at the browser and wants to be back
                    // in it. A scan started in this app has nowhere to return to, so it
                    // says what happened and stays open.
                    if (_startedInApp)
                    {
                        _startedInApp = false;
                        if (isBarcode)
                        {
                            // [FIXED v2026.9.26] The lookup only SUGGESTS a name; it
                            // creates nothing. This used to stop here and tell the
                            // user to open the panel and file an item that had never
                            // been created - a success message for something that
                            // did not happen. The second step is below.
                            await FinishBarcodeAsync(payloadData,
                                await response.Content.ReadAsStringAsync());
                        }
                        else
                        {
                            // [FIXED v2026.9.26] This used to announce that the lines
                            // were waiting in the Review tab whatever came back. Five
                            // different replies mean nothing was saved - the model
                            // failed, the document could not be read, or the receipt
                            // was a duplicate - and all five were reported as that
                            // same success. Home Assistant already words the outcome;
                            // it is shown instead of guessed.
                            string said = SaidByHa(await response.Content.ReadAsStringAsync());
                            await ShowAlertAsync("Home Organizer", string.IsNullOrWhiteSpace(said)
                                ? "The receipt was sent, but Home Assistant did not say what happened."
                                : said, "OK");
                        }
                        SwitchToChat_Clicked(this, EventArgs.Empty);
                        UpdateTabState("chat");
                    }
                    else
                    {
                        Microsoft.Maui.Controls.Application.Current?.Quit();
                    }
                }
                else
                {
                    // The body carries the reason when there is one; a bare status
                    // code tells the user nothing they can act on.
                    string why = SaidByHa(await response.Content.ReadAsStringAsync());
                    await ShowAlertAsync("Upload failed", string.IsNullOrWhiteSpace(why)
                        ? $"Home Assistant answered {(int)response.StatusCode} {response.StatusCode}."
                        : why, "OK");
                }
            }
            catch (Exception ex)
            {
                await ShowAlertAsync("Upload failed", ex.Message, "OK");
            }
            finally { _isProcessing = false; if (LoadingOverlay != null) LoadingOverlay.IsVisible = false; }
        }

        // [ADDED v2026.9.26] What Home Assistant said about a scan, in its own
        // words. "result.response" is the sentence the panel would have shown -
        // how many lines were stored, that the receipt was already saved, or that
        // the document could not be read. "message" is there when the request was
        // refused outright. Empty when the body carries neither, and the caller
        // then says so rather than inventing an outcome.
        private static string SaidByHa(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return "";
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("result", out var result)
                    && result.ValueKind == JsonValueKind.Object
                    && result.TryGetProperty("response", out var said))
                {
                    return said.GetString() ?? "";
                }
                if (root.TryGetProperty("message", out var message))
                {
                    return message.GetString() ?? "";
                }
            }
            catch (Exception)
            {
            }
            return "";
        }

        // [ADDED v2026.9.26] The second half of a barcode scan.
        //
        // Scanning a barcode in Home Assistant is two steps and only the first was
        // wired. Step one looks the code up - in the history of codes seen before,
        // or by asking the model - and SUGGESTS a name. Step two is what actually
        // creates the item, in the pending state the Review tab exists for.
        //
        // The name is confirmed rather than assumed, because a name guessed from a
        // barcode is a guess, and because this is exactly what the panel does at
        // the same point.
        private async Task FinishBarcodeAsync(string barcode, string lookupBody)
        {
            string suggested = "";
            try
            {
                using var doc = JsonDocument.Parse(lookupBody);
                if (doc.RootElement.TryGetProperty("result", out var result))
                {
                    // "item" when this code has been seen before, so the name the
                    // user chose last time comes back. "suggestion" when it has not.
                    if (result.TryGetProperty("item", out var known)
                        && known.TryGetProperty("name", out var knownName))
                    {
                        suggested = knownName.GetString() ?? "";
                    }
                    else if (result.TryGetProperty("suggestion", out var guess)
                             && guess.TryGetProperty("name", out var guessName))
                    {
                        suggested = guessName.GetString() ?? "";
                    }
                }
            }
            catch (Exception)
            {
                // An unreadable reply is not a reason to lose the scan - the user
                // can still type the name against the code below.
            }

            string? name = await Microsoft.Maui.ApplicationModel.MainThread.InvokeOnMainThreadAsync(
                () => this.DisplayPromptAsync($"Barcode {barcode}", "What is this product?",
                                              "Add", "Cancel", initialValue: suggested));
            if (string.IsNullOrWhiteSpace(name)) return;   // the user cancelled

            var (ok, body) = await PostAppScanAsync(new
            {
                kind = "resolve_barcode",
                barcode,
                name = name.Trim(),
                language = VoiceLanguage.LanguageCode(),
            }, TimeSpan.FromSeconds(120));

            if (!ok)
            {
                await ShowAlertAsync("Not saved", body, "OK");
                return;
            }
            // Home Assistant answers "Added <name> to the Review tab." when it
            // stored the item, so that is what is shown.
            string outcome = SaidByHa(body);
            await ShowAlertAsync("Home Organizer", string.IsNullOrWhiteSpace(outcome)
                ? $"\"{name.Trim()}\" was sent, but Home Assistant did not say what happened."
                : outcome, "OK");
        }

        // [ADDED v2026.9.26] One place that posts to app_scan, so the scan route and
        // the resolve route cannot drift apart.
        private async Task<(bool ok, string body)> PostAppScanAsync(object payload, TimeSpan timeout)
        {
            string haUrl = HaCredentials.GetUrl();
            if (string.IsNullOrEmpty(haUrl)) return (false, "The Home Assistant address is not set.");
            try
            {
                using var client = await HaCredentials.CreateClientAsync(timeout);
                var res = await client.PostAsJsonAsync($"{haUrl}/api/home_organizer/app_scan", payload);
                string body = await res.Content.ReadAsStringAsync();
                if (!res.IsSuccessStatusCode)
                {
                    return (false, $"Home Assistant answered {(int)res.StatusCode} {res.StatusCode}.");
                }
                return (true, body);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        // [ADDED v2026.9.24] Send a receipt that is already a PDF - an emailed
        // invoice, a downloaded order - instead of photographing a screen.
        //
        // It travels the same route as a photograph: the same upload endpoint,
        // the same event, the same receipt scanner. Only the data URL prefix
        // differs, and that prefix is how the panel tells the two apart.
        private async void PickPdf_Clicked(object? sender, EventArgs e)
        {
#if ANDROID
            if (_isProcessing) return;

            var uri = await HO_Mind_AI.Platforms.Android.PdfPicker.PickAsync();
            if (uri == null) return;   // the user backed out

            byte[]? bytes = HO_Mind_AI.Platforms.Android.PdfPicker.ReadAllBytes(uri);
            if (bytes == null || bytes.Length == 0)
            {
                await ShowAlertAsync("Could not read the file", "That document could not be opened. Try picking it from the folder itself rather than from a recent-files shortcut.", "OK");
                return;
            }

            // Base64 adds a third to the size, and the receiving end has its own
            // body limit. Capped here so the user is told why, instead of the
            // upload failing somewhere they cannot see.
            const int MaxBytes = 7 * 1024 * 1024;
            if (bytes.Length > MaxBytes)
            {
                await ShowAlertAsync("File is too large", $"This PDF is about {bytes.Length / (1024 * 1024)} MB. The limit is 7 MB.", "OK");
                return;
            }

            _isProcessing = true;
            if (LoadingOverlay != null) LoadingOverlay.IsVisible = true;
            await SendDataToHA("data:application/pdf;base64," + Convert.ToBase64String(bytes), isBarcode: false);
#else
            await ShowAlertAsync("Not available here", "Picking a PDF is implemented for Android.", "OK");
#endif
        }

        // [ADDED v2026.9.24] The type is already declared at the front of the data
        // URL, so it is read from there rather than tracked in a second place that
        // could disagree with it.
        private static string MimeOf(string dataUrl)
        {
            if (string.IsNullOrEmpty(dataUrl) || !dataUrl.StartsWith("data:")) return "image/jpeg";
            int semi = dataUrl.IndexOf(';');
            if (semi <= 5) return "image/jpeg";
            return dataUrl.Substring(5, semi - 5);
        }

        private Task ShowAlertAsync(string title, string message, string cancel) => Microsoft.Maui.ApplicationModel.MainThread.InvokeOnMainThreadAsync(() => this.DisplayAlertAsync(title, message, cancel));
        private void TestSTT_Clicked(object sender, EventArgs e) { string toSpeak = "Testing STT removed from this mode."; if (TestInput != null) TestInput.Text = toSpeak; }
        private async void TestTTS_Clicked(object sender, EventArgs e) { string toSpeak = "Testing voice audio output."; if (TestInput != null && !string.IsNullOrWhiteSpace(TestInput.Text) && TestInput.Text != "Listening...") toSpeak = TestInput.Text; await SpeakTextAsync(toSpeak); }
        private void OpenSettings_Clicked(object? sender, EventArgs e)
        {
            if (SettingsModal != null) SettingsModal.IsVisible = true;
            // [ADDED v2026.10.21] Name the saved devices in their dropdowns.
            _ = NamePhoneFieldsAsync();
        }
        private void Close_Clicked(object? sender, EventArgs e) => Microsoft.Maui.Controls.Application.Current?.Quit();

        private void LoadSettings()
        {
            if (ServerUrlEntry != null) ServerUrlEntry.Text = HaCredentials.GetUrl();
            // [MODIFIED v2026.9.24] The token lives in the device secure store
            // and is read asynchronously. LoadSettings() stays synchronous
            // because the constructor calls it, so the field is filled when
            // the value arrives rather than being awaited here.
            if (TokenEntry != null) _ = FillTokenFieldAsync();
            if (AgentIdEntry != null) AgentIdEntry.Text = Preferences.Get("ha_agent_id", "conversation.ho_ai_agent");
            if (NotifyIdEntry != null) NotifyIdEntry.Text = UserRoster.PrimaryDeviceId();
            if (PrimaryNameEntry != null) PrimaryNameEntry.Text = UserRoster.PrimaryName();
            // [ADDED v2026.10.21] The main phone's dropdown, and every person's
            // in RenderUsersList; the refreshers start over with the screen.
            _phoneFieldRefreshers.Clear();
            if (PrimaryPhoneSlot != null && NotifyIdEntry != null) PrimaryPhoneSlot.Content = PhoneField(NotifyIdEntry, PrimaryNameEntry);
            RenderUsersList();
            if (UseBtMicSwitch != null) UseBtMicSwitch.IsToggled = Preferences.Get("use_bt_mic", false);
            if (UseShakeMicSwitch != null) UseShakeMicSwitch.IsToggled = Preferences.Get("use_shake_mic", false);
            if (EnableLoggingSwitch != null) EnableLoggingSwitch.IsToggled = Preferences.Get("enable_file_logging", false);
            if (LanguagePicker != null) { string savedLang = VoiceLanguage.DisplayName(); int index = LanguagePicker.Items.IndexOf(savedLang); LanguagePicker.SelectedIndex = index >= 0 ? index : 0; }
            if (ActiveAgentLabel != null) ActiveAgentLabel.Text = $"Agent: {Preferences.Get("ha_agent_id", "conversation.ho_ai_agent")}";

            // [ADDED v2.87.0] Load volume slider
            if (VolumeSlider != null)
            {
                VolumeSlider.Value = Preferences.Get("app_custom_volume", 100.0);
                if (VolumeLabel != null) VolumeLabel.Text = $"{(int)VolumeSlider.Value}%";
            }
        }

        // [ADDED v2026.9.26] The people rows in Settings, built here because the
        // list has no fixed length.
        private void RenderUsersList()
        {
            if (UsersList == null) return;
            UsersList.Children.Clear();
            foreach (var user in UserRoster.Others())
            {
                UsersList.Children.Add(BuildUserRow(user));
            }
        }

        private View BuildUserRow(HoUser user)
        {
            var name = new Entry
            {
                Placeholder = "Name, alias, ...",
                Text = user.Name,
                TextColor = Microsoft.Maui.Graphics.Colors.White,
                BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#111"),
            };
            // [MODIFIED v2026.10.21] The id is chosen from a dropdown of the
            // devices Home Assistant knows. The 32 characters live in this hidden
            // field, which ReadUserRows and Save read exactly as before.
            var id = new Entry { Text = user.DeviceId, IsVisible = false };
            var remove = new Button
            {
                Text = "✕",
                BackgroundColor = Microsoft.Maui.Graphics.Colors.Transparent,
                TextColor = Microsoft.Maui.Graphics.Colors.White,
                WidthRequest = 40,
                HeightRequest = 40,
                Padding = 0,
            };

            var row = new Grid
            {
                ColumnSpacing = 8,
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Star },
                    new ColumnDefinition { Width = GridLength.Star },
                    new ColumnDefinition { Width = GridLength.Auto },
                },
            };
            // Order matters: ReadUserRows takes the row's FIRST Entry as the name
            // and the SECOND as the id.
            row.Add(name, 0);
            row.Add(PhoneField(id, name), 1);
            row.Add(remove, 2);
            row.Add(id, 1);

            // Removed from the screen only. Nothing is stored until Save, so a
            // row taken out by mistake comes back by closing Settings.
            remove.Clicked += (s, e) => UsersList?.Children.Remove(row);
            return row;
        }

        // [ADDED v2026.10.21] The devices Home Assistant knows, as last read, and
        // the dropdowns showing them - so a dropdown can name the device whose id
        // it holds instead of showing 32 characters.
        private List<HaPhone> _knownPhones = new();
        private readonly List<Action> _phoneFieldRefreshers = new();
        private const string TypeIdByHand = "Type the ID by hand…";

        private string PhoneFieldText(string? deviceId)
        {
            string id = (deviceId ?? "").Trim();
            if (id.Length == 0) return "Choose phone";
            var known = _knownPhones.FirstOrDefault(p => string.Equals(p.DeviceId, id, StringComparison.OrdinalIgnoreCase));
            if (known != null) return known.Label;
            return $"Saved device …{id[Math.Max(0, id.Length - 4)..]}";
        }

        private void RefreshPhoneFields()
        {
            foreach (var refresh in _phoneFieldRefreshers) refresh();
        }

        // [ADDED v2026.10.21] A dropdown in place of the device id field. Tapped,
        // it reads the devices from Home Assistant with the address and token as
        // they stand on this screen (Save may not have been pressed yet) and opens
        // them as a list; the one chosen is shown by name and its id kept in the
        // hidden field beside it. An empty name is filled with the device's owner.
        private View PhoneField(Entry id, Entry? name)
        {
            var text = new Label
            {
                TextColor = Microsoft.Maui.Graphics.Colors.White,
                VerticalOptions = LayoutOptions.Center,
                LineBreakMode = LineBreakMode.TailTruncation,
                FontSize = 14,
            };
            var arrow = new Label
            {
                Text = "▾",
                TextColor = Microsoft.Maui.Graphics.Color.FromArgb("#AAA"),
                VerticalOptions = LayoutOptions.Center,
                FontSize = 16,
            };
            var inner = new Grid
            {
                Padding = new Thickness(10, 0),
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Star },
                    new ColumnDefinition { Width = GridLength.Auto },
                },
            };
            inner.Add(text, 0);
            inner.Add(arrow, 1);
            var box = new Border
            {
                Content = inner,
                BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#111"),
                StrokeThickness = 0,
                MinimumHeightRequest = 44,
            };

            void Refresh() => text.Text = PhoneFieldText(id.Text);
            id.TextChanged += (s, e) => Refresh();
            _phoneFieldRefreshers.Add(Refresh);
            Refresh();

            var tap = new TapGestureRecognizer();
            tap.Tapped += async (s, e) =>
            {
                var chosen = await ChoosePhoneAsync(name?.Text);
                if (chosen == null) return;
                id.Text = chosen.DeviceId;
                if (name != null && string.IsNullOrWhiteSpace(name.Text) && chosen.PersonName.Length > 0)
                    name.Text = chosen.PersonName;
            };
            box.GestureRecognizers.Add(tap);
            return box;
        }

        // The list the dropdown opens. Two devices with the same label get the end
        // of their id added, so the line chosen maps back to exactly one device.
        // The last line always lets the id be typed by hand: an older Home
        // Organizer has no list to give, and then the reason is the list's title.
        private async Task<HaPhone?> ChoosePhoneAsync(string? forName)
        {
            var (phones, error) = await PhoneDirectory.FetchAsync(ServerUrlEntry?.Text, TokenEntry?.Text);
            if (phones != null)
            {
                _knownPhones = phones;
                RefreshPhoneFields();
            }
            var list = phones ?? new List<HaPhone>();
            var dupes = list.GroupBy(p => p.Label).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            var lines = list.Select(p => dupes.Contains(p.Label)
                ? $"{p.Label} [{p.DeviceId[Math.Max(0, p.DeviceId.Length - 4)..]}]"
                : p.Label).ToList();
            lines.Add(TypeIdByHand);

            string title = error ?? (string.IsNullOrWhiteSpace(forName) ? "Choose a device" : $"Device for {forName.Trim()}");
            string? chosen = await Microsoft.Maui.ApplicationModel.MainThread.InvokeOnMainThreadAsync(
                () => DisplayActionSheetAsync(title, "Cancel", null, lines.ToArray()));
            if (chosen == TypeIdByHand)
            {
                string? typed = await Microsoft.Maui.ApplicationModel.MainThread.InvokeOnMainThreadAsync(
                    () => DisplayPromptAsync("Device ID",
                        "In Home Assistant: Settings > Devices > the device. Copy the long code at the end of the page address.",
                        "OK", "Cancel", maxLength: 64));
                typed = typed?.Trim();
                return string.IsNullOrEmpty(typed) ? null : new HaPhone { DeviceId = typed };
            }
            int i = chosen == null ? -1 : lines.IndexOf(chosen);
            return i >= 0 && i < list.Count ? list[i] : null;
        }

        // [ADDED v2026.10.21] When Settings opens, the saved devices are named
        // quietly in the background - nothing is shown if Home Assistant cannot
        // be reached, the dropdowns simply keep their short form.
        private async Task NamePhoneFieldsAsync()
        {
            var (phones, _) = await PhoneDirectory.FetchAsync(ServerUrlEntry?.Text, TokenEntry?.Text);
            if (phones == null) return;
            _knownPhones = phones;
            Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(RefreshPhoneFields);
        }

        // [ADDED v2026.10.21] Everyone's phone in one tap. ADDS rows only: a row
        // already on screen, or the main phone, is never changed or removed, so
        // running it twice, or after adding someone by hand, does no harm.
        private async void LoadHousehold_Clicked(object? sender, EventArgs e)
        {
            var (phones, error) = await PhoneDirectory.FetchAsync(ServerUrlEntry?.Text, TokenEntry?.Text);
            if (phones == null)
            {
                await ShowAlertAsync("Household", error ?? "No devices found.", "OK");
                return;
            }
            _knownPhones = phones;

            string primaryNote = "";
            var mine = phones.Where(p => p.IsMe && p.CanNotify).ToList();
            if (NotifyIdEntry != null && string.IsNullOrWhiteSpace(NotifyIdEntry.Text) && mine.Count == 1)
            {
                NotifyIdEntry.Text = mine[0].DeviceId;
                if (PrimaryNameEntry != null && string.IsNullOrWhiteSpace(PrimaryNameEntry.Text)) PrimaryNameEntry.Text = mine[0].PersonName;
                primaryNote = "Your phone was filled in. ";
            }

            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(NotifyIdEntry?.Text)) known.Add(NotifyIdEntry!.Text.Trim());
            foreach (var u in ReadUserRows())
                if (!string.IsNullOrWhiteSpace(u.DeviceId)) known.Add(u.DeviceId.Trim());

            int added = 0;
            foreach (var p in phones.Where(p => !p.IsMe && p.CanNotify))
            {
                if (!known.Add(p.DeviceId)) continue;
                UsersList?.Children.Add(BuildUserRow(new HoUser { Name = p.PersonName, DeviceId = p.DeviceId }));
                added++;
            }
            RefreshPhoneFields();

            string message = added > 0
                ? $"{primaryNote}Added {added} {(added == 1 ? "person" : "people")}. Change each name to the way you say it - you can add more names separated by commas. Then press Save."
                : $"{primaryNote}Everyone with a phone is already on the list.";
            await ShowAlertAsync("Household", message, "OK");
        }

        private void AddUser_Clicked(object? sender, EventArgs e)
        {
            UsersList?.Children.Add(BuildUserRow(new HoUser()));
        }

        // What is on screen right now, which is what Save writes.
        private List<HoUser> ReadUserRows()
        {
            var users = new List<HoUser>();
            if (UsersList == null) return users;
            foreach (var child in UsersList.Children)
            {
                if (child is not Grid row) continue;
                var entries = row.Children.OfType<Entry>().ToList();
                if (entries.Count < 2) continue;
                users.Add(new HoUser
                {
                    Name = entries[0].Text ?? "",
                    DeviceId = entries[1].Text ?? "",
                });
            }
            return users;
        }

        private async Task FillTokenFieldAsync()
        {
            string token = await HaCredentials.GetTokenAsync();
            Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
            {
                if (TokenEntry != null) TokenEntry.Text = token;
            });
        }

        // [ADDED v2.87.0] Event handler for the volume slider label
        private void VolumeSlider_ValueChanged(object sender, ValueChangedEventArgs e)
        {
            if (VolumeLabel != null) VolumeLabel.Text = $"{(int)e.NewValue}%";
        }

        private async void SaveSettings_Clicked(object? sender, EventArgs e)
        {
            HaCredentials.SetUrl(ServerUrlEntry?.Text);
            // [ADDED v2026.10.22] Show the address as it was stored - "http://"
            // added to a bare IP - and say so when it still cannot be used.
            if (ServerUrlEntry != null) ServerUrlEntry.Text = HaCredentials.GetUrl();
            string savedUrl = HaCredentials.GetUrl();
            if (savedUrl.Length > 0 && !HaCredentials.IsUsableUrl(savedUrl))
                await ShowAlertAsync("Settings", "The Home Assistant address must look like http://192.168.1.100:8123", "OK");
            // [MODIFIED v2026.9.24] The token goes to the device secure store.
            // When that store refuses it there is deliberately no fallback to
            // writing plain text - the failure is reported below instead.
            bool tokenSaved = await HaCredentials.TrySetTokenAsync(TokenEntry?.Text?.Trim() ?? "");
            Preferences.Set("ha_agent_id", AgentIdEntry?.Text?.Trim() ?? "conversation.ho_ai_agent");
            UserRoster.SetPrimaryDeviceId(NotifyIdEntry?.Text);
            UserRoster.SetPrimaryName(PrimaryNameEntry?.Text);
            UserRoster.SaveOthers(ReadUserRows());
            if (UseBtMicSwitch != null) Preferences.Set("use_bt_mic", UseBtMicSwitch.IsToggled);
            if (UseShakeMicSwitch != null) Preferences.Set("use_shake_mic", UseShakeMicSwitch.IsToggled);
            if (EnableLoggingSwitch != null) Preferences.Set("enable_file_logging", EnableLoggingSwitch.IsToggled);
            if (LanguagePicker?.SelectedIndex >= 0) VoiceLanguage.SetDisplayName(LanguagePicker.Items[LanguagePicker.SelectedIndex]);
            if (ActiveAgentLabel != null) ActiveAgentLabel.Text = $"Agent: {AgentIdEntry?.Text?.Trim() ?? "conversation.ho_ai_agent"}";

            // [ADDED v2.87.0] Save volume
            if (VolumeSlider != null) Preferences.Set("app_custom_volume", VolumeSlider.Value);

            if (SettingsModal != null) SettingsModal.IsVisible = false;
            UpdateBleManagerStatus();

            // [ADDED v2026.9.26] An id that is not a device id resolves to nothing,
            // and the reminder then goes to whoever asked rather than the person
            // named - silently. Said out loud here instead.
            var suspect = UserRoster.SuspectEntries();
            if (suspect.Count > 0)
            {
                await ShowAlertAsync("Check these IDs",
                    "A Home Assistant device ID is 32 hexadecimal characters, from Settings > Devices. These do not look like one, and a reminder aimed at them will arrive on your own phone instead:\n\n"
                    + string.Join("\n", suspect), "OK");
            }

            if (!tokenSaved)
            {
                await ShowAlertAsync("Token not saved", "This device secure storage refused the token, so it was not saved. It is deliberately not written in plain text instead.", "OK");
            }
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