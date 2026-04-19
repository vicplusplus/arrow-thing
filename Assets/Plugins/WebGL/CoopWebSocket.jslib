// Browser WebSocket bridge for CoopClient (WebGL builds).
//
// Exposes three Unity-callable functions and dispatches connection events
// back into Unity via SendMessage on a hidden GameObject named "CoopBridge"
// (created on-demand from C#) — handlers are CoopClient.OnJsOpen /
// OnJsMessage / OnJsBinary / OnJsClose. Each connection is keyed by an
// integer handle. Binary frames are base64-encoded for SendMessage transport
// (which only accepts strings) and decoded back to bytes on the C# side.

// The `__deps` entries below are load-bearing: without them emscripten's
// tree-shaker (stricter in the Unity 6 toolchain) strips the $CoopWS_State
// object as unreferenced at link time, and the first CoopWS_Connect call
// throws `ReferenceError: CoopWS_State is not defined` at runtime.
mergeInto(LibraryManager.library, {
    $CoopWS_State: {
        sockets: {},
        nextHandle: 1,
        bridgeObject: 'CoopBridge',
    },

    CoopWS_Connect__deps: ['$CoopWS_State'],
    CoopWS_Connect: function (urlPtr) {
        var url = UTF8ToString(urlPtr);
        var handle = CoopWS_State.nextHandle++;

        try {
            var ws = new WebSocket(url);
            ws.binaryType = 'arraybuffer';
            CoopWS_State.sockets[handle] = ws;

            ws.onopen = function () {
                SendMessage(CoopWS_State.bridgeObject, 'JsOnOpen', String(handle));
            };

            ws.onmessage = function (e) {
                if (typeof e.data === 'string') {
                    // Format the payload as "{handle}|{json}" so the C# side can split.
                    SendMessage(
                        CoopWS_State.bridgeObject,
                        'JsOnMessage',
                        handle + '|' + e.data
                    );
                } else {
                    // ArrayBuffer — base64-encode for SendMessage (which only accepts strings).
                    var bytes = new Uint8Array(e.data);
                    var binary = '';
                    var chunkSize = 0x8000;
                    for (var i = 0; i < bytes.length; i += chunkSize) {
                        binary += String.fromCharCode.apply(
                            null,
                            bytes.subarray(i, i + chunkSize)
                        );
                    }
                    var base64 = btoa(binary);
                    SendMessage(
                        CoopWS_State.bridgeObject,
                        'JsOnBinary',
                        handle + '|' + base64
                    );
                }
            };

            ws.onclose = function (e) {
                delete CoopWS_State.sockets[handle];
                SendMessage(
                    CoopWS_State.bridgeObject,
                    'JsOnClose',
                    handle + '|' + (e.reason || 'closed')
                );
            };

            ws.onerror = function () {
                // onclose will fire after onerror; the close handler reports the disconnect.
            };

            return handle;
        } catch (err) {
            console.error('[CoopWS] Connect failed', err);
            return -1;
        }
    },

    CoopWS_Send__deps: ['$CoopWS_State'],
    CoopWS_Send: function (handle, dataPtr) {
        var ws = CoopWS_State.sockets[handle];
        if (!ws || ws.readyState !== 1 /* OPEN */) {
            return;
        }
        var data = UTF8ToString(dataPtr);
        ws.send(data);
    },

    CoopWS_Close__deps: ['$CoopWS_State'],
    CoopWS_Close: function (handle) {
        var ws = CoopWS_State.sockets[handle];
        if (!ws) {
            return;
        }
        try {
            ws.close();
        } catch (err) {
            console.warn('[CoopWS] Close failed', err);
        }
        delete CoopWS_State.sockets[handle];
    },
});
