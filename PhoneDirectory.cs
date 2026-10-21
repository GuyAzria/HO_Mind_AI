// [ADDED v2026.10.21 | 2026-10-21] Purpose: The household's phones, read from
// Home Assistant, so a phone's device id is picked from a list instead of
// being copied by hand out of a desktop browser's address bar.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace HO_Mind_AI
{
    /// <summary>One phone the Home Assistant companion app registered.</summary>
    internal sealed class HaPhone
    {
        [JsonPropertyName("device_id")] public string DeviceId { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("manufacturer")] public string Manufacturer { get; set; } = "";
        [JsonPropertyName("owner")] public string Owner { get; set; } = "";
        [JsonPropertyName("is_me")] public bool IsMe { get; set; }
        [JsonPropertyName("can_notify")] public bool CanNotify { get; set; }

        /// <summary>What the person is called in the list: the owner, else the phone.</summary>
        public string PersonName => Owner.Length > 0 ? Owner : Name;

        /// <summary>
        /// One line in the picker, e.g. "Yulia · SM-S921B (samsung)" or
        /// "Guy · Guy's phone (Google Pixel 8)".
        ///
        /// A device nobody renamed in Home Assistant is called by its model code
        /// ("SM-S921B"), which tells a person nothing - so the owner leads, and
        /// the maker and model follow whenever the name does not already say them.
        /// </summary>
        public string Label
        {
            get
            {
                string device = Name.Length > 0 ? Name : Model;
                string maker = $"{Manufacturer} {(Name.Contains(Model, StringComparison.OrdinalIgnoreCase) ? "" : Model)}".Trim();
                if (maker.Length > 0) device += $" ({maker})";
                string who = Owner.Length > 0 ? $"{Owner} · {device}" : device;
                if (IsMe) who += " - you";
                if (!CanNotify) who += " - no notifications";
                return who;
            }
        }
    }

    /// <summary>
    /// GET /api/home_organizer/phones, added to the integration in v2026.10.21.
    ///
    /// WHY THE ADDRESS AND TOKEN ARE PASSED IN. Settings is where they are
    /// typed, and the search button sits on the same screen: a user setting
    /// the app up for the first time has not pressed Save yet. The values on
    /// screen are used first and the saved ones only when a field is empty.
    ///
    /// The list carries names and ids only. Every authenticated user of the
    /// Home Assistant sees every phone - a reminder from one person in the
    /// house to another needs the other's phone. Nothing here sends anything.
    /// </summary>
    internal static class PhoneDirectory
    {
        private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

        private sealed class Reply
        {
            [JsonPropertyName("phones")] public List<HaPhone>? Phones { get; set; }
        }

        /// <summary>
        /// The phones, or an error message meant for the user. Exactly one of
        /// the two is non-null.
        /// </summary>
        public static async Task<(List<HaPhone>? Phones, string? Error)> FetchAsync(string? url, string? token)
        {
            string baseUrl = (url ?? "").Trim().TrimEnd('/');
            if (baseUrl.Length == 0) baseUrl = HaCredentials.GetUrl();
            string bearer = (token ?? "").Trim();
            if (bearer.Length == 0) bearer = await HaCredentials.GetTokenAsync();
            if (baseUrl.Length == 0) return (null, "Enter the Home Assistant address first.");
            if (bearer.Length == 0) return (null, "Enter the access token first.");

            try
            {
                using var client = new HttpClient(HaCredentials.CreateHandler()) { Timeout = TimeSpan.FromSeconds(15) };
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                using var res = await client.GetAsync($"{baseUrl}/api/home_organizer/phones");
                if (res.StatusCode == HttpStatusCode.NotFound)
                    return (null, "This needs a newer Home Organizer. Update it in HACS, restart Home Assistant and try again. You can still paste the ID by hand.");
                if (res.StatusCode == HttpStatusCode.Unauthorized || res.StatusCode == HttpStatusCode.Forbidden)
                    return (null, "Home Assistant refused the access token. Check it and try again.");
                if (!res.IsSuccessStatusCode)
                    return (null, $"Home Assistant answered {(int)res.StatusCode}. Try again in a moment.");

                var reply = JsonSerializer.Deserialize<Reply>(await res.Content.ReadAsStringAsync(), Json);
                var phones = reply?.Phones ?? new List<HaPhone>();
                if (phones.Count == 0)
                    return (null, "No phones found. Install the Home Assistant app on the phone and sign in to this Home Assistant.");
                return (phones, null);
            }
            catch (TaskCanceledException)
            {
                return (null, "Home Assistant did not answer. Check the address and the network.");
            }
            catch (HttpRequestException)
            {
                return (null, "Could not reach Home Assistant. Check the address and the network.");
            }
            catch (JsonException)
            {
                return (null, "Home Assistant sent an answer this app cannot read.");
            }
        }
    }
}
