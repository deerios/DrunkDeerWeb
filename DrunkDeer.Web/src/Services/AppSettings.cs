namespace DrunkDeer.Web.Services;

/// <summary>What the app does to the keyboard when it connects at startup.</summary>
/// <remarks>
/// One choice rather than a set of switches, because these are three answers to the same question
/// and two of them cannot both happen: a profile and the last unsaved changes are each a whole
/// board's worth of settings, so running both would mean the second silently undoing the first.
/// </remarks>
public enum StartupAction
{
    /// <summary>Leave the board exactly as the firmware has it. The default.</summary>
    Nothing,

    /// <summary>Apply the profile named in <see cref="AppSettings.StartupProfile"/>.</summary>
    Profile,

    /// <summary>Apply the settings this app last had on the board, saved as they were made.</summary>
    LastChanges,
}

/// <summary>
/// The user's preferences, as stored in the browser. Everything here has a default that means "do
/// what the app did before there were settings", so a browser with nothing stored behaves as it did.
/// </summary>
/// <remarks>
/// A mutable class rather than a record because the settings page binds to it directly and hands a
/// finished copy back — see <see cref="Clone"/>.
/// </remarks>
public sealed class AppSettings
{
    /// <summary>
    /// Whether choosing a colour or a brightness writes it to the board straight away, instead of
    /// waiting for Apply.
    /// </summary>
    /// <remarks>
    /// Off by default. It only takes effect once a session has coloured the whole board once, which
    /// is the write that needs a decision from the user — see <c>LightingPanel</c>. The name is
    /// narrower than what it now covers: it is a storage key, and widening it would read as "off"
    /// to every browser that already has this turned on.
    /// </remarks>
    public bool LiveEditColors { get; set; }

    /// <summary>
    /// Whether the "this writes to the whole keyboard" confirmations are skipped, so that Apply
    /// applies.
    /// </summary>
    /// <remarks>
    /// Off by default. It covers only the dialogs that precede a write to the board, which a
    /// second Apply can undo; the ones guarding a saved profile you are about to overwrite or
    /// delete still ask, because nothing puts those back.
    /// </remarks>
    public bool SkipWriteConfirmations { get; set; }

    /// <summary>What to put on the board once it's connected. See <see cref="StartupAction"/>.</summary>
    public StartupAction Startup { get; set; } = StartupAction.Nothing;

    /// <summary>
    /// The profile <see cref="StartupAction.Profile"/> applies. Null when none has been chosen,
    /// which makes that action a no-op rather than an error.
    /// </summary>
    public string? StartupProfile { get; set; }

    /// <summary>
    /// Whether to silently reconnect to a keyboard this browser has already been given permission
    /// for, and go straight to the dashboard.
    /// </summary>
    /// <remarks>
    /// On by default, and safe to be: it can only ever open a device the user has already picked
    /// from Chrome's own permission prompt, and it puts nothing on the board by itself. With no
    /// permission granted, or no keyboard plugged in, it finds nothing and the Connect page stays
    /// put.
    /// </remarks>
    public bool AutoConnect { get; set; } = true;

    /// <summary>
    /// The user's GitHub account name, which is how the gallery knows which themes are theirs.
    /// Null when they have not said, which is the default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a login and not proof of anything: the app has no GitHub account of its own and asking
    /// people to sign in to browse lighting themes would be a strange trade. It is matched against
    /// the <c>submittedBy</c> the catalogue records for each theme, and all that turns on is which
    /// themes appear under "My themes" with a Modify and an Unpublish button on them.
    /// </para>
    /// <para>
    /// So a name typed in here that is somebody else's buys nothing. The buttons only open a
    /// prefilled issue, and whether that issue is allowed to do anything is decided by the themes
    /// repository against the account that actually submits it. The worst a wrong name does is show
    /// its owner buttons that will be refused.
    /// </para>
    /// </remarks>
    public string? GitHubUsername { get; set; }

    /// <summary>A copy, so the settings page can edit one without the live one changing under the app.</summary>
    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
