// Named-profile storage and file export. The profiles themselves are JSON produced by the SDK's
// KeyboardProfile, so this layer only ever moves opaque strings around — it never parses one.

const PREFIX = "drunkdeer.profile.";

export function list() {
	const names = [];
	for (let i = 0; i < localStorage.length; i++) {
		const key = localStorage.key(i);
		if (key && key.startsWith(PREFIX)) names.push(key.substring(PREFIX.length));
	}
	// Sorted here rather than in C# so the list is stable no matter what order the browser
	// happens to hand back its keys.
	return names.sort((a, b) => a.localeCompare(b, undefined, { sensitivity: "base" }));
}

export function read(name) {
	return localStorage.getItem(PREFIX + name);
}

export function write(name, json) {
	// Quota is the one failure worth reporting: a full origin throws, and silently losing a
	// profile the user thinks they saved is worse than a message.
	try {
		localStorage.setItem(PREFIX + name, json);
		return null;
	} catch (e) {
		return e && e.name ? e.name : "unknown error";
	}
}

export function remove(name) {
	localStorage.removeItem(PREFIX + name);
}

export function download(filename, text) {
	const url = URL.createObjectURL(new Blob([text], { type: "application/json" }));
	const a = document.createElement("a");
	a.href = url;
	a.download = filename;
	document.body.appendChild(a);
	a.click();
	a.remove();
	// Revoking immediately can race the download in some browsers; a tick is enough.
	setTimeout(() => URL.revokeObjectURL(url), 1000);
}
