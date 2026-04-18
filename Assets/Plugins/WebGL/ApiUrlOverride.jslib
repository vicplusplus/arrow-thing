// Resolves the API base URL for WebGL builds. Returns:
//   - `?api=<url>` query param if present (full override, no trailing slash)
//   - `http://localhost:5000` when the page is served from localhost/127.0.0.1
//   - empty string otherwise (C# falls back to the production URL)
//
// This lets a local WebGL build point at a local ASP.NET Core API without
// any rebuild-flags or per-environment preprocessor toggles.

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
