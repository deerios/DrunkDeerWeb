# DrunkDeer Web

A browser configurator for DrunkDeer analog HID keyboards over WebHID.

It does what the vendor's driver does, minus the driver: per-key actuation depth and rapid trigger,
per-key lighting and the built-in effects, profiles saved in the browser, a device panel that
describes the board rather than the last thing you typed, and a diagnostics page that shows the
actual packets going over the wire.

## Requirements

- A Chromium-based browser (Chrome, Edge, Opera). **WebHID is not implemented in Firefox or Safari**,
- An HTTPS origin, or `localhost`. WebHID is a powerful feature and browsers refuse it otherwise.
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build.

## Running it

```sh
dotnet run --project DrunkDeer.Web/src/DrunkDeer.Web.csproj
```

Then open the URL it prints and press Connect. The browser shows a device picker; the page cannot
see the keyboard until you choose it, which is the browser's decision and not something the app can
skip.

```sh
dotnet test DrunkDeerWeb.slnx      # the suite
```

## Its relationship to the SDK

The protocol lives in [DrunkDeerSDK](https://github.com/deerios/DrunkDeerSDK), referenced here as a
NuGet package:

```xml
<PackageReference Include="DrunkDeerSDK" Version="0.2.0" />
```

This app is one of its front ends, alongside the `deerkb` CLI.

## Themes

The gallery reads its catalogue from [DrunkDeerThemes](https://github.com/deerios/DrunkDeerThemes),
a separate repository that anyone can submit to.

## Hardware

Only the **A75** is verified against real hardware. Other models are implemented from the vendor's
web driver and are not confirmed. If you face any problems, open an issue or DM me on Discord: @afemaledeer
