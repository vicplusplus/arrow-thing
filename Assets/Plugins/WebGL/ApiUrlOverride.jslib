// Resolves the API base URL for WebGL builds. Returns:
//   - `?api=<url>` query param if present (full override, no trailing slash)
//   - `http://localhost:5000` when the page is served from localhost/127.0.0.1
//   - `https://api-staging.arrow-thing.com` when served from the staging host
//   - empty string otherwise (C# falls back to the production URL)
//
// This lets the same WebGL build run against local, staging, or prod
// backends without build-flags or per-environment preprocessor toggles.
// The staging Pages project serves the same artifact that prod does; only
// the hostname differs, so routing by hostname keeps the pipeline simple.

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
                    url = 'https://api-staging.arrow-thing.com';
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
