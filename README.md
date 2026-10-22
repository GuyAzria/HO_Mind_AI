# HO-Mind-AI by Guy Azria
An AI-powered personal assistant application for Home Assistant, featuring seamless voice interaction, barcode scanning, and smart home organization.

HO_Mind_AI is the Android companion app for **[Home Organizer (HO-AI)](https://github.com/GuyAzria/home-organizer)**. It talks to your Home Assistant directly - voice commands, receipt and barcode scanning with the camera - and works over plain `http://` on your home network or a VPN, where a browser would refuse the camera and microphone.

**Current version: 2026.10.22**

---

## Install

1. In the Home Organizer panel, open **Settings (⚙️) ▸ 📱 HO_Mind_AI ▸ Download HO_Mind_AI App** - or download the APK from **[Releases](https://github.com/GuyAzria/HO_Mind_AI/releases/latest)**.
2. Open the APK on the phone. Android asks once to allow installing from this source.
3. Updating? Install the new APK over the old one - your settings are kept.

**Requirements**
- Home Organizer installed in Home Assistant. **v2026.10.21 or newer** for the phone list in Settings; with an older one you can still type the device ID by hand.
- The official **Home Assistant app** on every phone a reminder should reach - that is how Home Assistant knows the phone.

---

## Set it up

Open the app, tap **⋮ ▸ HO Settings**.

1. **HA URL** - your Home Assistant address, exactly as you open it in a browser: `http://192.168.1.100:8123` at home or over a VPN, or `https://yourname.duckdns.org:8123` if Home Assistant has a valid certificate. Typed without `http://`, it is added for you. A self-signed certificate is not accepted.
2. **Access Token** - in Home Assistant, click your name (bottom left) ▸ **Security** ▸ **Long-lived access tokens** ▸ **Create token**. Copy it at once - it is shown only one time - and paste it here. Each person makes their own token under their own Home Assistant user, so the app knows whose phone it is on.
3. **Conversation Agent ID** - leave it as `conversation.ho_ai_agent`.
4. **People and phones** - tap the phone field: a list of every device with the Home Assistant app opens, by owner and name (*"Yulia · Galaxy S24 (samsung)"*). Pick yours. Tap **👪 Load household** to add everyone else's phone in one go, so a reminder can be sent to anyone in the house - *"remind Yulia to collect the kid in 10 minutes"*. Change each name to the way you say it; several names go in one field, separated by commas.
5. **Shake to Speak**, **Language**, **Volume Override** - as you like.
6. Tap **Save**.

---

## Troubleshooting

| What you see | What to do |
|---|---|
| **"HA API Error"** after you **reinstalled the app**, although the address, the token and the VPN all look right | **Disconnect and reconnect the VPN** on the phone. A reinstalled app is a new app to Android, and a VPN tunnel that was already open may not carry it until it is restarted. |
| "HA API Error: An invalid request URI was provided" | The address had no `http://`. From 2026.10.22 it is added automatically - update the app, or type it in full. |
| "Home Assistant refused the access token" | Create a new token (step 2) and paste it again. |
| The phone list says "This needs a newer Home Organizer" | Update Home Organizer in HACS and restart Home Assistant - or pick **Type the ID by hand…** at the bottom of the list. |
| A device shows a code like `SM-S921B` | Give it a name in Home Assistant: **Settings ▸ Devices ▸ the device ▸ ✏️**. |
| A device says "no notifications" | The Home Assistant app on that device is not set up to receive notifications - open it and check its notification settings. |

---

## Privacy and security

- The token is kept in the phone's secure storage (Android Keystore), never in a plain settings file, and Android backups do not copy the app's data.
- Plain `http://` is allowed on purpose: this app is meant for your home network or a VPN into it. Over `https://` the certificate is always checked.
- The phone list holds names and device IDs only. Every user of your Home Assistant sees the same list, so anyone in the house can send a reminder to anyone else.

---

## What's new

**2026.10.22**
- A Home Assistant address typed without `http://` works - it used to fail every request with "HA API Error".
- The phone list reports any problem instead of closing.

**2026.10.21**
- The phone is picked from a list instead of copying a 32-character device ID out of a desktop browser. **Load household** adds everyone's phone in one tap.
