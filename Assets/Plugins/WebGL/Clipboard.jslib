// Clipboard write helper for WebGL builds. Uses the modern async Clipboard API
// with a fallback to execCommand('copy') for older browsers.

mergeInto(LibraryManager.library, {
    Clipboard_WriteText: function (textPtr) {
        var text = UTF8ToString(textPtr);
        try {
            if (navigator.clipboard && navigator.clipboard.writeText) {
                navigator.clipboard.writeText(text).catch(function (e) {
                    console.warn('[Clipboard] writeText failed', e);
                });
                return;
            }
            // Fallback: hidden textarea + execCommand
            var ta = document.createElement('textarea');
            ta.value = text;
            ta.style.position = 'absolute';
            ta.style.left = '-9999px';
            document.body.appendChild(ta);
            ta.select();
            document.execCommand('copy');
            document.body.removeChild(ta);
        } catch (e) {
            console.warn('[Clipboard] write failed', e);
        }
    },
});
