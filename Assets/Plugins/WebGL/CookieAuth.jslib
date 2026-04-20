mergeInto(LibraryManager.library, {
    // Patch XMLHttpRequest + fetch so requests to the Arrow Thing API carry
    // cookies (arrow_access, arrow_refresh) as credentials. Unity 6 WebGL
    // may route UnityWebRequest through either XHR or the Fetch API depending
    // on build settings / emscripten version, so we cover both. Without this
    // the browser strips our HttpOnly session cookies on cross-origin calls.
    //
    // Called once at ApiClient construction with the API origin resolved
    // by ApiUrl_Resolve (prod, staging, or localhost depending on host).
    // The patch is scoped by origin prefix so third-party XHRs (if any) stay
    // credential-less.
    EnableCredentialsForApi: function(apiOriginPtr) {
        if (window._arrowThingCredentialsPatched) return;
        window._arrowThingCredentialsPatched = true;

        var apiOrigin = UTF8ToString(apiOriginPtr);
        console.log('[CookieAuth] Patching XHR+fetch for credentials on ' + apiOrigin);

        var matches = function(url) {
            return typeof url === 'string' && url.indexOf(apiOrigin) === 0;
        };

        // -- XHR path -------------------------------------------------------
        var origOpen = XMLHttpRequest.prototype.open;
        XMLHttpRequest.prototype.open = function(method, url) {
            origOpen.apply(this, arguments);
            if (matches(url)) {
                this.withCredentials = true;
            }
        };

        // -- Fetch path -----------------------------------------------------
        // Wrap fetch so Request objects and raw URLs both pick up
        // credentials: 'include'. Avoids clobbering callers that already
        // set an explicit credentials mode.
        if (typeof window.fetch === 'function') {
            var origFetch = window.fetch.bind(window);
            window.fetch = function(input, init) {
                var url = (typeof input === 'string')
                    ? input
                    : (input && input.url) || '';
                if (matches(url)) {
                    init = init || {};
                    if (!init.credentials) init.credentials = 'include';
                    // If input is a Request object with its own credentials
                    // mode, rebuild it so our override actually takes effect.
                    if (typeof input !== 'string' && input && input.credentials !== 'include') {
                        input = new Request(input, { credentials: 'include' });
                    }
                }
                return origFetch(input, init);
            };
        }
    }
});
