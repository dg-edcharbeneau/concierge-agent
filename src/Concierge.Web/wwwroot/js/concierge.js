(() => {
    const sessionKey = "hotel-zgy-concierge";
    const themeKey = "hotel-zgy-theme";
    let transcriptListener;

    document.addEventListener("keydown", (event) => {
        const textarea = event.target;
        if (textarea?.id !== "message-draft"
            || event.key !== "Enter"
            || event.shiftKey
            || event.isComposing
            || event.keyCode === 229) {
            return;
        }

        const form = textarea.closest("form");
        const sendButton = form?.querySelector('button[type="submit"]');
        if (!sendButton || sendButton.disabled) return;

        event.preventDefault();
        form.requestSubmit(sendButton);
    });

    window.conciergeChat = {
        loadSession() {
            try {
                const stored = sessionStorage.getItem(sessionKey);
                return stored ? JSON.parse(stored) : null;
            } catch {
                return null;
            }
        },

        saveSession(state) {
            sessionStorage.setItem(sessionKey, JSON.stringify(state));
        },

        clearSession() {
            sessionStorage.removeItem(sessionKey);
        },

        loadTheme() {
            const theme = localStorage.getItem(themeKey);
            return theme === "light" || theme === "dark" ? theme : "system";
        },

        applyTheme(theme) {
            if (theme === "system") {
                document.documentElement.removeAttribute("data-theme");
                localStorage.setItem(themeKey, "system");
                return;
            }

            document.documentElement.dataset.theme = theme;
            localStorage.setItem(themeKey, theme);
        },

        isNearBottom(element) {
            return element.scrollHeight - element.scrollTop - element.clientHeight <= 80;
        },

        scrollToBottom(element) {
            element.scrollTop = element.scrollHeight;
        },

        unwireTranscript() {
            if (transcriptListener) {
                transcriptListener.element.removeEventListener("scroll", transcriptListener.handler);
                clearTimeout(transcriptListener.timeout);
                transcriptListener = null;
            }
        },

        wireTranscript(element, dotNetReference, version) {
            window.conciergeChat.unwireTranscript();

            const listener = { element, handler: null, timeout: null };
            const handler = () => {
                clearTimeout(listener.timeout);
                listener.timeout = setTimeout(() => {
                    const nearBottom = window.conciergeChat.isNearBottom(element);
                    dotNetReference.invokeMethodAsync("OnTranscriptScroll", nearBottom, version).catch(() => {});
                }, 80);
            };

            element.addEventListener("scroll", handler, { passive: true });
            listener.handler = handler;
            transcriptListener = listener;
        }
    };
})();
