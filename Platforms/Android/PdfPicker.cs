// [ADDED v2026.9.24 | 2026-09-24] Purpose: Opens the Android document picker
// filtered to PDF, remembers the folder the last file came from, and hands the
// bytes back. MAUI's FilePicker cannot set a starting folder, which is the one
// thing this had to do, so it drives ACTION_OPEN_DOCUMENT directly.

using System;
using System.IO;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Provider;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using AndroidUri = Android.Net.Uri;

namespace HO_Mind_AI.Platforms.Android
{
    /// <summary>
    /// The PDF file picker.
    ///
    /// WHY NOT MAUI FilePicker.
    /// FilePicker can filter by type but cannot pass EXTRA_INITIAL_URI, and
    /// opening in the folder the user used last is the whole point here.
    ///
    /// WHAT "REMEMBERS THE FOLDER" REALLY MEANS.
    /// EXTRA_INITIAL_URI is a HINT to whichever document provider the picker
    /// shows. Most honour it on Android 8 and above; some ignore it, and the
    /// system picker also keeps its own idea of the last place visited. So this
    /// works most of the time and cannot be guaranteed - that is the platform,
    /// not this code. Everything here is wrapped so that a provider that
    /// dislikes the hint still opens the picker rather than failing.
    ///
    /// The read permission is taken persistably, so the remembered location
    /// survives a restart instead of becoming a URI the app may no longer open.
    /// </summary>
    internal static class PdfPicker
    {
        /// <summary>Matched in MainActivity.OnActivityResult.</summary>
        public const int RequestCode = 7301;

        private const string LastFolderKey = "pdf_last_folder_uri";

        // The literal key rather than the DocumentsContract constant: the
        // binding name has moved between .NET Android versions and the string
        // has not.
        private const string ExtraInitialUri = "android.provider.extra.INITIAL_URI";

        private static TaskCompletionSource<AndroidUri?>? _pending;

        /// <summary>
        /// Shows the picker. Returns the chosen document, or null when the user
        /// backed out or no activity was available.
        /// </summary>
        public static Task<AndroidUri?> PickAsync()
        {
            Activity? activity = Platform.CurrentActivity;
            if (activity == null) return Task.FromResult<AndroidUri?>(null);

            // A picker left open from an earlier press is abandoned rather than
            // left waiting for ever on a result that will never be delivered.
            _pending?.TrySetResult(null);
            _pending = new TaskCompletionSource<AndroidUri?>();

            var intent = new Intent(Intent.ActionOpenDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("application/pdf");
            intent.AddFlags(ActivityFlags.GrantReadUriPermission);
            intent.AddFlags(ActivityFlags.GrantPersistableUriPermission);

            string last = Preferences.Get(LastFolderKey, "") ?? "";
            if (!string.IsNullOrEmpty(last))
            {
                try { intent.PutExtra(ExtraInitialUri, AndroidUri.Parse(last)); }
                catch (Exception) { /* a stale or unparsable hint is not worth failing over */ }
            }

            try
            {
                activity.StartActivityForResult(intent, RequestCode);
            }
            catch (Exception)
            {
                _pending.TrySetResult(null);
            }

            return _pending.Task;
        }

        /// <summary>Called by MainActivity when the picker returns.</summary>
        public static void Deliver(Result resultCode, Intent? data)
        {
            TaskCompletionSource<AndroidUri?>? tcs = _pending;
            _pending = null;
            if (tcs == null) return;

            AndroidUri? uri = data?.Data;
            if (resultCode != Result.Ok || uri == null)
            {
                tcs.TrySetResult(null);
                return;
            }

            try
            {
                Platform.CurrentActivity?.ContentResolver?.TakePersistableUriPermission(
                    uri, ActivityFlags.GrantReadUriPermission);
            }
            catch (Exception)
            {
                // Not every provider grants a persistable permission. The file
                // still reads now, which is all this call needs.
            }

            RememberFolderOf(uri);
            tcs.TrySetResult(uri);
        }

        /// <summary>
        /// Stores where to open next time. The document id of a picked file
        /// looks like "primary:Download/receipt.pdf", so trimming the last
        /// segment gives its folder. When that cannot be worked out, the
        /// document's own URI is stored instead - as a hint it usually still
        /// lands the picker in the right place.
        /// </summary>
        private static void RememberFolderOf(AndroidUri uri)
        {
            string hint = uri.ToString() ?? "";
            try
            {
                string docId = DocumentsContract.GetDocumentId(uri) ?? "";
                int cut = docId.LastIndexOf('/');
                if (cut > 0 && uri.Authority != null)
                {
                    string parentId = docId.Substring(0, cut);
                    AndroidUri? parent = DocumentsContract.BuildDocumentUri(uri.Authority, parentId);
                    if (parent != null) hint = parent.ToString() ?? hint;
                }
            }
            catch (Exception)
            {
                // Providers are free to use any document-id format. Falling
                // back to the file's own URI is better than storing nothing.
            }

            if (!string.IsNullOrEmpty(hint)) Preferences.Set(LastFolderKey, hint);
        }

        /// <summary>The document's bytes, or null when it cannot be opened.</summary>
        public static byte[]? ReadAllBytes(AndroidUri uri)
        {
            try
            {
                using Stream? input = Platform.CurrentActivity?.ContentResolver?.OpenInputStream(uri);
                if (input == null) return null;
                using var buffer = new MemoryStream();
                input.CopyTo(buffer);
                return buffer.ToArray();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
