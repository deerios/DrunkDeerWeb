// Drives the on-screen keyboard's hot path. Blazor renders the board structure once
// (one .key per KeyInfo); this module owns everything that changes at poll rate so the
// Blazor render tree is never touched per frame — see FUTURE_PLAN §5.4.
//
// Per frame we pull the live per-key travel from .NET (a float[] indexed by firmware
// slot) and patch only the CSS custom properties of keys whose depth changed. Layout
// (px-per-unit) is recomputed from the container width via a ResizeObserver, not per frame.
//
// This module also owns the pointer gestures that drive selection, and — importantly — it
// owns the `class` attribute of every .key. Blazor renders each key's class once and must
// never re-render it: the rAF loop sets `.pressed` out-of-band, so a Blazor re-render would
// diff against a tree that has never heard of `.pressed` and silently strip it. Selection
// therefore arrives here via setSelection() rather than through the render tree.

const boards = new WeakMap();

// A drag shorter than this (in px) is a click, not a marquee.
const DRAG_THRESHOLD = 4;

// Travel below this (in mm) is drawn as a key at rest. Hall-effect switches never report a clean
// zero — a resting key wanders by a few hundredths of a millimetre as the sensor picks up noise —
// and without a floor the whole board shimmers because every one of those wanders is a changed
// value the loop faithfully redraws. Small enough that a real press crosses it on the way down
// long before it reaches any usable actuation point.
const REST_DEADZONE_MM = 0.5;

// Mirrors DrunkDeer.Web.Services.SelectionMode.
const MODE_REPLACE = 'Replace';
const MODE_ADD = 'Add';
const MODE_TOGGLE = 'Toggle';

// The room around the board is part of the selection surface: a drag there rubber-bands the keys
// it sweeps, and a plain click there means "I'm done with these keys" and clears. Anything that
// reads the selection has to be exempt from both, or the click would empty the selection before the
// one it belongs to ever lands and the Apply buttons in the side panels would act on nothing. The
// panels opt out by marking themselves data-keeps-selection.
//
// MudBlazor's overlays are exempt by class rather than by attribute because they don't render
// inside the panel that opened them — popovers, dialogs and the colour picker's dropdown are all
// stamped at the body root, so a click in one is "away from the board" by DOM position while being
// the opposite in intent.
//
// The board itself is not listed: it is recognised by identity (the element this module was
// attached to) rather than by class, so a page showing both a live board and a static one — the
// gallery's thumbnails are .board too — can never confuse the two.
const KEEPS_SELECTION = [
    '[data-keeps-selection]',
    '.mud-popover',
    '.mud-overlay',
    '.mud-dialog',
    '.mud-snackbar',
].join(',');

function modeFor(ev) {
    if (ev.ctrlKey || ev.metaKey) return MODE_TOGGLE;
    if (ev.shiftKey) return MODE_ADD;
    return MODE_REPLACE;
}

// Attach the rAF loop + resize handling to a board element.
//   boardEl   the .board container Blazor rendered
//   dotNetRef DotNetObjectReference exposing SampleHeights() -> float[] (by slot) and
//             SelectSlots(). Null puts the board in static mode: a picture of a theme with no
//             live keyboard behind it (the gallery thumbnails), so there is nothing to sample and
//             nothing to select. Sizing still applies — that is the whole reason it attaches.
//   maxDepth  full-travel depth in mm (session.MaxDepthMm), maps depth -> 0..1
//   actDepth  fallback actuation depth in mm, used only for keys with no marker of their own;
//             normally a key reads as "pressed" past its own --act marker (see setActuation)
export function attach(boardEl, dotNetRef, maxDepth, actDepth) {
    detach(boardEl);

    // slot -> { el, last, rect } so the loop can skip keys that didn't move and gestures can
    // hit-test without touching the DOM. The rect is in KLE units (as authored in the
    // geometry), scaled by --u only at test time, so a resize needs no recompute.
    const keys = [];
    boardEl.querySelectorAll('.key[data-slot]').forEach(el => {
        const slot = parseInt(el.getAttribute('data-slot'), 10);
        const u = prop => parseFloat(el.style.getPropertyValue(prop)) || 0;
        keys[slot] = {
            el,
            last: -1,
            pressed: false,
            // Where this key's actuation marker sits, as a fraction of full travel — the same
            // number the CSS draws the line from, so the fill lights up exactly as it reaches it.
            // Blazor stamps it inline; setActuation keeps it current after that.
            act: u('--act'),
            // Primary rect only: the secondary leg of an ISO Enter is a small sliver, and
            // ignoring it costs nothing a user would notice when marquee-selecting.
            rect: { x: u('--kx'), y: u('--ky'), w: u('--kw'), h: u('--kh') },
        };
    });

    const state = { boardEl, dotNetRef, keys, maxDepth: maxDepth || 4, actDepth: actDepth || 1, rafId: 0, running: true };

    // Recompute px-per-unit from the rendered width and expose it as --u; the board's
    // height follows from its row count so the aspect ratio stays correct at any width.
    const cols = parseFloat(boardEl.style.getPropertyValue('--cols')) || 1;
    const rows = parseFloat(boardEl.style.getPropertyValue('--rows')) || 1;
    const relayout = () => {
        const u = boardEl.clientWidth / cols;
        boardEl.style.setProperty('--u', u + 'px');
        boardEl.style.height = (rows * u) + 'px';
    };
    const ro = new ResizeObserver(relayout);
    ro.observe(boardEl);
    relayout();
    state.ro = ro;

    const frame = () => {
        if (!state.running) return;
        let heights;
        try {
            heights = dotNetRef.invokeMethod('SampleHeights');
        } catch {
            // Session went away between frames (disconnect races the loop) — stop quietly.
            state.running = false;
            return;
        }
        if (heights) {
            const inv = 1 / state.maxDepth;
            for (let slot = 0; slot < heights.length; slot++) {
                const k = keys[slot];
                if (!k) continue;
                // Snapped to rest before the comparison, not after: the point is that a jittering
                // resting key produces the same value every frame and drops out here.
                const mm = heights[slot] < REST_DEADZONE_MM ? 0 : heights[slot];
                if (mm === k.last) continue;
                k.last = mm;
                const frac = mm <= 0 ? 0 : (mm >= state.maxDepth ? 1 : mm * inv);
                k.el.style.setProperty('--depth', frac.toFixed(3));
                k.el.style.setProperty('--glow', frac.toFixed(3));
                // Against the key's own marker, so the fill turns bright as its leading edge
                // reaches the line and not a moment before: the two are the same claim about the
                // same key, and a bar that lights up short of its own marker just looks broken.
                // Only a key with no marker falls back to the board-wide depth.
                const act = k.act > 0 ? k.act : state.actDepth * inv;
                const pressed = frac >= act;
                if (pressed !== k.pressed) {
                    k.pressed = pressed;
                    k.el.classList.toggle('pressed', pressed);
                }
            }
        }
        state.rafId = requestAnimationFrame(frame);
    };

    boards.set(boardEl, state);

    // A static board is done here: it has been measured, and the colours Blazor rendered inline
    // are the whole picture. Starting a rAF loop or a gesture handler for it would only burn a
    // frame budget per thumbnail on a gallery page that shows a dozen of them.
    if (!dotNetRef) return;

    attachSelection(state);
    state.rafId = requestAnimationFrame(frame);
}

// Pointer gestures: click a key to select it, drag to marquee-select, click empty space to clear.
// Shift adds, ctrl/cmd toggles. The gesture reports the hit slots to .NET, which owns the
// selection; the resulting classes come back via setSelection() so the store stays the single
// source of truth.
//
// The gesture is bound to the document rather than to the board, so a drag can start in the room
// around the board and sweep into it. That matters because a 75% layout is very nearly all keys —
// there is almost nowhere *on* the board to begin a drag that doesn't land on a key first — so the
// margins are where a user actually aims to rubber-band a row or a corner. Coordinates are in board
// units throughout and are simply negative out there, so nothing else has to know about it.
function attachSelection(state) {
    const { boardEl, keys } = state;

    const unitsAt = ev => {
        const u = parseFloat(boardEl.style.getPropertyValue('--u')) || 1;
        const box = boardEl.getBoundingClientRect();
        return { x: (ev.clientX - box.left) / u, y: (ev.clientY - box.top) / u };
    };

    const hitsIn = (a, b) => {
        const x1 = Math.min(a.x, b.x), x2 = Math.max(a.x, b.x);
        const y1 = Math.min(a.y, b.y), y2 = Math.max(a.y, b.y);
        const hits = [];
        keys.forEach((k, slot) => {
            const r = k.rect;
            if (r.x < x2 && r.x + r.w > x1 && r.y < y2 && r.y + r.h > y1) hits.push(slot);
        });
        return hits;
    };

    // Blazor renders the marquee (see the component) so that scoped CSS applies to it.
    const marquee = boardEl.querySelector('.marquee');
    if (!marquee) return;
    state.marquee = marquee;

    const drawMarquee = (a, b) => {
        const u = parseFloat(boardEl.style.getPropertyValue('--u')) || 1;
        marquee.style.left = (Math.min(a.x, b.x) * u) + 'px';
        marquee.style.top = (Math.min(a.y, b.y) * u) + 'px';
        marquee.style.width = (Math.abs(b.x - a.x) * u) + 'px';
        marquee.style.height = (Math.abs(b.y - a.y) * u) + 'px';
    };

    let drag = null;

    // The board sets user-select:none, but the room around it is ordinary prose. Without this a
    // drag that starts out there rubber-bands a text selection across the page alongside the keys.
    // Registered only for the life of a drag, so an ordinary click or double-click on that text
    // still selects a word the way it should.
    const blockSelect = ev => ev.preventDefault();

    const endDrag = () => {
        marquee.hidden = true;
        document.removeEventListener('selectstart', blockSelect);
        drag = null;
    };

    const onPointerDown = ev => {
        if (ev.button !== 0) return;
        // A press on a scrollbar arrives as a pointerdown on the element being scrolled, so without
        // this a scrollbar drag would rubber-band the board sitting behind it.
        if (ev.clientX >= document.documentElement.clientWidth ||
            ev.clientY >= document.documentElement.clientHeight) return;
        if (!boardEl.contains(ev.target) && ev.target.closest?.(KEEPS_SELECTION)) return;

        drag = { start: unitsAt(ev), moved: false, mode: modeFor(ev) };
        document.addEventListener('selectstart', blockSelect);
    };

    const onPointerMove = ev => {
        if (!drag) return;
        // The button came up somewhere we were never told about — over browser chrome, or another
        // window entirely. Drop the drag rather than carry a stale marquee around the page.
        if (!(ev.buttons & 1)) {
            endDrag();
            return;
        }
        const at = unitsAt(ev);
        const u = parseFloat(boardEl.style.getPropertyValue('--u')) || 1;
        if (!drag.moved) {
            const dx = (at.x - drag.start.x) * u, dy = (at.y - drag.start.y) * u;
            if (Math.hypot(dx, dy) < DRAG_THRESHOLD) return;
            drag.moved = true;
            marquee.hidden = false;
        }
        drawMarquee(drag.start, at);
    };

    const onPointerUp = ev => {
        if (!drag) return;
        const at = unitsAt(ev);
        const hits = drag.moved
            ? hitsIn(drag.start, at)
            : hitsIn(drag.start, drag.start); // a click is a zero-area marquee

        // A plain click that hit nothing means "none of them" and clears — whether it landed on the
        // bare board or in the room around it. A modified one leaves the selection alone: the user
        // is mid-refinement and has simply missed.
        const isEmptyClick = hits.length === 0 && !drag.moved;
        const mode = drag.mode;
        endDrag();
        if (isEmptyClick && mode !== MODE_REPLACE) return;

        state.dotNetRef.invokeMethodAsync('SelectSlots', hits, mode);
    };

    // Something took the gesture over — off the board that means the page is scrolling under a
    // touch drag, since only the board itself sets touch-action:none. The user asked to scroll, not
    // to select, so the half-drawn marquee is abandoned rather than committed.
    const onPointerCancel = () => endDrag();

    const onKeyDown = ev => {
        if (ev.key === 'Escape') {
            ev.preventDefault();
            state.dotNetRef.invokeMethodAsync('SelectSlots', [], MODE_REPLACE);
            return;
        }
        if (ev.key !== 'Enter' && ev.key !== ' ') return;
        const el = ev.target.closest?.('.key[data-slot]');
        if (!el || !boardEl.contains(el)) return;
        ev.preventDefault();
        const slot = parseInt(el.getAttribute('data-slot'), 10);
        state.dotNetRef.invokeMethodAsync('SelectSlots', [slot], modeFor(ev));
    };

    // On the document, not the board: the whole point is to hear about drags that never touch it.
    // No pointer capture — capturing retargets the compatibility mouse events too, which would
    // rob every button outside the board of its click.
    document.addEventListener('pointerdown', onPointerDown);
    document.addEventListener('pointermove', onPointerMove);
    document.addEventListener('pointerup', onPointerUp);
    document.addEventListener('pointercancel', onPointerCancel);
    boardEl.addEventListener('keydown', onKeyDown);
    state.detachSelection = () => {
        document.removeEventListener('pointerdown', onPointerDown);
        document.removeEventListener('pointermove', onPointerMove);
        document.removeEventListener('pointerup', onPointerUp);
        document.removeEventListener('pointercancel', onPointerCancel);
        document.removeEventListener('selectstart', blockSelect);
        boardEl.removeEventListener('keydown', onKeyDown);
    };
}

// Pushes the authoritative selection from .NET onto the DOM. Called on every selection
// change — rare enough that touching all keys is cheaper than tracking deltas.
export function setSelection(boardEl, slots) {
    const state = boards.get(boardEl);
    if (!state) return;
    const wanted = new Set(slots);
    state.keys.forEach((k, slot) => {
        const on = wanted.has(slot);
        if (k.selected === on) return;
        k.selected = on;
        k.el.classList.toggle('selected', on);
        k.el.setAttribute('aria-pressed', on ? 'true' : 'false');
    });
}

// Pushes the per-key backlight colours from .NET onto the DOM. Called after a lighting write
// (rare), so like setSelection it just touches every key rather than tracking deltas.
//   entries  [{ slot, kc, lc, lit }] — kc is the backlight colour driving the glow, lc the
//            (readable) legend colour, lit 0/1 for whether the key is backlit at rest.
export function setColors(boardEl, entries) {
    const state = boards.get(boardEl);
    if (!state) return;
    for (const { slot, kc, lc, lit } of entries) {
        const k = state.keys[slot];
        if (!k) continue;
        k.el.style.setProperty('--kc', kc);
        k.el.style.setProperty('--lc', lc);
        k.el.style.setProperty('--lit', lit);
    }
}

// Pushes the per-key actuation markers from .NET onto the DOM. Called on a lighting-style
// cadence — a write, a slider move, a selection change — never per frame.
//   entries  [{ slot, act, state }] — act is the marker position as a fraction of full travel,
//            state is 'unknown' | 'set' | 'preview', naming a --act-* colour the CSS defines.
export function setActuation(boardEl, entries) {
    const state = boards.get(boardEl);
    if (!state) return;
    for (const { slot, act, state: kind } of entries) {
        const k = state.keys[slot];
        if (!k) continue;
        k.el.style.setProperty('--act', act);
        k.el.style.setProperty('--actc', `var(--act-${kind})`);
        // The marker is also the point the fill lights up at, so moving it can change whether a
        // key that hasn't moved is pressed — dragging the slider under a held-down finger. Clearing
        // `last` makes the next frame re-decide rather than skip the key as unchanged.
        k.act = parseFloat(act) || 0;
        k.last = -1;
    }
}

export function detach(boardEl) {
    const state = boards.get(boardEl);
    if (!state) return;
    state.running = false;
    if (state.rafId) cancelAnimationFrame(state.rafId);
    if (state.ro) state.ro.disconnect();
    if (state.detachSelection) state.detachSelection();
    boards.delete(boardEl);
}
