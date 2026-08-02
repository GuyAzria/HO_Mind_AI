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
    // הוספנו Name מפורש כדי למנוע מ-MAUI לעוות את השם בקימפול
    [Service(Exported = true, Name = "com.hocamera.app.HOCarAppService")]

    // שינינו את הקטגוריה ל-POI כדי שתתאים ל-API Level 1
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
    // 2. CAR SESSION (הקוד המקורי שלך - לא נגעתי)
    // ==========================================
    public class HOCarSession : Session
    {
        public override Screen OnCreateScreen(Intent intent)
        {
            return new HOMainScreen(CarContext);
        }
    }

    // ==========================================
    // 3. CAR SCREEN (הקוד המקורי שלך - פותח מיקרופון מיד)
    // ==========================================
    public class HOMainScreen : Screen
    {
        private string _currentText = "Listening...";
        private bool _isClosing = false;

        public HOMainScreen(CarContext carContext) : base(carContext)
        {
            // 1. עדכון בזמן אמת של הטקסט
            FloatingVoiceService.OnCarSttUpdated = (text) =>
            {
                _currentText = string.IsNullOrWhiteSpace(text) ? "Listening..." : text;
                // שימוש במנגנון ה-UI הבטוח של MAUI
                Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() => Invalidate());
            };

            // 2. האזנה לסיום הקלטה כדי לסגור את המסך של הרכב
            FloatingVoiceService.OnCarListeningStopped = () =>
            {
                if (!_isClosing)
                {
                    _isClosing = true;
                    // חזרה למסך הקודם ברכב (לדוגמה, Waze)
                    Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() => CarContext.FinishCarApp());
                }
            };

            // 3. הפעלת המיקרופון באופן אוטומטי
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