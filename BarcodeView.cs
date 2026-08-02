// [MODIFIED v1.42.2 | 2026-04-16] Purpose: Added CameraContext property to let the native hardware know if we are updating a thumbnail.
// [ADDED v1.42.0 | 2026-04-07] Purpose: Created a lightweight cross-platform MAUI Virtual View. This acts as the bridge that our MainPage.xaml will connect to without referencing any Android-specific code.

using Microsoft.Maui.Controls;
using Microsoft.Maui.ApplicationModel;
using System;

namespace HO_Mind_AI
{
    public enum CameraMode { Photo, Barcode }

    public class BarcodeView : View
    {
        public static readonly BindableProperty CameraModeProperty =
            BindableProperty.Create(nameof(CameraMode), typeof(CameraMode), typeof(BarcodeView), CameraMode.Photo);

        public CameraMode CameraMode
        {
            get => (CameraMode)GetValue(CameraModeProperty);
            set => SetValue(CameraModeProperty, value);
        }

        public static readonly BindableProperty IsFrontCameraProperty =
            BindableProperty.Create(nameof(IsFrontCamera), typeof(bool), typeof(BarcodeView), false);

        public bool IsFrontCamera
        {
            get => (bool)GetValue(IsFrontCameraProperty);
            set => SetValue(IsFrontCameraProperty, value);
        }

        // [NEW] Track the exact context string (e.g. 'update', 'chat', 'stylist_avatar')
        public static readonly BindableProperty CameraContextProperty =
            BindableProperty.Create(nameof(CameraContext), typeof(string), typeof(BarcodeView), "");

        public string CameraContext
        {
            get => (string)GetValue(CameraContextProperty);
            set => SetValue(CameraContextProperty, value);
        }

        public event Action<string>? BarcodeDetected;
        public event Action<string>? PhotoCaptured;

        internal void RaiseBarcodeDetected(string value) =>
            MainThread.BeginInvokeOnMainThread(() => BarcodeDetected?.Invoke(value));

        internal void RaisePhotoCaptured(string base64) =>
            MainThread.BeginInvokeOnMainThread(() => PhotoCaptured?.Invoke(base64));

        public void TakePhoto() => Handler?.Invoke(nameof(TakePhoto));
        public void SetFlash(bool on) => Handler?.Invoke(nameof(SetFlash), on);
        public void SetZoom(float factor) => Handler?.Invoke(nameof(SetZoom), factor);
        public void SetUseAiBg(bool useAiBg) => Handler?.Invoke(nameof(SetUseAiBg), useAiBg);
    }
}