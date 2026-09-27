// [MODIFIED v2026.9.24 | 2026-09-24] Purpose: Nine comments translated to English (RULE 17). No behaviour change.
// [ADDED v4.5.0 | 2026-05-02] Purpose: Merged the missing CarAppService (gateway) with the existing brilliant voice-activated HOMainScreen logic.
using Android.App;
using Android.Content;
using AndroidX.Car.App;
using AndroidX.Car.App.Model;
using AndroidX.Car.App.Validation;
using System;
using Action = AndroidX.Car.App.Model.Action;

namespace HO_Mind_AI.Platforms.Android
{
    // An explicit Name, so MAUI does not mangle it at compile time.
    [Service(Exported = true, Name = "com.hocamera.app.HOCarAppService")]

    // Category POI, so it matches car API level 1.
    [IntentFilter(new[] { "androidx.car.app.CarAppService" }, Categories = new[] { "androidx.car.app.category.POI" })]
    public class HOCarAppService : CarAppService
    {
        public override HostValidator CreateHostValidator()
        {
            return HostValidator.AllowAllHostsValidator;
        }

        public override Session OnCreateSession()
        {
            return new HOCarSession();
        }
    }

    // ==========================================
    // 2. CAR SESSION (unchanged).
    // ==========================================
    public class HOCarSession : Session
    {
        public override Screen OnCreateScreen(Intent intent)
        {
            return new HOMainScreen(CarContext);
        }
    }

    // ==========================================
    // 3. CAR SCREEN (unchanged - opens the microphone immediately).
    // ==========================================
    public class HOMainScreen : Screen
    {
        private string _currentText = "Listening...";
        private bool _isClosing = false;

        public HOMainScreen(CarContext carContext) : base(carContext)
        {
            // 1. Live text update.
            FloatingVoiceService.OnCarSttUpdated = (text) =>
            {
                _currentText = string.IsNullOrWhiteSpace(text) ? "Listening..." : text;
                // Uses the thread-safe MAUI UI mechanism.
                Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() => Invalidate());
            };

            // 2. Listens for the end of recording, to close the car screen.
            FloatingVoiceService.OnCarListeningStopped = () =>
            {
                if (!_isClosing)
                {
                    _isClosing = true;
                    // Back to the previous car screen (Waze, for example).
                    Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() => CarContext.FinishCarApp());
                }
            };

            // 3. Starts the microphone automatically.
            var intent = new Intent(CarContext, typeof(FloatingVoiceService));
            intent.SetAction("START_LISTENING");

            if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O)
                CarContext.StartForegroundService(intent);
            else
                CarContext.StartService(intent);
        }

        // [MODIFIED v4.1.1] In C# bindings, Template is ITemplate
        public override ITemplate OnGetTemplate()
        {
            return new MessageTemplate.Builder(_currentText)
                .SetTitle("HO Voice Assistant")
                .SetHeaderAction(Action.AppIcon)
                .Build();
        }
    }
}