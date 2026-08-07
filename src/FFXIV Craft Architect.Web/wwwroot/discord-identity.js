(() => {
    let authorizationWindow = null;

    window.CraftArchitectDiscordIdentity = {
        begin() {
            authorizationWindow = window.open(
                "about:blank",
                "craft-architect-discord-link");
            if (!authorizationWindow) return false;
            authorizationWindow.opener = null;
            return true;
        },

        navigate(url) {
            if (!authorizationWindow || authorizationWindow.closed) return false;
            authorizationWindow.location.replace(url);
            authorizationWindow = null;
            return true;
        },

        close() {
            if (authorizationWindow && !authorizationWindow.closed) {
                authorizationWindow.close();
            }
            authorizationWindow = null;
        },

        readSignInFragment() {
            const values = new URLSearchParams(window.location.hash.slice(1));
            if (values.has("signin")) {
                return { kind: "signin", value: values.get("signin") };
            }
            if (values.has("signin-error")) {
                return { kind: "error", value: values.get("signin-error") };
            }
            return null;
        },

        clearSignInFragment() {
            history.replaceState(null, "", window.location.pathname + window.location.search);
        }
    };
})();
