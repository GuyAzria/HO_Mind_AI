// [MODIFIED v2026.9.24 | 2026-09-24] Purpose: Delivers the PDF picker result to
//   PdfPicker, and two comments translated to English (RULE 17).
// [ADDED v2.85.0 | 2026-05-07] Purpose: Added logic to detect if the app was launched from device boot, and if so, immediately move it to the background so the user is not interrupted.

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Maui.Controls;

namespace HO_Mind_AI
{
    // CRITICAL FIX: Changed Theme from "@style/Maui.SplashTheme" to "@style/TransparentTheme"
    [Activity(Theme = "@style/TransparentTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTask, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    [IntentFilter(new[] { Intent.ActionView },
                  Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
                  DataScheme = "hocamera",
                  DataHost = "capture")]
    public class MainActivity : MauiAppCompatActivity, ViewTreeObserver.IOnGlobalLayoutListener
    {
        private Android.Views.View? _rootView;
        private int _lastVisibleHeight = 0;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // Overlay permission check - required by the floating assistant.
            if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
            {
                if (!Android.Provider.Settings.CanDrawOverlays(this))
                {
                    var intent = new Intent(
                        Android.Provider.Settings.ActionManageOverlayPermission,
                        Android.Net.Uri.Parse("package:" + PackageName));

                    intent.AddFlags(ActivityFlags.NewTask);
                    StartActivity(intent);
                }
            }

            // Transparent window.
            if (Window != null)
            {
                Window.AddFlags(WindowManagerFlags.LayoutNoLimits);
                Window.AddFlags(WindowManagerFlags.NotTouchModal);
                Window.ClearFlags(WindowManagerFlags.DimBehind);

                Window.SetFormat(Android.Graphics.Format.Translucent);
                Window.SetBackgroundDrawable(
                    new Android.Graphics.Drawables.ColorDrawable(
                        Android.Graphics.Color.Transparent));
            }

            // [ADDED v2.84.0] Register ViewTreeObserver to detect keyboard open/close via root view height
            _rootView = Window?.DecorView?.FindViewById(Android.Resource.Id.Content);
            _rootView?.ViewTreeObserver?.AddOnGlobalLayoutListener(this);

            // [ADDED v2.85.0 | 2026-05-07] Check if launched silently from device boot
            bool isFromBoot = Intent?.GetBooleanExtra("from_boot", false) ?? false;
            if (isFromBoot)
            {
                MoveTaskToBack(true);
            }

            HandleIntent(Intent);
        }

        // [ADDED v2.84.0] Called by Android every time the layout changes — including when keyboard opens or closes
        public void OnGlobalLayout()
        {
            if (_rootView == null) return;

            Android.Graphics.Rect visibleRect = new Android.Graphics.Rect();
            _rootView.GetWindowVisibleDisplayFrame(visibleRect);
            int visibleHeight = visibleRect.Height();

            if (_lastVisibleHeight == 0)
            {
                _lastVisibleHeight = visibleHeight;
                return;
            }

            // Keyboard just closed: visible height increased back to normal
            if (visibleHeight > _lastVisibleHeight + 100)
            {
                Microsoft.Maui.Controls.Application.Current?.Dispatcher.Dispatch(() =>
                {
                    MainPage.ResetKeyboardMargin();
                });
            }

            _lastVisibleHeight = visibleHeight;
        }

        protected override void OnDestroy()
        {
            // [ADDED v2.84.0] Clean up the listener to avoid memory leaks
            _rootView?.ViewTreeObserver?.RemoveOnGlobalLayoutListener(this);
            base.OnDestroy();
        }

        protected override void OnNewIntent(Intent? intent)
        {
            base.OnNewIntent(intent);
            Intent = intent;

            // [ADDED v2.85.0 | 2026-05-07] Check if launched silently from device boot while app was in memory
            bool isFromBoot = intent?.GetBooleanExtra("from_boot", false) ?? false;
            if (isFromBoot)
            {
                MoveTaskToBack(true);
            }

            HandleIntent(intent);
        }

        // [ADDED v2026.9.24] The PDF picker is started with
        // StartActivityForResult because MAUI FilePicker cannot open in a
        // chosen folder. Its result comes back here and nowhere else.
        protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if (requestCode == HO_Mind_AI.Platforms.Android.PdfPicker.RequestCode)
            {
                HO_Mind_AI.Platforms.Android.PdfPicker.Deliver(resultCode, data);
            }
        }

        private void HandleIntent(Intent? intent)
        {
            if (intent?.Action == Intent.ActionView && intent.Data != null)
            {
                string contextVal = intent.Data.GetQueryParameter("context") ?? "chat";
                string mVal = intent.Data.GetQueryParameter("m") ?? "";
                string sVal = intent.Data.GetQueryParameter("s") ?? "";
                string bVal = intent.Data.GetQueryParameter("b") ?? "";

                var payloadDict = new Dictionary<string, string>
                {
                    { "ctx", contextVal },
                    { "m", mVal },
                    { "s", sVal },
                    { "b", bVal }
                };

                string jsonPayload = JsonSerializer.Serialize(payloadDict);

                if (HO_Mind_AI.MainPage.OnDeepLinkReceived != null)
                {
                    HO_Mind_AI.MainPage.OnDeepLinkReceived.Invoke(jsonPayload);
                }
            }
        }
    }
}