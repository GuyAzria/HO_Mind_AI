// [MODIFIED v1.42.0 | 2026-04-07] Purpose: Registered the new native BarcodeViewHandler.
// [ADDED v2.9.0 | 2026-04-26] Purpose: Added .UseMauiCommunityToolkit() for Speech-to-Text capabilities.
using Microsoft.Maui.Hosting;
using Microsoft.Maui.Controls.Hosting;
using CommunityToolkit.Maui; // חובה עבור זיהוי הקול

namespace HO_Mind_AI
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .UseMauiCommunityToolkit() // [ADDED v2.9.0]
                .ConfigureMauiHandlers(handlers =>
                {
#if ANDROID
                    handlers.AddHandler<BarcodeView, HO_Mind_AI.Platforms.Android.BarcodeViewHandler>();
#endif
                })
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

            return builder.Build();
        }
    }
}