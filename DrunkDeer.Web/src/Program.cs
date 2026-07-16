using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using DrunkDeer.Web;
using DrunkDeer.Web.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

builder.Services.AddMudServices();

// Built by hand rather than resolved, because it has to be in place before anything can log to
// it: the logging pipeline and the container are handed the same instance.
var diagnostics = new DiagnosticsLog();
builder.Services.AddSingleton(diagnostics);
builder.Logging.AddProvider(new DiagnosticsLoggerProvider(diagnostics));

// Debug for the SDK only. That level is where the session reports the things the diagnostics page
// exists to show — a dropped frame, a packet arriving out of turn — while the framework's own
// Debug output would bury them. Deliberately below Trace: the poll loop logs there every frame,
// hundreds of times a second.
builder.Logging.AddFilter("DrunkDeer", LogLevel.Debug);

// The session lives for the lifetime of the page, so these are singletons in WASM
// (one user, one tab, one keyboard). KeyboardService owns the async session and the
// connection; KeyboardStore is the UI-facing snapshot components bind to; SelectionStore
// carries the key selection the edit panels act on.
builder.Services.AddSingleton<KeyboardService>();
builder.Services.AddSingleton<KeyboardStore>();
builder.Services.AddSingleton<SelectionStore>();

// Watches the other two for colour changes and rebuilds the palette, so it has to outlive any one
// component. Nothing else reads it: the theme provider is its only consumer.
builder.Services.AddSingleton<ThemeService>();

// Holds no keyboard state of its own — it reads and writes localStorage — but stays a singleton
// so the interop module is imported once rather than per panel render.
builder.Services.AddSingleton<ProfileLibrary>();
builder.Services.AddSingleton<BrowserStorage>();

// One copy of the user's preferences for the whole app, so a setting changed on the settings page
// is in force everywhere without anything having to be told.
builder.Services.AddSingleton<SettingsService>();

// Which saved profile the board is wearing. Singleton because it is a fact about the one keyboard,
// not about whichever panel happens to be on screen — the Profiles panel is disposed on every tab
// switch, and the board goes on wearing the profile regardless.
builder.Services.AddSingleton<ActiveProfile>();

// Saves the board's settings as they change, so there is something for the startup action to put
// back. It works by subscribing rather than by being called, so it has to be resolved below to
// exist at all.
builder.Services.AddSingleton<SessionRestore>();

// Fetches the shared catalogue from the themes repository once and holds it, rather than per card.
// Its own HttpClient, not the one above: that one is pointed at the app's own origin, and the
// gallery is the only thing here that talks to somebody else's.
builder.Services.AddSingleton(sp => new ThemeGallery(
    sp.GetRequiredService<ProfileLibrary>(),
    new HttpClient(),
    sp.GetRequiredService<ILogger<ThemeGallery>>()));

// A preview is a change to the one physical keyboard with a timer on it, so it has to outlive the
// card that started it — navigating away mid-preview must still put the board back.
builder.Services.AddSingleton<ThemePreview>();

var host = builder.Build();

// Before the first render, so that a component reading a setting mid-render sees the user's answer
// rather than the default — SettingsService.Current is synchronous precisely because this ran.
await host.Services.GetRequiredService<SettingsService>().LoadAsync();

// Resolved for its constructor: it subscribes to the keyboard's changes, and nothing injects it
// until the connection it is meant to be watching has already happened.
host.Services.GetRequiredService<SessionRestore>();

await host.RunAsync();
