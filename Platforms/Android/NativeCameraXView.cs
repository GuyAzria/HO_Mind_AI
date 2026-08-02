#if ANDROID
// [MODIFIED v1.63.0 | 2026-04-16] Purpose: Implemented aggressive native image scaling down to 512px strictly for the 'update' context to eliminate the 5-second 15MB file upload bottleneck on item image edits.

using Android.Content;
using Android.Graphics;
using Android.Media;
using Android.Widget;
using AndroidX.Camera.Core;
using AndroidX.Camera.Lifecycle;
using AndroidX.Camera.View;
using AndroidX.Core.Content;
using Java.Util.Concurrent;
using Microsoft.Maui.ApplicationModel;
using System;
using System.IO;
using Orientation = Android.Media.Orientation;

namespace HO_Mind_AI.Platforms.Android
{
    public class NativeCameraXView : FrameLayout
    {
        private readonly PreviewView _previewView;
        private ProcessCameraProvider? _provider;
        private Preview? _preview;
        private ImageAnalysis? _analysisUseCase;
        private ImageCapture? _captureUseCase;
        private ICamera? _camera;
        private readonly IExecutorService _analysisExecutor;

        private CameraMode _currentMode = CameraMode.Photo;
        private int _lensFacing = CameraSelector.LensFacingBack;

        private float _currentZoom = 0f;
        private bool _isFlashOn = false;

        private bool _useAiBg = true;

        // [NEW] Store the context
        private string _cameraContext = "";

        public event Action<string>? BarcodeDetected;
        public event Action<string>? PhotoCaptured;

        public NativeCameraXView(Context context) : base(context)
        {
            _previewView = new PreviewView(context);
            _previewView.LayoutParameters = new FrameLayout.LayoutParams(LayoutParams.MatchParent, LayoutParams.MatchParent);
            _previewView.SetImplementationMode(PreviewView.ImplementationMode.Compatible);
            AddView(_previewView);

            _analysisExecutor = Executors.NewSingleThreadExecutor()!;
            InitProvider();
        }

        private void InitProvider()
        {
            var future = ProcessCameraProvider.GetInstance(Context!);
            future.AddListener(new Java.Lang.Runnable(() =>
            {
                _provider = (ProcessCameraProvider)future.Get()!;
                BuildUseCases();
                BindCamera();
            }), ContextCompat.GetMainExecutor(Context!)!);
        }

        private void BuildUseCases()
        {
            _preview = new Preview.Builder().Build();
            _preview.SetSurfaceProvider(ContextCompat.GetMainExecutor(Context!), _previewView.SurfaceProvider);

            _analysisUseCase = new ImageAnalysis.Builder()
                .SetTargetResolution(new global::Android.Util.Size(1280, 720))
                .SetBackpressureStrategy(ImageAnalysis.StrategyKeepOnlyLatest)
                .SetOutputImageFormat(ImageAnalysis.OutputImageFormatYuv420888)
                .Build();

            _analysisUseCase.SetAnalyzer(_analysisExecutor, new BarcodeAnalyzer(barcode =>
            {
                if (_currentMode == CameraMode.Barcode) BarcodeDetected?.Invoke(barcode);
            }));

            _captureUseCase = new ImageCapture.Builder()
                .SetCaptureMode(ImageCapture.CaptureModeMinimizeLatency)
                .SetJpegQuality(85)
                .Build();
        }

        public void SetMode(CameraMode mode)
        {
            if (mode == _currentMode) return;
            _currentMode = mode;

            if (_provider != null)
            {
                MainThread.BeginInvokeOnMainThread(() => BindCamera());
            }
        }

        public void SetIsFrontCamera(bool isFront)
        {
            _lensFacing = isFront ? CameraSelector.LensFacingFront : CameraSelector.LensFacingBack;
            if (_provider != null)
            {
                MainThread.BeginInvokeOnMainThread(() => BindCamera());
            }
        }

        public void SetUseAiBg(bool useAiBg)
        {
            _useAiBg = useAiBg;
        }

        // [NEW] Let the hardware know the context
        public void SetCameraContext(string context)
        {
            _cameraContext = context;
        }

        private void BindCamera()
        {
            if (_provider == null || _preview == null) return;
            _provider.UnbindAll();

            var selector = new CameraSelector.Builder().RequireLensFacing(_lensFacing).Build();
            var lifecycleOwner = Platform.CurrentActivity as AndroidX.Lifecycle.ILifecycleOwner;

            if (lifecycleOwner == null) return;

            try
            {
                _camera = _currentMode == CameraMode.Barcode
                    ? _provider.BindToLifecycle(lifecycleOwner, selector!, _preview, _analysisUseCase!)
                    : _provider.BindToLifecycle(lifecycleOwner, selector!, _preview, _captureUseCase!);

                _camera?.CameraControl?.SetLinearZoom(_currentZoom);
                _camera?.CameraControl?.EnableTorch(_isFlashOn);
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error("CameraX", $"BindToLifecycle failed: {ex.Message}");
            }
        }

        public void TakePhoto()
        {
            if (_captureUseCase == null || _currentMode != CameraMode.Photo) return;

            var outputOptions = new ImageCapture.OutputFileOptions.Builder(
                new Java.IO.File(Context!.CacheDir!, $"capture_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.jpg")
            ).Build();

            // Pass _cameraContext into the callback
            _captureUseCase.TakePicture(outputOptions, ContextCompat.GetMainExecutor(Context!)!, new PhotoSavedCallback(PhotoCaptured, _useAiBg, _cameraContext));
        }

        public void SetFlash(bool on)
        {
            _isFlashOn = on;
            _camera?.CameraControl?.EnableTorch(_isFlashOn);
        }

        public void SetZoom(float linearZoom)
        {
            _currentZoom = Math.Clamp(linearZoom, 0f, 1f);
            _camera?.CameraControl?.SetLinearZoom(_currentZoom);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _analysisExecutor.Shutdown();
                _provider?.UnbindAll();
            }
            base.Dispose(disposing);
        }

        private class PhotoSavedCallback : Java.Lang.Object, ImageCapture.IOnImageSavedCallback
        {
            private readonly Action<string>? _onReady;
            private readonly bool _useAiBg;
            private readonly string _context;

            public PhotoSavedCallback(Action<string>? onReady, bool useAiBg, string context)
            {
                _onReady = onReady;
                _useAiBg = useAiBg;
                _context = context;
            }

            public void OnImageSaved(ImageCapture.OutputFileResults? output)
            {
                try
                {
                    var path = output?.SavedUri?.Path;
                    if (path == null) return;

                    var exif = new ExifInterface(path);
                    int orientation = exif.GetAttributeInt(ExifInterface.TagOrientation, (int)Orientation.Normal);
                    int rotationDegrees = 0;
                    switch (orientation)
                    {
                        case (int)Orientation.Rotate90: rotationDegrees = 90; break;
                        case (int)Orientation.Rotate180: rotationDegrees = 180; break;
                        case (int)Orientation.Rotate270: rotationDegrees = 270; break;
                    }

                    BitmapFactory.Options options = new BitmapFactory.Options
                    {
                        InMutable = true,
                        InPreferredConfig = Bitmap.Config.Argb8888
                    };
                    Bitmap rawBitmap = BitmapFactory.DecodeFile(path, options)!;

                    Matrix matrix = new Matrix();
                    if (rotationDegrees != 0)
                    {
                        matrix.PostRotate(rotationDegrees);
                    }

                    // [MODIFIED v1.63.0] Massively shrink image size if editing an item to allow instant network upload!
                    int maxDim = _context == "update" ? 512 : 1024;
                    float scale = Math.Min((float)maxDim / rawBitmap.Width, (float)maxDim / rawBitmap.Height);
                    if (scale < 1.0f)
                    {
                        matrix.PostScale(scale, scale);
                    }

                    Bitmap originalBitmap = Bitmap.CreateBitmap(rawBitmap, 0, 0, rawBitmap.Width, rawBitmap.Height, matrix, true);
                    if (originalBitmap != rawBitmap)
                    {
                        rawBitmap.Recycle();
                    }

                    string mimePrefix = "data:image/jpeg;base64,";
                    Bitmap.CompressFormat format = Bitmap.CompressFormat.Jpeg;

                    if (_useAiBg)
                    {
                        int width = originalBitmap.Width;
                        int height = originalBitmap.Height;
                        int[] pixels = new int[width * height];
                        originalBitmap.GetPixels(pixels, 0, width, 0, 0, width, height);

                        for (int i = 0; i < pixels.Length; i++)
                        {
                            int color = pixels[i];

                            int r = (color >> 16) & 0xFF;
                            int g = (color >> 8) & 0xFF;
                            int b = color & 0xFF;

                            if (r > 190 && g > 190 && b > 190)
                            {
                                pixels[i] = 0; // Pure Transparent
                            }
                        }
                        originalBitmap.SetPixels(pixels, 0, width, 0, 0, width, height);

                        mimePrefix = "data:image/png;base64,";
                        format = Bitmap.CompressFormat.Png;
                    }

                    using var stream = new MemoryStream();
                    originalBitmap.Compress(format, 85, stream);
                    byte[] imageBytes = stream.ToArray();
                    string base64String = Convert.ToBase64String(imageBytes);

                    System.IO.File.Delete(path); // Cleanup

                    _onReady?.Invoke($"{mimePrefix}{base64String}");
                }
                catch (Exception e)
                {
                    global::Android.Util.Log.Error("CameraX", $"Processing error: {e.Message}");
                }
            }

            public void OnError(ImageCaptureException? ex)
            {
                if (ex != null) global::Android.Util.Log.Error("CameraX", $"Photo capture failed: {ex.Message}");
            }
        }
    }
}
#endif