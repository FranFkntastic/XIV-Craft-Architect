(() => {
    window.CraftArchitectDiscordIdentity = {
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
