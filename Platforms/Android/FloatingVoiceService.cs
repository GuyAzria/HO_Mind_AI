// FloatingVoiceService.cs — DIAGNOSTIC VERSION v4.35.0
// [MODIFIED v4.35.0 | 2026-07-28] Purpose: Removed the custom start beep (PlayStartBeepAsync) from TriggerListeningSequence to allow the default Android STT beep to play alone.
// [MODIFIED v4.33.0 | 2026-06-16] Purpose: Fixed the real-time MediaSession text update bug. Android silently ignores MediaMetadata updates if they originate from a background thread (like the STT engine event). Wrapped UpdateMediaSessionText in MainThread.BeginInvokeOnMainThread and added a PlaybackState refresh ping to force the OS to immediately redraw the media player widget with the live transcription.
// [ADDED v4.32.0 | 2026-06-16] Purpose: Added a missing 'Stop' beep at the end of voice capture sessions. Redesigned the audio cues to mimic a "Star Trek" communicator (Combadge) style: a fast double-chirp for opening the mic, and a single acknowledgment blip for closing, using precise ToneGenerator sequencing.
// [ADDED v4.31.0 | 2026-06-16] Purpose: Dynamically update MediaSession metadata with real-time Speech-To-Text transcription results.
// [ADDED v4.30.0 | 2026-06-16] Purpose: Completely destroy and Release() the MediaSession when BT is toggled OFF, rather than just setting it to Stopped. 
// [ADDED v4.29.0 | 2026-06-15] Purpose: Abstracted MediaSession activation into EnableMediaRouting and DisableMediaRouting to remove the widget from the Android lock screen.
// [ADDED v4.28.0 | 2026-06-14] Purpose: Battery optimization and dynamic UI control. Active KeepAlive loop and toggle button logic.
// [MODIFIED v4.27.0 | 2026-06-14] Purpose: Bypassed Android 12+ Background Service Launch restrictions via direct PendingIntent service routing.
// [ADDED v4.26.0 | 2026-06-14] Purpose: Fake MediaMetadata and 44100Hz Stereo AudioTrack to trick Samsung Bluetooth AVRCP.
// [ADDED v4.25.0 | 2026-06-14] Purpose: Expanded MediaSession callbacks (Transport controls).
// [ADDED v4.24.0 | 2026-06-14] Purpose: Direct overrides for Transport Controls (OnPlay, OnPause, OnStop).
// [ADDED v4.23.0 | 2026-06-14] Purpose: Solved Android 11+ background media routing with Foreground Notification MediaStyle.
// [ADDED v4.22.0 | 2026-06-14] Purpose: Continuous Zello-style 'KeepAlive' AudioTrack loop.
// [ADDED v4.21.0 | 2026-06-14] Purpose: Obsolete MediaSessionFlags and global AudioFocus.Gain.
// [ADDED v4.20.0 | 2026-06-14] Purpose: Transient silence hack.
// [MODIFIED v4.19.0 | 2026-06-14] Purpose: PlaybackStateCode enum updates.
// [MODIFIED v4.18.0 | 2026-06-14] Purpose: Fixed Spotify resume bug.
// [MODIFIED v4.17.0 | 2026-06-14] Purpose: Conditionally hijack AudioFocus based on _musicWasPlayingBeforeListen.

using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Hardware;
using Android.OS;
using Android.Views;
using Android.Widget;
using Android.Media.Session;
using CommunityToolkit.Maui.Media;
using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

using AView = Android.Views.View;
using AButton = Android.Widget.Button;
using ATextView = Android.Widget.TextView;
using ADrawable = Android.Graphics.Drawables;
using AColor = Android.Graphics.Color;
using AProvider = Android.Provider;
using ANet = Android.Net;
using AAPP = Android.App;
using PowerManager = Android.OS.PowerManager;

using APaint = Android.Graphics.Paint;
using ACanvas = Android.Graphics.Canvas;
using APath = Android.Graphics.Path;
using Java.Interop;
using Path = System.IO.Path;
using Environment = System.Environment;

namespace HO_Mind_AI.Platforms.Android
{
    [Service(ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeMicrophone)]
    public class FloatingVoiceService : Service, ISensorEventListener
    {
        private const string TAG = "FVS";

        public static Action<string>? OnCarSttUpdated;
        public static Action? OnCarListeningStopped;

        private IWindowManager? _windowManager;
        private global::Android.Views.View? _floatingView;
        private WindowManagerLayoutParams? _layoutParams;

        private global::Android.Widget.TextView? _sttTextView;
        private CancellationTokenSource? _sttCts;
        private bool _isListening = false;

        private bool _isRestarting = false;
        private bool _isTranscriptionRunning = false;

        private SensorManager? _sensorManager;
        private Sensor? _linearAcceleration;
        private Sensor? _gravitySensor;
        private float _currentGravityY = 0f;

        private const float SHAKE_THRESHOLD_X = 12.0f;
        private const int MIN_SHAKE_GAP_MS = 150;
        private const int MAX_SHAKE_WINDOW_MS = 600;

        private long _lastShakeMs = 0;
        private int _shakeCount = 0;

        private CancellationTokenSource? _waveCts;
        private SineWaveView? _sineWaveView;
        private long _lastSpeechDetectedMs = 0;

        private bool _musicWasPlayingBeforeListen = false;

        private MediaSession? _mediaSession;
        private HeadsetButtonCallback? _sessionCallback;

        private global::Android.Media.AudioTrack? _keepAliveTrack;
        private CancellationTokenSource? _keepAliveCts;

        private AButton? _btToggleBtn;

        public override IBinder? OnBind(Intent? intent) => null;

        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            Log($"=== SERVICE STARTING v4.35.0 | Action: {intent?.Action} ===");

            if (_windowManager == null)
            {
                bool useBtMic = Microsoft.Maui.Storage.Preferences.Get("use_bt_mic", false);
                if (useBtMic)
                {
                    EnableMediaRouting();
                }
                else
                {
                    StartForegroundServiceWithNotification();
                }

                InitializeFloatingWindow();
                RegisterSensor();
            }

            if (intent?.Action == Intent.ActionMediaButton)
            {
                var keyEvent = (KeyEvent?)intent.GetParcelableExtra(Intent.ExtraKeyEvent);
                if (keyEvent != null && keyEvent.Action == KeyEventActions.Down)
                {
                    Log($"[GESTURE] Hardware Button ({keyEvent.KeyCode}) received directly in OnStartCommand!");
                    if (!_isListening && !_isTranscriptionRunning)
                    {
                        _ = TriggerListeningSequence();
                    }
                }
            }
            else if (intent?.Action == "START_LISTENING")
            {
                _ = TriggerListeningSequence();
            }

            return StartCommandResult.Sticky;
        }

        public bool IsPhoneInCall()
        {
            try
            {
                var context = global::Android.App.Application.Context;
                var tm = (global::Android.Telephony.TelephonyManager?)context.GetSystemService(global::Android.Content.Context.TelephonyService);
                if (tm != null)
                {
                    return tm.CallState != global::Android.Telephony.CallState.Idle;
                }
            }
            catch (Exception ex)
            {
                Log("Error checking phone call state: " + ex.Message);
            }
            return false;
        }

        private void UpdateMediaSessionText(string title, string artist = "HO Assistant")
        {
            if (_mediaSession == null) return;

            Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    string safeTitle = string.IsNullOrWhiteSpace(title) ? "HO Voice Assistant" : title;

                    var metadataBuilder = new global::Android.Media.MediaMetadata.Builder()
                        .PutString(global::Android.Media.MediaMetadata.MetadataKeyTitle, safeTitle)
                        .PutString(global::Android.Media.MediaMetadata.MetadataKeyArtist, artist)
                        .PutString(global::Android.Media.MediaMetadata.MetadataKeyAlbum, "HO Platform");
                    _mediaSession.SetMetadata(metadataBuilder.Build());

                    // Force Android to refresh the Media Player widget by pinging the PlaybackState
                    var stateBuilder = new PlaybackState.Builder()
                        .SetActions(
                            PlaybackState.ActionPlay |
                            PlaybackState.ActionPause |
                            PlaybackState.ActionPlayPause |
                            PlaybackState.ActionStop |
                            PlaybackState.ActionSkipToNext |
                            PlaybackState.ActionSkipToPrevious |
                            PlaybackState.ActionFastForward |
                            PlaybackState.ActionRewind);

                    stateBuilder.SetState(PlaybackStateCode.Playing, 0, 1.0f);
                    _mediaSession.SetPlaybackState(stateBuilder.Build());
                }
                catch (Exception ex)
                {
                    Log("UpdateMediaText Error: " + ex.Message);
                }
            });
        }

        private void EnableMediaRouting()
        {
            try
            {
                if (_mediaSession == null)
                {
                    _mediaSession = new MediaSession(this, "HO_Voice_MediaSession");

#pragma warning disable CS0618
                    _mediaSession.SetFlags(MediaSessionFlags.HandlesMediaButtons | MediaSessionFlags.HandlesTransportControls);
#pragma warning restore CS0618

                    var mediaButtonIntent = new Intent(Intent.ActionMediaButton);
                    mediaButtonIntent.SetClass(this, typeof(FloatingVoiceService));
                    var pendingIntent = PendingIntent.GetService(this, 0, mediaButtonIntent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
                    _mediaSession.SetMediaButtonReceiver(pendingIntent);

                    _sessionCallback = new HeadsetButtonCallback(this);
                    _mediaSession.SetCallback(_sessionCallback);

                    var stateBuilder = new PlaybackState.Builder()
                        .SetActions(
                            PlaybackState.ActionPlay |
                            PlaybackState.ActionPause |
                            PlaybackState.ActionPlayPause |
                            PlaybackState.ActionStop |
                            PlaybackState.ActionSkipToNext |
                            PlaybackState.ActionSkipToPrevious |
                            PlaybackState.ActionFastForward |
                            PlaybackState.ActionRewind);

                    stateBuilder.SetState(PlaybackStateCode.Playing, 0, 1.0f);
                    _mediaSession.SetPlaybackState(stateBuilder.Build());

                    UpdateMediaSessionText("Ready to Listen", "Waiting for command...");
                }

                _mediaSession.Active = true;

                var am = (global::Android.Media.AudioManager?)GetSystemService(AudioService);
#pragma warning disable CA1422
                am?.RequestAudioFocus(null, global::Android.Media.Stream.Music, global::Android.Media.AudioFocus.Gain);
#pragma warning restore CA1422

                StartKeepAliveAudio();

                StartForegroundServiceWithNotification();

                Log("✅ MediaRouting ENABLED: OS Player is Active");
            }
            catch (Exception ex) { Log("EnableMediaRouting error: " + ex.Message); }
        }

        private void DisableMediaRouting()
        {
            try
            {
                StopKeepAliveAudio();

                var am = (global::Android.Media.AudioManager?)GetSystemService(AudioService);
#pragma warning disable CA1422
                am?.AbandonAudioFocus(null);
#pragma warning restore CA1422

                if (_mediaSession != null)
                {
                    _mediaSession.Active = false;
                    _mediaSession.Release();
                    _mediaSession = null;
                    _sessionCallback = null;
                }

                StartForegroundServiceWithNotification();

                Log("🛑 MediaRouting DISABLED: OS Player Destroyed & Released to Spotify");
            }
            catch (Exception ex) { Log("DisableMediaRouting error: " + ex.Message); }
        }

        private void StartKeepAliveAudio()
        {
            try
            {
                if (_keepAliveTrack != null) return;

                int sampleRate = 44100;
                int bufferSize = global::Android.Media.AudioTrack.GetMinBufferSize(
                    sampleRate,
                    global::Android.Media.ChannelOut.Stereo,
                    global::Android.Media.Encoding.Pcm16bit);

#pragma warning disable CA1422
                _keepAliveTrack = new global::Android.Media.AudioTrack(
                    global::Android.Media.Stream.Music,
                    sampleRate,
                    global::Android.Media.ChannelOut.Stereo,
                    global::Android.Media.Encoding.Pcm16bit,
                    bufferSize,
                    global::Android.Media.AudioTrackMode.Stream);
#pragma warning restore CA1422

                _keepAliveTrack.Play();

                _keepAliveCts = new CancellationTokenSource();

                Task.Run(async () =>
                {
                    byte[] silence = new byte[bufferSize];

                    while (!_keepAliveCts.Token.IsCancellationRequested)
                    {
                        _keepAliveTrack?.Write(silence, 0, silence.Length);
                        await Task.Delay(100);
                    }
                });

                Log("🔥 KEEP ALIVE AUDIO STARTED");
            }
            catch (Exception ex)
            {
                Log("KeepAlive error: " + ex.Message);
            }
        }

        private void StopKeepAliveAudio()
        {
            try
            {
                _keepAliveCts?.Cancel();
                _keepAliveCts = null;

                if (_keepAliveTrack != null)
                {
                    _keepAliveTrack.Stop();
                    _keepAliveTrack.Release();
                    _keepAliveTrack.Dispose();
                    _keepAliveTrack = null;
                }

                Log("🛑 KEEP ALIVE AUDIO STOPPED");
            }
            catch { }
        }

        private class HeadsetButtonCallback : MediaSession.Callback
        {
            private readonly FloatingVoiceService _service;
            public HeadsetButtonCallback(FloatingVoiceService service)
            {
                _service = service;
            }

            public override void OnPlay()
            {
                FloatingVoiceService.Log("[GESTURE] Transport Control: OnPlay triggered.");
                _service.TriggerFromHeadset();
                base.OnPlay();
            }

            public override void OnPause()
            {
                FloatingVoiceService.Log("[GESTURE] Transport Control: OnPause triggered.");
                _service.TriggerFromHeadset();
                base.OnPause();
            }

            public override void OnStop()
            {
                FloatingVoiceService.Log("[GESTURE] Transport Control: OnStop triggered.");
                _service.TriggerFromHeadset();
                base.OnStop();
            }

            public override void OnSkipToNext()
            {
                FloatingVoiceService.Log("[GESTURE] Transport Control: OnSkipToNext (Next Track) triggered.");
                _service.TriggerFromHeadset();
                base.OnSkipToNext();
            }

            public override void OnSkipToPrevious()
            {
                FloatingVoiceService.Log("[GESTURE] Transport Control: OnSkipToPrevious (Prev Track) triggered.");
                _service.TriggerFromHeadset();
                base.OnSkipToPrevious();
            }

            public override void OnFastForward()
            {
                FloatingVoiceService.Log("[GESTURE] Transport Control: OnFastForward triggered.");
                _service.TriggerFromHeadset();
                base.OnFastForward();
            }

            public override void OnRewind()
            {
                FloatingVoiceService.Log("[GESTURE] Transport Control: OnRewind triggered.");
                _service.TriggerFromHeadset();
                base.OnRewind();
            }

            public override bool OnMediaButtonEvent(Intent? mediaButtonIntent)
            {
                if (_service.IsPhoneInCall())
                {
                    FloatingVoiceService.Log("[GESTURE] Ignored headset button due to active/ringing phone call.");
                    return base.OnMediaButtonEvent(mediaButtonIntent);
                }

                var keyEvent = (KeyEvent?)mediaButtonIntent?.GetParcelableExtra(Intent.ExtraKeyEvent);
                if (keyEvent != null && keyEvent.Action == KeyEventActions.Down)
                {
                    var kc = keyEvent.KeyCode;

                    if (kc == Keycode.Headsethook ||
                        kc == Keycode.MediaPlayPause ||
                        kc == Keycode.MediaPlay ||
                        kc == Keycode.MediaPause ||
                        kc == Keycode.MediaNext ||
                        kc == Keycode.MediaPrevious ||
                        kc == Keycode.MediaStop)
                    {
                        FloatingVoiceService.Log($"[GESTURE] Headset Raw Button ({kc}) pressed! Triggering assistant.");
                        _service.TriggerFromHeadset();
                        return true;
                    }
                    else
                    {
                        FloatingVoiceService.Log($"[GESTURE] Unhandled Raw Button ({kc}) pressed.");
                    }
                }
                return base.OnMediaButtonEvent(mediaButtonIntent);
            }
        }

        public void TriggerFromHeadset()
        {
            bool useBtMic = Microsoft.Maui.Storage.Preferences.Get("use_bt_mic", false);
            if (!useBtMic)
            {
                Log("[GESTURE] Ignored headset button because 'use_bt_mic' is OFF.");
                return;
            }

            if (!_isListening && !_isTranscriptionRunning)
            {
                _ = TriggerListeningSequence();
            }
        }

        private void StartForegroundServiceWithNotification()
        {
            const string channelId = "floating_voice_service_channel";
            var nm = (NotificationManager?)GetSystemService(NotificationService);

            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var ch = new NotificationChannel(channelId, "HO Voice Assistant", NotificationImportance.Low);
                nm?.CreateNotificationChannel(ch);
            }

            var notificationBuilder = new Notification.Builder(this, channelId)
                .SetContentTitle("HO Assistant")
                .SetContentText("Ready for voice commands")
                .SetSmallIcon(global::Android.Resource.Drawable.IcDialogInfo)
                .SetCategory(Notification.CategoryService)
                .SetVisibility(NotificationVisibility.Public)
                .SetOngoing(true);

            if (_mediaSession != null)
            {
                var mediaStyle = new Notification.MediaStyle();
                mediaStyle.SetMediaSession(_mediaSession.SessionToken);
                notificationBuilder.SetStyle(mediaStyle);
                notificationBuilder.SetContentText("Listening for headset button...");
            }

            var notification = notificationBuilder.Build();

            if (Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake)
                StartForeground(1002, notification, global::Android.Content.PM.ForegroundService.TypeMicrophone);
            else
                StartForeground(1002, notification);
        }

        private void UpdateBtToggleButtonUI(bool isBtOn)
        {
            if (_btToggleBtn != null)
            {
                Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
                {
                    _btToggleBtn.Text = isBtOn ? "🎧 BT: ON" : "🎧 BT: OFF";
                    _btToggleBtn.SetBackgroundColor(isBtOn ? AColor.ParseColor("#03a9f4") : AColor.ParseColor("#88000000"));
                });
            }
        }

        private void InitializeFloatingWindow()
        {
            var contextToUse = global::Android.App.Application.Context;
            _windowManager = contextToUse.GetSystemService(WindowService).JavaCast<IWindowManager>();

            if (_windowManager == null) return;

            var inflater = LayoutInflater.From(contextToUse);
            _floatingView = inflater?.Inflate(Resource.Layout.floating_assistant, null);

            if (_floatingView == null) return;

            _sttTextView = _floatingView.FindViewById<ATextView>(Resource.Id.stt_text_view);

            var openBtn = _floatingView.FindViewById<AButton>(Resource.Id.open_app_button);
            var closeBtn = _floatingView.FindViewById<AButton>(Resource.Id.close_overlay_button);
            var transcribeBtn = _floatingView.FindViewById<AButton>(Resource.Id.transcribe_button);

            _btToggleBtn = _floatingView.FindViewById<AButton>(Resource.Id.bt_toggle_button);
            if (_btToggleBtn != null)
            {
                bool currentBtState = Microsoft.Maui.Storage.Preferences.Get("use_bt_mic", false);
                UpdateBtToggleButtonUI(currentBtState);

                _btToggleBtn.Click += (s, e) => {
                    bool state = Microsoft.Maui.Storage.Preferences.Get("use_bt_mic", false);
                    bool newState = !state;
                    Microsoft.Maui.Storage.Preferences.Set("use_bt_mic", newState);

                    UpdateBtToggleButtonUI(newState);

                    if (newState)
                    {
                        EnableMediaRouting();
                        Log("[UI] User turned BT ON -> Media Player Activated");
                    }
                    else
                    {
                        DisableMediaRouting();
                        Log("[UI] User turned BT OFF -> Media Player Destroyed & Hidden");
                    }
                };
            }

            if (transcribeBtn != null)
            {
                transcribeBtn.Click += async (s, e) => {
                    if (!_isTranscriptionRunning)
                    {
                        _isTranscriptionRunning = true;
                        if (_isListening) _sttCts?.Cancel();
                        transcribeBtn.Text = "STOP";
                        transcribeBtn.SetBackgroundColor(AColor.Red);
                        await StartContinuousTranscription();
                    }
                    else
                    {
                        _isTranscriptionRunning = false;
                        _isListening = false;
                        transcribeBtn.Text = "Transcribe";
                        transcribeBtn.SetBackgroundColor(AColor.ParseColor("#44FFFFFF"));
                        _sttCts?.Cancel();

                        UpdateMediaSessionText("Ready to Listen", "Waiting for command...");

                        _ = PlayStopBeepAsync();

                        var audioManager = (global::Android.Media.AudioManager?)GetSystemService(AudioService);
                        if (Microsoft.Maui.Storage.Preferences.Get("use_bt_mic", false) && audioManager != null)
                        {
                            TeardownBluetoothAudio(audioManager);
                        }

                        HideFloatingUI();
                        OnCarListeningStopped?.Invoke();

                        if (_musicWasPlayingBeforeListen && _mediaSession != null)
                        {
#pragma warning disable CA1422
                            audioManager?.AbandonAudioFocus(null);
                            audioManager?.RequestAudioFocus(null, global::Android.Media.Stream.Music, global::Android.Media.AudioFocus.Gain);
#pragma warning restore CA1422
                            _musicWasPlayingBeforeListen = false;
                            Log("[AUDIO] Focus abandoned → Spotify resumes. AudioFocus.Gain re-acquired.");
                        }
                    }
                };
            }

            var waveContainer = _floatingView.FindViewById<LinearLayout>(Resource.Id.wave_container);
            if (waveContainer != null)
            {
                waveContainer.RemoveAllViews();
                _sineWaveView = new SineWaveView(contextToUse)
                {
                    LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent)
                };
                waveContainer.AddView(_sineWaveView);
            }

            openBtn?.Click += (s, e) => { OpenMainActivity(); HideFloatingUI(); };
            closeBtn?.Click += (s, e) => { HideFloatingUI(); };

            var overlayType = Build.VERSION.SdkInt >= BuildVersionCodes.O
                ? WindowManagerTypes.ApplicationOverlay
                : WindowManagerTypes.Phone;

            _layoutParams = new WindowManagerLayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.WrapContent,
                overlayType,
                WindowManagerFlags.NotFocusable |
                WindowManagerFlags.LayoutInScreen |
                WindowManagerFlags.NotTouchModal |
                WindowManagerFlags.LayoutNoLimits |
                WindowManagerFlags.ShowWhenLocked |
                WindowManagerFlags.TurnScreenOn,
                Format.Translucent
            );
            _layoutParams.Gravity = GravityFlags.Bottom | GravityFlags.CenterHorizontal;

            try
            {
                _windowManager.AddView(_floatingView, _layoutParams);
                _floatingView.Visibility = ViewStates.Gone;
                _floatingView.Alpha = 0f;
            }
            catch (Exception ex) { Log("AddView EXCEPTION: " + ex.Message); }
        }

        private async Task StartContinuousTranscription()
        {
            _isListening = true;
            _ = StartWaveAnimation();

            HO_Mind_AI.MainPage.OnTranscriptionSessionStarted?.Invoke();
            HO_Mind_AI.MainPage.ApplyCustomVolume();

            while (_isTranscriptionRunning)
            {
                try
                {
                    string partial = await CaptureVoiceAsync();

                    if (!string.IsNullOrWhiteSpace(partial))
                    {
                        Log($"Transcription segment: {partial}");
                        HO_Mind_AI.MainPage.OnTranscriptionChunkReceived?.Invoke(partial);
                    }
                }
                catch (TaskCanceledException) { }
                catch (Exception ex)
                {
                    Log("STT Engine Timeout/Error: " + ex.Message);
                    await Task.Delay(300);
                }
            }

            HO_Mind_AI.MainPage.RestoreCustomVolume();
            StopWaveAnimation();
        }

        private void ForceWakeUpScreen()
        {
            try
            {
                var pm = (PowerManager?)GetSystemService(PowerService);
                if (pm != null && !pm.IsInteractive)
                {
#pragma warning disable CA1422 
                    var wakeLock = pm.NewWakeLock(WakeLockFlags.ScreenDim | WakeLockFlags.AcquireCausesWakeup, "HOApp:WakeLock");
#pragma warning restore CA1422
                    wakeLock?.Acquire((long)TimeSpan.FromSeconds(5).TotalMilliseconds);
                }
            }
            catch { }
        }

        private async Task StartWaveAnimation()
        {
            if (_sineWaveView == null) return;
            _waveCts = new CancellationTokenSource();
            float t = 0;
            while (!_waveCts.IsCancellationRequested)
            {
                _sineWaveView.Phase += 0.3f;
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                bool isSpeaking = (now - _lastSpeechDetectedMs) < 800;
                float targetAmplitude = isSpeaking ? (40f * Resources!.DisplayMetrics!.Density) : 2f;
                _sineWaveView.Amplitude += (targetAmplitude - _sineWaveView.Amplitude) * 0.15f;
                Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() => _sineWaveView.Invalidate());
                await Task.Delay(30);
            }
        }

        private void StopWaveAnimation()
        {
            _waveCts?.Cancel();
            _waveCts = null;
        }

        private void ShowFloatingUI()
        {
            if (_floatingView == null || _windowManager == null || _layoutParams == null) return;

            bool currentBtState = Microsoft.Maui.Storage.Preferences.Get("use_bt_mic", false);
            UpdateBtToggleButtonUI(currentBtState);

            ForceWakeUpScreen();
            _floatingView.Visibility = ViewStates.Visible;
            _floatingView.Alpha = 0f;
            _floatingView.RequestLayout();
            try { _windowManager.UpdateViewLayout(_floatingView, _layoutParams); } catch { }
            _floatingView.Animate().Alpha(1f).SetDuration(250).Start();
        }

        private void HideFloatingUI()
        {
            if (_floatingView == null || _windowManager == null || _layoutParams == null) return;
            _floatingView.Animate().Alpha(0f).SetDuration(200).WithEndAction(new Java.Lang.Runnable(() =>
            {
                _floatingView.Visibility = ViewStates.Gone;
                try { _windowManager.UpdateViewLayout(_floatingView, _layoutParams); } catch { }
            })).Start();
        }

        private void RegisterSensor()
        {
            _sensorManager = (SensorManager?)GetSystemService(SensorService);
            _linearAcceleration = _sensorManager?.GetDefaultSensor(SensorType.LinearAcceleration);
            _sensorManager?.RegisterListener(this, _linearAcceleration, SensorDelay.Ui);
            _gravitySensor = _sensorManager?.GetDefaultSensor(SensorType.Gravity);
            _sensorManager?.RegisterListener(this, _gravitySensor, SensorDelay.Ui);
            Log("Sensors Registered: LinAccel + Gravity (Upright Check)");
        }

        public void OnSensorChanged(SensorEvent? e)
        {
            if (e?.Sensor?.Type == SensorType.Gravity)
            {
                _currentGravityY = e.Values[1];
                return;
            }

            if (e?.Sensor?.Type != SensorType.LinearAcceleration) return;
            if (!Microsoft.Maui.Storage.Preferences.Get("use_shake_mic", false)) return;

            if (_currentGravityY < 2.0f)
            {
                return;
            }

            if (_isTranscriptionRunning) return;

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            float ax = e.Values[0];
            float ay = e.Values[1];
            float az = e.Values[2];

            if (Math.Abs(ax) > SHAKE_THRESHOLD_X && Math.Abs(ax) > Math.Abs(ay) && Math.Abs(ax) > Math.Abs(az))
            {
                if (nowMs - _lastShakeMs < MIN_SHAKE_GAP_MS) return;

                if (nowMs - _lastShakeMs > MAX_SHAKE_WINDOW_MS)
                {
                    _shakeCount = 1;
                    _lastShakeMs = nowMs;
                    Log($"[GESTURE] X-Shake 1/2 (Upright). Force: {ax:F2}");
                }
                else
                {
                    _shakeCount = 0;
                    _lastShakeMs = 0;

                    if (_isListening)
                    {
                        Log($"[GESTURE] X-Shake 2/2 (Upright). RESTARTING!");
                        _isRestarting = true;
                        _sttCts?.Cancel();
                    }
                    else
                    {
                        Log($"[GESTURE] X-Shake 2/2 (Upright). TRIGGERING!");
                        _ = TriggerListeningSequence();
                    }
                }
            }
        }

        public void OnAccuracyChanged(Sensor? sensor, SensorStatus accuracy) { }

        private async Task TriggerListeningSequence()
        {
            if (_isListening || _isTranscriptionRunning) return;
            _isListening = true;
            _lastSpeechDetectedMs = 0;

            var audioManager = (global::Android.Media.AudioManager?)GetSystemService(AudioService);

            if (!_isRestarting)
            {
                _musicWasPlayingBeforeListen = audioManager?.IsMusicActive ?? false;

                if (_musicWasPlayingBeforeListen && _mediaSession == null)
                {
#pragma warning disable CA1422
                    audioManager?.RequestAudioFocus(null, global::Android.Media.Stream.Music, global::Android.Media.AudioFocus.GainTransient);
#pragma warning restore CA1422
                    await Task.Delay(300);
                    Log("[AUDIO] Music was active — paused for assistant.");
                }
            }

            HO_Mind_AI.MainPage.OnVoiceSessionStarted?.Invoke();
            OnCarSttUpdated?.Invoke("Listening...");

            UpdateMediaSessionText("Listening...", "Microphone Active");

            Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_sttTextView != null) _sttTextView.Text = "Listening...";
                ShowFloatingUI();
            });

            _ = StartWaveAnimation();

            // [MODIFIED v4.35.0 | 2026-07-28] Purpose: Removed PlayStartBeepAsync() so only the default Android OS "bloop" plays.
            // await PlayStartBeepAsync();

            bool useBtMic = Microsoft.Maui.Storage.Preferences.Get("use_bt_mic", false);
            if (useBtMic && audioManager != null) { SetupBluetoothAudio(audioManager); await Task.Delay(800); }

            try
            {
                string spokenText = await CaptureVoiceAsync();

                if (!_isRestarting && !_isTranscriptionRunning && !string.IsNullOrWhiteSpace(spokenText))
                    HO_Mind_AI.MainPage.OnFloatingVoiceResult?.Invoke(spokenText);
            }
            catch { }
            finally
            {
                if (_isTranscriptionRunning)
                {
                    // Do nothing! Let StartContinuousTranscription take over.
                }
                else
                {
                    StopWaveAnimation();
                    await StopListeningAsync();

                    await PlayStopBeepAsync();

                    if (!_isRestarting)
                    {
                        if (useBtMic && audioManager != null) TeardownBluetoothAudio(audioManager);

                        if (_musicWasPlayingBeforeListen && _mediaSession == null)
                        {
#pragma warning disable CA1422
                            audioManager?.AbandonAudioFocus(null);
                            audioManager?.RequestAudioFocus(null, global::Android.Media.Stream.Music, global::Android.Media.AudioFocus.Gain);
#pragma warning restore CA1422
                            _musicWasPlayingBeforeListen = false;
                            Log("[AUDIO] Focus abandoned → Spotify resumes. AudioFocus.Gain re-acquired.");
                        }

                        Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() => HideFloatingUI());

                        OnCarListeningStopped?.Invoke();
                    }
                    else
                    {
                        _isRestarting = false;
                        _isListening = false;
                        _ = TriggerListeningSequence();
                    }
                }
            }
        }

        private async Task PlayStartBeepAsync()
        {
            try
            {
                Microsoft.Maui.Devices.HapticFeedback.Perform(Microsoft.Maui.Devices.HapticFeedbackType.Click);
                HO_Mind_AI.MainPage.ApplyCustomVolume();

                using var toneGen = new global::Android.Media.ToneGenerator(global::Android.Media.Stream.Music, 100);
                toneGen.StartTone(global::Android.Media.Tone.PropBeep, 40);
                await Task.Delay(60);
                toneGen.StartTone(global::Android.Media.Tone.PropBeep, 40);
                await Task.Delay(150);
            }
            catch { }
            finally
            {
                HO_Mind_AI.MainPage.RestoreCustomVolume();
            }
        }

        private async Task PlayStopBeepAsync()
        {
            try
            {
                HO_Mind_AI.MainPage.ApplyCustomVolume();
                using var toneGen = new global::Android.Media.ToneGenerator(global::Android.Media.Stream.Music, 100);
                toneGen.StartTone(global::Android.Media.Tone.PropAck, 60);
                await Task.Delay(100);
            }
            catch { }
            finally
            {
                HO_Mind_AI.MainPage.RestoreCustomVolume();
            }
        }

        private void SetupBluetoothAudio(global::Android.Media.AudioManager am)
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake)
            {
                var bt = am.AvailableCommunicationDevices.FirstOrDefault(d => d.Type == global::Android.Media.AudioDeviceType.BluetoothSco);
                if (bt != null) am.SetCommunicationDevice(bt);
            }
            else
            {
#pragma warning disable CA1422
                am.StartBluetoothSco(); am.BluetoothScoOn = true;
#pragma warning restore CA1422
            }
        }

        private void TeardownBluetoothAudio(global::Android.Media.AudioManager am)
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake) am.ClearCommunicationDevice();
            else
            {
#pragma warning disable CA1422
                am.StopBluetoothSco(); am.BluetoothScoOn = false;
#pragma warning restore CA1422
            }
        }

        private async Task<string> CaptureVoiceAsync()
        {
            var tcs = new TaskCompletionSource<string>();
            _sttCts = new CancellationTokenSource();
            string finalResult = "";

            PowerManager.WakeLock? wakeLock = null;
            try
            {
                var pm = (PowerManager?)GetSystemService(PowerService);
#pragma warning disable CA1422
                wakeLock = pm?.NewWakeLock(WakeLockFlags.Partial, "HOApp:VoiceCaptureLock");
#pragma warning restore CA1422
                wakeLock?.Acquire(15000);
            }
            catch { }

            if (!_isTranscriptionRunning)
            {
                _sttCts.CancelAfter(12000);
            }

            using var reg = _sttCts.Token.Register(() => tcs.TrySetCanceled());

            EventHandler<SpeechToTextRecognitionResultUpdatedEventArgs> onUpdated = (s, a) => {
                if (!_isTranscriptionRunning) _sttCts.CancelAfter(4000);

                _lastSpeechDetectedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                string currentText = a.RecognitionResult ?? "";
                OnCarSttUpdated?.Invoke(currentText);

                UpdateMediaSessionText(currentText, "You said:");

                Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() => {
                    if (_sttTextView != null) _sttTextView.Text = currentText;
                    finalResult = currentText;
                });
            };

            EventHandler<SpeechToTextRecognitionResultCompletedEventArgs> onCompleted = (s, a) => {
                string completedText = a.RecognitionResult?.Text ?? finalResult;
                UpdateMediaSessionText(completedText, "Final command:");
                tcs.TrySetResult(completedText);
            };

            try
            {
                SpeechToText.Default.RecognitionResultUpdated += onUpdated;
                SpeechToText.Default.RecognitionResultCompleted += onCompleted;
                string savedLang = Microsoft.Maui.Storage.Preferences.Get("voice_lang", "English (US)");
                string langCode = savedLang switch { "Hebrew (Israel)" => "he-IL", "French (France)" => "fr-FR", "Russian (Russia)" => "ru-RU", "Arabic (SA)" => "ar-SA", _ => "en-US" };

                await SpeechToText.Default.StartListenAsync(new SpeechToTextOptions { Culture = new CultureInfo(langCode), AutoStopSilenceTimeout = TimeSpan.FromSeconds(3) }, _sttCts.Token);
                return await tcs.Task;
            }
            catch (TaskCanceledException) { return finalResult; }
            finally
            {
                SpeechToText.Default.RecognitionResultUpdated -= onUpdated;
                SpeechToText.Default.RecognitionResultCompleted -= onCompleted;

                try
                {
                    if (wakeLock?.IsHeld == true) wakeLock.Release();
                }
                catch { }
            }
        }

        private async Task StopListeningAsync()
        {
            try { await SpeechToText.Default.StopListenAsync(CancellationToken.None); } catch { }
            if (_sttCts != null) { try { _sttCts.Cancel(); } catch { } _sttCts.Dispose(); _sttCts = null; }
            _isListening = false;
        }

        private void OpenMainActivity()
        {
            var intent = new Intent(this, typeof(MainActivity));
            intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
            StartActivity(intent);
        }

        public override void OnDestroy()
        {
            DisableMediaRouting();
            _sensorManager?.UnregisterListener(this);
            StopWaveAnimation();
            _isTranscriptionRunning = false;
            _ = StopListeningAsync();
            if (_floatingView != null && _windowManager != null) { try { _windowManager.RemoveView(_floatingView); } catch { } _floatingView = null; }
            base.OnDestroy();
        }

        public static void Log(string msg)
        {
            global::Android.Util.Log.Debug(TAG, msg);
            if (!Microsoft.Maui.Storage.Preferences.Get("enable_file_logging", false)) return;
            try
            {
                string logPath = Path.Combine(Microsoft.Maui.Storage.FileSystem.CacheDirectory, "FVS_Logs.txt");
                File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} | {msg}{Environment.NewLine}");
            }
            catch { }
        }

        public class SineWaveView : AView
        {
            private readonly APaint _paint;
            private readonly APath _path;
            public float Amplitude { get; set; } = 2f;
            public float Phase { get; set; } = 0f;

            public SineWaveView(Context context) : base(context)
            {
                _paint = new APaint { Color = AColor.ParseColor("#03a9f4"), StrokeWidth = 8f, AntiAlias = true };
                _paint.SetStyle(APaint.Style.Stroke);
                _path = new APath();
            }

            protected override void OnDraw(ACanvas? canvas)
            {
                base.OnDraw(canvas);
                if (canvas == null) return;
                int width = Width;
                float centerY = Height / 2f;
                _path.Reset();
                _path.MoveTo(0, centerY);
                float frequency = 1.0f * 2f * (float)Math.PI / width;
                for (float x = 0; x <= width; x += 10)
                {
                    float y = centerY + (float)(Math.Sin(x * frequency + Phase) * Amplitude) + (float)(Math.Sin(x * frequency * 1.5f + Phase * 1.2f) * (Amplitude * 0.4f));
                    _path.LineTo(x, y);
                }
                canvas.DrawPath(_path, _paint);
            }
        }
    }
}