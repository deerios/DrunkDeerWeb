// The WebHID transport's browser half. Everything HID-specific that can only be done in JS
// lives here; the protocol, the buffering, and the timeouts stay in C#
// (see Transport/WebHidKeyboardConnection.cs). This module holds no protocol knowledge beyond
// which report ID the command stream uses and how wide it has to be.
//
// WebHID is Chromium-only (Chrome/Edge/Opera) and needs a secure context (HTTPS or localhost).
// navigator.hid is absent otherwise; that's what isSupported() checks.

// Vendor-defined usage pages start here. The keyboard's *typing* interface (usage page 1,
// usage 6) is a protected collection Chrome refuses to open — we never want it anyway; the
// vendor collection is where the configuration protocol lives.
const VENDOR_USAGE_PAGE_MIN = 0xFF00;

// The command interface is the bidirectional one: report 4 must carry the protocol's payload in
// *both* directions. The read-only data interface (unsolicited travel packets) has no output
// reports at all, so requiring both directions also excludes it — this mirrors
// KeyboardDiscoverer.IsCommandInterface on the desktop side.
const COMMAND_REPORT_ID = 4;
const MIN_CAPACITY = 63;

const handles = new Map();
let nextHandle = 1;

export function isSupported() {
    return typeof navigator !== 'undefined'
        && 'hid' in navigator
        && window.isSecureContext === true;
}

// A report's payload size in bytes, summed from its descriptor items. WebHID never states this
// directly — unlike hidraw's GetMaxOutputReportLength — so it has to be computed. The report-ID
// byte is not one of these items, so this is already the payload capacity (63 on the A75).
function reportCapacity(reports, reportId) {
    const report = (reports ?? []).find(r => r.reportId === reportId);
    if (!report) return 0;
    let bits = 0;
    for (const item of report.items ?? []) {
        bits += (item.reportSize ?? 0) * (item.reportCount ?? 0);
    }
    return Math.floor(bits / 8);
}

function commandCapacity(device) {
    for (const collection of device.collections ?? []) {
        if ((collection.usagePage ?? 0) < VENDOR_USAGE_PAGE_MIN) continue;
        const out = reportCapacity(collection.outputReports, COMMAND_REPORT_ID);
        const inp = reportCapacity(collection.inputReports, COMMAND_REPORT_ID);
        if (out >= MIN_CAPACITY && inp >= MIN_CAPACITY) return out;
    }
    return 0;
}

// Chrome hands back one HIDDevice per HID interface of the granted physical device, so picking
// the right one is on us.
function pickCommandInterface(devices) {
    for (const device of devices) {
        const capacity = commandCapacity(device);
        if (capacity > 0) return { device, capacity };
    }
    return null;
}

// Prompts for a device and opens its command interface. Must be called from a user gesture —
// Chrome requires transient activation for requestDevice, which is why the caller keeps this
// module imported ahead of the click rather than importing it on the way in.
//
// Returns null when the user dismisses the picker without choosing (a normal outcome, not an
// error), and throws when a device was chosen but has no usable command interface.
export async function requestDevice(filters) {
    if (!isSupported()) throw new Error('WebHID is not available in this browser.');

    const devices = await navigator.hid.requestDevice({ filters: filters ?? [] });
    if (!devices || devices.length === 0) return null; // picker dismissed

    const picked = pickCommandInterface(devices);
    if (!picked) {
        throw new Error(
            'That device has no DrunkDeer command interface (a vendor collection whose report 4 ' +
            `carries at least ${MIN_CAPACITY} bytes each way). It may not be a configurable DrunkDeer keyboard.`);
    }

    return await open(picked);
}

// Opens a keyboard this origin has already been granted, without prompting and without a user
// gesture — getDevices only ever returns devices the user picked from Chrome's own prompt at some
// earlier point, so this can't reach anything they haven't already allowed.
//
// Returns null when there's nothing to open: no permission granted yet, nothing plugged in, or the
// granted device is gone. All three are ordinary, so none of them throws — the caller falls back to
// asking.
export async function openKnownDevice(filters) {
    if (!isSupported()) return null;

    const granted = await navigator.hid.getDevices();
    if (!granted || granted.length === 0) return null;

    // getDevices ignores the filters requestDevice takes, so the match is ours to make. Without it
    // an unrelated HID device this origin happens to have been granted could be opened as though it
    // were a keyboard.
    const pairs = filters ?? [];
    const matching = granted.filter(d => pairs.some(f => f.vendorId === d.vendorId && f.productId === d.productId));

    const picked = pickCommandInterface(matching);
    // A match with no command interface is not an error here, unlike in requestDevice: nobody chose
    // this device just now, so there is nobody to tell.
    return picked ? await open(picked) : null;
}

async function open({ device, capacity }) {
    if (!device.opened) await device.open();

    const handle = nextHandle++;
    handles.set(handle, { device, capacity, dotNetRef: null, onInputReport: null, onDisconnect: null });

    return {
        handle,
        capacity,
        productName: device.productName ?? '',
        vendorId: device.vendorId,
        productId: device.productId,
    };
}

function get(handle) {
    const state = handles.get(handle);
    if (!state) throw new Error(`No open WebHID device for handle ${handle}.`);
    return state;
}

// Starts forwarding input reports and disconnects to C#. Listening only starts here, once
// there's a connection object to receive them — nothing is sent to the keyboard before this
// point, so no report can be missed in the gap.
export function attach(handle, dotNetRef) {
    const state = get(handle);
    state.dotNetRef = dotNetRef;

    state.onInputReport = event => {
        // WebHID strips the report-ID byte for us — event.data starts at the protocol's byte 0,
        // which is exactly what the SDK expects after its own strip on the hidraw path. Handing
        // event.data over untouched is the whole adaptation; stripping again would eat 0xA0.
        const bytes = new Uint8Array(event.data.buffer, event.data.byteOffset, event.data.byteLength);
        dotNetRef.invokeMethodAsync('OnInputReport', bytes);
    };
    state.device.addEventListener('inputreport', state.onInputReport);

    state.onDisconnect = event => {
        if (event.device !== state.device) return;
        dotNetRef.invokeMethodAsync('OnDisconnected');
    };
    navigator.hid.addEventListener('disconnect', state.onDisconnect);
}

export async function send(handle, data) {
    const state = get(handle);
    // C# has already fitted the payload to `capacity` (the zero-padding-only truncation rule);
    // sendReport takes the report ID separately, so `data` is payload only — nothing to prepend.
    await state.device.sendReport(COMMAND_REPORT_ID, data);
}

export async function close(handle) {
    const state = handles.get(handle);
    if (!state) return;
    handles.delete(handle);

    if (state.onInputReport) state.device.removeEventListener('inputreport', state.onInputReport);
    if (state.onDisconnect) navigator.hid.removeEventListener('disconnect', state.onDisconnect);
    state.dotNetRef = null;

    // The device is often already gone by the time we get here (that's usually *why* we're
    // closing), and failing to close a gone device isn't worth surfacing.
    try { await state.device.close(); } catch { /* already disconnected */ }
}
