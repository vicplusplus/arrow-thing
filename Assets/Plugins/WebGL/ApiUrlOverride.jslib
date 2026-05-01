// Resolves the API base URL for WebGL builds. Returns:
//   - `?api=<url>` query param if present (full override, no trailing slash)
//   - `http://localhost:5000` when the page is served from localhost/127.0.0.1
//   - the page origin (e.g. `https://staging.arrow-thing.com`) when served
//     from the staging host — a Pages Function reverse-proxies /api/* to
//     api-staging.arrow-thing.com so cookies are scoped to the page origin
//   - empty string otherwise (C# falls back to the production URL)
//
// This lets the same WebGL build run against local, staging, or prod
// backends without build-flags or per-environment preprocessor toggles.
// The staging Pages project serves the same artifact that prod does; only
// the hostname differs, so routing by hostname keeps the pipeline simple.
//
// WebSockets don't go through the Pages proxy (Pages Functions don't proxy
// WS upgrades cleanly), so ApiWsUrl_Resolve returns the api-staging host
// directly. CoopClient passes the JWT in the query string, so the WS
// handshake doesn't depend on cookie scope.

mergeInto(LibraryManager.library, {
    ApiUrl_Resolve: function () {
        try {
            var url = '';
            var params = new URLSearchParams(window.location.search);
            var override = params.get('api');
            if (override) {
                url = override.replace(/\/$/, '');
            } else {
                var host = window.location.hostname;
                if (host === 'localhost' || host === '127.0.0.1' || host === '0.0.0.0') {
                    url = 'http://' + host + ':5000';
                } else if (host === 'staging.arrow-thing.com') {
                    url = window.location.origin;
                }
            }
            var bufferSize = lengthBytesUTF8(url) + 1;
            var buffer = _malloc(bufferSize);
            stringToUTF8(url, buffer, bufferSize);
            return buffer;
        } catch (e) {
            return 0;
        }
    },

    ApiWsUrl_Resolve: function () {
        try {
            var url = '';
            var params = new URLSearchParams(window.location.search);
            var override = params.get('api');
            if (override) {
                // Mirror ?api= override to its ws scheme so a local-API
                // override also redirects WebSocket connects.
                var stripped = override.replace(/\/$/, '');
                if (stripped.indexOf('https://') === 0) {
                    url = 'wss://' + stripped.substring('https://'.length);
                } else if (stripped.indexOf('http://') === 0) {
                    url = 'ws://' + stripped.substring('http://'.length);
                }
            } else {
                var host = window.location.hostname;
                if (host === 'localhost' || host === '127.0.0.1' || host === '0.0.0.0') {
                    url = 'ws://' + host + ':5000';
                } else if (host === 'staging.arrow-thing.com') {
                    url = 'wss://api-staging.arrow-thing.com';
                }
            }
            var bufferSize = lengthBytesUTF8(url) + 1;
            var buffer = _malloc(bufferSize);
            stringToUTF8(url, buffer, bufferSize);
            return buffer;
        } catch (e) {
            return 0;
        }
    },
});
