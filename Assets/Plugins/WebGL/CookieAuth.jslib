mergeInto(LibraryManager.library, {
    // Patch XMLHttpRequest so requests to the Arrow Thing API carry cookies
    // (arrow_access, arrow_refresh) as credentials. UnityWebRequest on WebGL
    // routes through browser XHR but never sets withCredentials; without this
    // the browser strips our HttpOnly session cookies on cross-origin calls.
    //
    // Called once at ApiClient construction with the API origin (e.g.
    // "https://api.arrow-thing.com" in prod, "http://localhost:5000" in dev).
    // The patch is scoped by origin prefix so third-party XHRs (if any) stay
    // credential-less.
    EnableCredentialsForApi: function(apiOriginPtr) {
        if (window._arrowThingCredentialsPatched) return;
        window._arrowThingCredentialsPatched = true;

        var apiOrigin = UTF8ToString(apiOriginPtr);
        var origOpen = XMLHttpRequest.prototype.open;
        XMLHttpRequest.prototype.open = function(method, url) {
            origOpen.apply(this, arguments);
            if (typeof url === 'string' && url.indexOf(apiOrigin) === 0) {
                this.withCredentials = true;
            }
        };
    }
});
