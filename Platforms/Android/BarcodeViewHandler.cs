#if ANDROID
// [MODIFIED v1.52.0 | 2026-04-16] Purpose: Mapped the new CameraContext property.

using Microsoft.Maui.Handlers;
using System;

namespace HO_Mind_AI.Platforms.Android
{
    public class BarcodeViewHandler : ViewHandler<BarcodeView, NativeCameraXView>
    {
        public static PropertyMapper<BarcodeView, BarcodeViewHandler> Mapper = new(ViewMapper)
        {
            [nameof(BarcodeView.CameraMode)] = MapCameraMode,
            [nameof(BarcodeView.IsFrontCamera)] = MapIsFrontCamera,
            [nameof(BarcodeView.CameraContext)] = MapCameraContext, // [NEW]
        };

        public static CommandMapper<BarcodeView, BarcodeViewHandler> Commands = new(ViewCommandMapper)
        {
            [nameof(BarcodeView.TakePhoto)] = MapTakePhoto,
            [nameof(BarcodeView.SetFlash)] = MapSetFlash,
            [nameof(BarcodeView.SetZoom)] = MapSetZoom,
            [nameof(BarcodeView.SetUseAiBg)] = MapSetUseAiBg,
        };

        public BarcodeViewHandler() : base(Mapper, Commands) { }

        protected override NativeCameraXView CreatePlatformView()
        {
            var native = new NativeCameraXView(Context);
            native.BarcodeDetected += value => VirtualView.RaiseBarcodeDetected(value);
            native.PhotoCaptured += b64 => VirtualView.RaisePhotoCaptured(b64);
            return native;
        }

        private static void MapCameraMode(BarcodeViewHandler h, BarcodeView v) => h.PlatformView.SetMode(v.CameraMode);
        private static void MapIsFrontCamera(BarcodeViewHandler h, BarcodeView v) => h.PlatformView.SetIsFrontCamera(v.IsFrontCamera);
        private static void MapCameraContext(BarcodeViewHandler h, BarcodeView v) => h.PlatformView.SetCameraContext(v.CameraContext); // [NEW]
        private static void MapTakePhoto(BarcodeViewHandler h, BarcodeView v, object? _) => h.PlatformView.TakePhoto();
        private static void MapSetFlash(BarcodeViewHandler h, BarcodeView v, object? arg) => h.PlatformView.SetFlash(arg is bool b && b);
        private static void MapSetZoom(BarcodeViewHandler h, BarcodeView v, object? arg) => h.PlatformView.SetZoom(arg is float f ? f : 1f);
        private static void MapSetUseAiBg(BarcodeViewHandler h, BarcodeView v, object? arg) => h.PlatformView.SetUseAiBg(arg is bool b && b);
    }
}
#endif