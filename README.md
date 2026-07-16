# DrunkDeer Web

A browser configurator for DrunkDeer analog HID keyboards. Blazor WebAssembly, talking to the board
over WebHID — nothing to install, and no driver or background service.

It does what the vendor's driver does, minus the driver: per-key actuation depth and rapid trigger,
per-key lighting and the built-in effects, profiles saved in the browser, a device panel that
describes the board rather than the last thing you typed, and a diagnostics page that shows the
actual packets going over the wire.

## Requirements

- A Chromium-based browser (Chrome, Edge, Opera). **WebHID is not implemented in Firefox or Safari**,
  and no amount of this app's code changes that — the pages load and the board cannot be reached.
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

This app is one of its front ends, alongside the `deerkb` CLI. Anything about how a keyboard is
actually spoken to — packets, models, capabilities — belongs over there, and arrives here by bumping
that version deliberately. What belongs here is the browser: the WebHID transport, the pages, and
the theme gallery.

The two repositories were one until the web app was split out, so the SDK's history carries this
app's early commits.

## Themes

The gallery reads its catalogue from [DrunkDeerThemes](https://github.com/deerios/DrunkDeerThemes),
a separate repository that anyone can submit to. Nothing it serves is trusted: both the catalogue
and each theme are validated here before being drawn, because the checks that gate a submission live
in another repository, in another language, and a repository can be wrong about its own rules.

## Hardware

Only the **A75** is verified against real hardware. Other models are implemented from the vendor's
web driver and are not confirmed.
