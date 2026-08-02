#if ANDROID
// [MODIFIED v1.58.0 | 2026-04-07] Purpose: Added the Xamarin.Google.MLKit.Vision.Barcode.Common namespace. The unbundled ML Kit package (v117) stores the core interfaces (IBarcodeScanner, Barcode, BarcodeScannerOptions) in the .Common namespace, which was causing the 11 missing type errors.
// [MODIFIED v1.57.0 | 2026-04-07] Purpose: Replaced direct C# casting with Android.Runtime.Extensions.JavaCast to correctly unwrap the Java.Util.IList from the MLKit success listener, fixing the silent scanner failure. Added try-catch blocks around the analyzer logic to prevent frame drops.

using Android.Runtime;
using AndroidX.Camera.Core;
using Java.Util.Concurrent.Atomic;
using System;
using Xamarin.Google.MLKit.Vision.Barcode;
// [ADDED v1.58.0 | 2026-04-07] Purpose: Include the Common namespace required by the unbundled ML Kit API architecture.
using Xamarin.Google.MLKit.Vision.Barcode.Common;
using Xamarin.Google.MLKit.Vision.BarCode;
using Xamarin.Google.MLKit.Vision.Common;
using Object = Java.Lang.Object;

namespace HO_Mind_AI.Platforms.Android
{
    internal class BarcodeAnalyzer : Object, ImageAnalysis.IAnalyzer
    {
        private readonly IBarcodeScanner _scanner;
        private readonly Action<string> _onDetected;

        private readonly AtomicBoolean _isProcessing = new AtomicBoolean(false);

        public BarcodeAnalyzer(Action<string> onDetected)
        {
            _onDetected = onDetected;

            var options = new BarcodeScannerOptions.Builder()
                .SetBarcodeFormats(
                    Barcode.FormatEan13,
                    Barcode.FormatUpcA,
                    Barcode.FormatEan8,
                    Barcode.FormatUpcE,
                    Barcode.FormatCode128,
                    Barcode.FormatCode39,
                    Barcode.FormatItf,
                    Barcode.FormatQrCode
                ).Build();

            _scanner = BarcodeScanning.GetClient(options);
        }

        public void Analyze(IImageProxy? imageProxy)
        {
            if (imageProxy == null) return;

            // Ensure we only process one frame at a time to prevent CPU choking
            if (!_isProcessing.CompareAndSet(false, true))
            {
                imageProxy.Close();
                return;
            }

            try
            {
                var mediaImage = imageProxy.Image;
                if (mediaImage == null)
                {
                    imageProxy.Close();
                    _isProcessing.Set(false);
                    return;
                }

                var inputImage = InputImage.FromMediaImage(mediaImage, imageProxy.ImageInfo.RotationDegrees);

                _scanner.Process(inputImage)
                    .AddOnSuccessListener(new OnSuccessListener<Object>(results =>
                    {
                        try
                        {
                            // MANDATORY: Use JavaCast to correctly unwrap the Java proxy object in MAUI
                            var list = results?.JavaCast<Java.Util.IList>();
                            if (list != null)
                            {
                                int size = list.Size();
                                for (int i = 0; i < size; i++)
                                {
                                    var barcode = list.Get(i)?.JavaCast<Barcode>();
                                    if (!string.IsNullOrWhiteSpace(barcode?.RawValue))
                                    {
                                        _onDetected(barcode.RawValue!);
                                        break;
                                    }
                                }
                            }
                        }
                        catch (Exception castEx)
                        {
                            global::Android.Util.Log.Error("MLKit", $"Cast Error: {castEx.Message}");
                        }
                    }))
                    .AddOnCompleteListener(new OnCompleteListener<Object>(_ =>
                    {
                        // Always close the proxy when finished to allow the next frame in
                        imageProxy.Close();
                        _isProcessing.Set(false);
                    }));
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error("MLKit", $"Analyze Error: {ex.Message}");
                imageProxy.Close();
                _isProcessing.Set(false);
            }
        }

        private class OnSuccessListener<T> : Object, global::Android.Gms.Tasks.IOnSuccessListener where T : Object
        {
            private readonly Action<Object> _action;
            public OnSuccessListener(Action<Object> action) => _action = action;
            public void OnSuccess(Object? result)
            {
                if (result != null) _action(result);
            }
        }

        private class OnCompleteListener<T> : Object, global::Android.Gms.Tasks.IOnCompleteListener where T : Object
        {
            private readonly Action<global::Android.Gms.Tasks.Task> _action;
            public OnCompleteListener(Action<global::Android.Gms.Tasks.Task> a) => _action = a;
            public void OnComplete(global::Android.Gms.Tasks.Task task) => _action(task);
        }
    }
}
#endif