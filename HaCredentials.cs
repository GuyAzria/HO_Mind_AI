// [ADDED v2026.9.24 | 2026-09-24] Purpose: One owner for the Home Assistant
// address, the long-lived token and the HttpClient that carries them. Created
// because the token was read and the client was built in two separate places,
// so a change to one never reached the other.

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.Maui.Storage;

namespace HO_Mind_AI
{
    /// <summary>
    /// The single source of truth for how this app talks to Home Assistant.
    ///
    /// WHY THE TOKEN IS NOT IN Preferences.
    /// A Home Assistant long-lived token is full control of the house. On
    /// Android, Preferences is SharedPreferences: a readable XML file that is
    /// also included in the platform backup set. SecureStorage puts it behind
    /// the device keystore instead. The move is one-way and automatic - see
    /// GetTokenAsync - so an existing install does not have to be re-entered.
    ///
    /// WHY THERE IS NO CERTIFICATE CALLBACK HERE.
    /// This app is used over plain HTTP across a VPN to the home network, which
    /// is a deliberate choice and still works: when the address is http:// no
    /// certificate is ever presented and no validation runs. What used to be
    /// here was a callback that returned true for ANY certificate, which did
    /// nothing for HTTP and, on any https address, silently accepted a forged
    /// certificate while this same client carried the token in a Bearer header.
    /// Cleartext stays permitted in network_security_config.xml; blanket trust
    /// of forged certificates does not.
    /// </summary>
    internal static class HaCredentials
    {
        private const string TokenKey = "ha_token";
        private const string UrlKey = "ha_url";

        /// <summary>The configured base address, with any trailing slash removed.</summary>
        public static string GetUrl() => (Preferences.Get(UrlKey, "") ?? "").Trim().TrimEnd('/');

        public static void SetUrl(string? url) => Preferences.Set(UrlKey, (url ?? "").Trim());

        /// <summary>
        /// The token, from secure storage. An install that still holds the old
        /// plaintext value is migrated here on the first read: the plaintext
        /// copy is removed ONLY after the secure write succeeded, so a device
        /// whose keystore is unavailable keeps working rather than losing it.
        /// </summary>
        public static async Task<string> GetTokenAsync()
        {
            try
            {
                string? secure = await SecureStorage.Default.GetAsync(TokenKey);
                if (!string.IsNullOrEmpty(secure)) return secure;
            }
            catch (Exception)
            {
                // Keystore unavailable on this device. Fall through to the
                // legacy value so the app still reaches Home Assistant.
            }

            string legacy = Preferences.Get(TokenKey, "") ?? "";
            if (string.IsNullOrEmpty(legacy)) return "";
            if (await TrySetTokenAsync(legacy)) Preferences.Remove(TokenKey);
            return legacy;
        }

        /// <summary>
        /// Stores the token. Returns false when the device secure storage
        /// refused it. It deliberately does NOT fall back to writing the token
        /// in plain text: the caller reports the failure instead.
        /// </summary>
        public static async Task<bool> TrySetTokenAsync(string? token)
        {
            try
            {
                if (string.IsNullOrEmpty(token))
                {
                    SecureStorage.Default.Remove(TokenKey);
                    Preferences.Remove(TokenKey);
                    return true;
                }
                await SecureStorage.Default.SetAsync(TokenKey, token);
                Preferences.Remove(TokenKey);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// The platform handler. AndroidMessageHandler is kept because it uses
        /// the Android network stack, which is what makes the VPN route work.
        /// </summary>
        public static HttpMessageHandler CreateHandler()
        {
#if ANDROID
            return new Xamarin.Android.Net.AndroidMessageHandler();
#else
            return new HttpClientHandler();
#endif
        }

        /// <summary>
        /// A client already carrying the Authorization header. Every call to
        /// Home Assistant goes through here, so the token is read in one place.
        /// </summary>
        public static async Task<HttpClient> CreateClientAsync(TimeSpan timeout)
        {
            var client = new HttpClient(CreateHandler()) { Timeout = timeout };
            string token = await GetTokenAsync();
            if (!string.IsNullOrEmpty(token))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            return client;
        }
    }
}
