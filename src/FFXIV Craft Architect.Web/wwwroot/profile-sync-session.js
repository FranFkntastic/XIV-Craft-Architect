const protocolVersion = 1;

export function createProfileSyncSession(
    callback,
    hostUrl,
    accessKey,
    profileId,
    sinceRevision) {
    if (!callback || !hostUrl || !accessKey || !profileId) {
        throw new Error("A callback, host URL, profile key, and profile identity are required.");
    }

    const normalizedProfileId = profileId.trim().toLowerCase();
    const initialRevision = normalizeRevision(sinceRevision);
    const lockName = `craft-architect-profile-sync:${normalizedProfileId}`;
    const channelName = `${lockName}:v${protocolVersion}`;
    const state = {
        stopped: false,
        cursor: initialRevision,
        reconnectCount: 0,
        fetchController: null,
        lockController: new AbortController(),
        channel: typeof BroadcastChannel === "function"
            ? new BroadcastChannel(channelName)
            : null
    };

    if (state.channel) {
        state.channel.onmessage = event => {
            const message = event.data;
            if (!isRevisionMessage(message, normalizedProfileId)) {
                return;
            }
            void applyRevision(message.serverRevision, "follower");
        };
    }

    async function applyRevision(serverRevision, source) {
        if (state.stopped) {
            return state.cursor;
        }

        const revision = normalizeRevision(serverRevision);
        if (revision <= state.cursor) {
            return state.cursor;
        }

        const appliedRevision = normalizeRevision(await callback.invokeMethodAsync(
            "ReceiveProfileRevision",
            normalizedProfileId,
            revision,
            source));
        state.cursor = Math.max(state.cursor, appliedRevision);
        return state.cursor;
    }

    async function streamOnce() {
        state.fetchController = new AbortController();
        const streamUrl = new URL("profile-host/changes/stream", normalizeHostUrl(hostUrl));
        streamUrl.searchParams.set("sinceRevision", String(state.cursor));
        const response = await fetch(streamUrl, {
            method: "GET",
            headers: {
                "Accept": "text/event-stream",
                "X-Profile-Key": accessKey
            },
            cache: "no-store",
            credentials: "omit",
            signal: state.fetchController.signal
        });

        if (response.status === 401) {
            throw new ProfileStreamError("profile_key_rejected", "The profile stream authorization was rejected.", true);
        }
        if (response.status === 409) {
            throw new ProfileStreamError("revision_cursor_rejected", "The profile stream cursor was rejected.", true);
        }
        if (!response.ok || !response.body) {
            throw new ProfileStreamError(
                "profile_stream_unavailable",
                `The profile stream returned HTTP ${response.status}.`,
                false);
        }

        await callback.invokeMethodAsync(
            "ReceiveProfileStreamState",
            normalizedProfileId,
            "leader",
            true,
            null,
            state.reconnectCount);
        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = "";
        try {
            while (!state.stopped) {
                const read = await reader.read();
                if (read.done) {
                    break;
                }

                buffer += decoder.decode(read.value, { stream: true });
                buffer = buffer.replace(/\r\n/g, "\n");
                let boundary;
                while ((boundary = buffer.indexOf("\n\n")) >= 0) {
                    const frame = buffer.slice(0, boundary);
                    buffer = buffer.slice(boundary + 2);
                    const revision = parseRevisionFrame(frame);
                    if (revision === null || revision <= state.cursor) {
                        continue;
                    }

                    state.channel?.postMessage({
                        protocolVersion,
                        kind: "profile-revision",
                        profileId: normalizedProfileId,
                        serverRevision: revision
                    });
                    await applyRevision(revision, "leader");
                }
            }
        } finally {
            reader.releaseLock();
            state.fetchController = null;
        }
    }

    async function runLeader() {
        if (!navigator.locks?.request) {
            await callback.invokeMethodAsync(
                "ReceiveProfileStreamState",
                normalizedProfileId,
                "unsupported",
                false,
                "Web Locks are unavailable; bounded cursor polling remains active.",
                state.reconnectCount);
            return;
        }

        try {
            await navigator.locks.request(
                lockName,
                { mode: "exclusive", signal: state.lockController.signal },
                async () => {
                    while (!state.stopped) {
                        try {
                            await streamOnce();
                            state.reconnectCount = 0;
                        } catch (error) {
                            if (state.stopped || error?.name === "AbortError") {
                                break;
                            }

                            const failure = error instanceof ProfileStreamError
                                ? error
                                : new ProfileStreamError(
                                    "profile_stream_interrupted",
                                    error?.message ?? "The profile stream was interrupted.",
                                    false);
                            await callback.invokeMethodAsync(
                                "ReceiveProfileStreamState",
                                normalizedProfileId,
                                "leader",
                                false,
                                failure.message,
                                state.reconnectCount);
                            if (failure.fatal) {
                                break;
                            }
                        }

                        if (!state.stopped) {
                            state.reconnectCount++;
                            const delay = Math.min(15000, 500 * (2 ** Math.min(state.reconnectCount, 5)));
                            await wait(delay, state.lockController.signal);
                        }
                    }
                });
        } catch (error) {
            if (!state.stopped && error?.name !== "AbortError") {
                await callback.invokeMethodAsync(
                    "ReceiveProfileStreamState",
                    normalizedProfileId,
                    "follower",
                    false,
                    error?.message ?? "Profile stream leadership failed.",
                    state.reconnectCount);
            }
        }
    }

    function stop() {
        if (state.stopped) {
            return;
        }
        state.stopped = true;
        state.fetchController?.abort();
        state.lockController.abort();
        state.channel?.close();
    }

    const pageHide = () => stop();
    window.addEventListener("pagehide", pageHide, { once: true });
    void runLeader();

    return {
        stop() {
            window.removeEventListener("pagehide", pageHide);
            stop();
        }
    };
}

function parseRevisionFrame(frame) {
    if (!frame || frame.startsWith(":")) {
        return null;
    }

    let eventName = "message";
    let eventId = null;
    let data = "";
    for (const line of frame.split("\n")) {
        if (line.startsWith("event:")) {
            eventName = line.slice(6).trim();
        } else if (line.startsWith("id:")) {
            eventId = normalizeRevision(line.slice(3).trim());
        } else if (line.startsWith("data:")) {
            data += line.slice(5).trim();
        }
    }

    if (eventName !== "profile-revision" || eventId === 0 || !data) {
        return null;
    }

    const payload = JSON.parse(data);
    const dataRevision = normalizeRevision(payload?.serverRevision);
    return dataRevision === eventId ? dataRevision : null;
}

function isRevisionMessage(message, profileId) {
    return message?.protocolVersion === protocolVersion &&
        message?.kind === "profile-revision" &&
        message?.profileId === profileId &&
        normalizeRevision(message?.serverRevision) > 0;
}

function normalizeRevision(value) {
    const revision = Number(value);
    return Number.isSafeInteger(revision) && revision >= 0 ? revision : 0;
}

function normalizeHostUrl(value) {
    const url = new URL(value);
    url.search = "";
    url.hash = "";
    if (!url.pathname.endsWith("/")) {
        url.pathname += "/";
    }
    return url;
}

function wait(milliseconds, signal) {
    return new Promise((resolve, reject) => {
        const onAbort = () => {
            clearTimeout(timer);
            reject(new DOMException("Profile synchronization stopped.", "AbortError"));
        };
        const timer = setTimeout(() => {
            signal?.removeEventListener("abort", onAbort);
            resolve();
        }, milliseconds);
        signal?.addEventListener("abort", onAbort, { once: true });
    });
}

class ProfileStreamError extends Error {
    constructor(code, message, fatal) {
        super(message);
        this.code = code;
        this.fatal = fatal;
    }
}
