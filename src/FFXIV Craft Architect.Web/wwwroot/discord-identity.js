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
        }
    };
})();
