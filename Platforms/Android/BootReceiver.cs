// [MODIFIED v2.89.0 | 2026-05-07] Purpose: Added "from_boot" extra to the intent so MainActivity knows to hide itself immediately without interrupting the user.
// [MODIFIED v2.88.0 | 2026-05-07] Purpose: Changed boot logic to launch MainActivity instead of directly starting the TypeMicrophone foreground service. Android 12+ blocks background-started services from accessing the microphone. Launching the Activity first bypasses this restriction legally.
// [ADDED v2.65.0 | 2026-04-28] Purpose: Listens for OS boot completion to automatically start the FloatingVoiceService background service, making the assistant always available via Shake or BLE without manual app launch.

using Android.App;
using Android.Content;
using Android.OS;

namespace HO_Mind_AI.Platforms.Android
{
    // These attributes tell Android to register this receiver in the compiled manifest automatically
    [BroadcastReceiver(Enabled = true, Exported = true, DirectBootAware = true)]
    [IntentFilter(new[] {
        Intent.ActionBootCompleted,
        Intent.ActionLockedBootCompleted,
        "android.intent.action.QUICKBOOT_POWERON",
        "com.htc.intent.action.QUICKBOOT_POWERON"
    })]
    public class BootReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            string action = intent?.Action ?? "";

            if (action == Intent.ActionBootCompleted ||
                action == Intent.ActionLockedBootCompleted ||
                action.Contains("QUICKBOOT_POWERON"))
            {
                if (context == null) return;

                var mainIntent = new Intent(context, typeof(MainActivity));

                // Flags required to start an Activity from outside an Activity context
                mainIntent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);

                // [ADDED v2.89.0] Signal the MainActivity to hide itself immediately
                mainIntent.PutExtra("from_boot", true);

                try
                {
                    context.StartActivity(mainIntent);
                }
                catch (System.Exception ex)
                {
                    global::Android.Util.Log.Error("FVS_BOOT", "Failed to launch MainActivity on boot: " + ex.Message);
                }
            }
        }
    }
}