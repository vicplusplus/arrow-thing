// Deep-link helper: reads the ?lobby= query parameter from the current URL.
// Called once at boot by CoopDeepLink.cs on the C# side.

mergeInto(LibraryManager.library, {
    CoopDeepLink_Get: function () {
        try {
            var params = new URLSearchParams(window.location.search);
            var code = params.get('lobby') || '';
            var bufferSize = lengthBytesUTF8(code) + 1;
            var buffer = _malloc(bufferSize);
            stringToUTF8(code, buffer, bufferSize);
            return buffer;
        } catch (e) {
            return 0;
        }
    },
});
