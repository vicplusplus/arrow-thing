// Browser WebSocket bridge for CoopClient (WebGL builds).
//
// Exposes three Unity-callable functions and dispatches connection events
// back into Unity via SendMessage on a hidden GameObject named "CoopBridge"
// (created on-demand from C#) — handlers are CoopClient.OnJsOpen / OnJsMessage
// / OnJsClose. Each connection is keyed by an integer handle.

mergeInto(LibraryManager.library, {
    $CoopWS_State: {
        sockets: {},
        nextHandle: 1,
        bridgeObject: 'CoopBridge',
    },

    CoopWS_Connect: function (urlPtr) {
        var url = UTF8ToString(urlPtr);
        var handle = CoopWS_State.nextHandle++;

        try {
            var ws = new WebSocket(url);
            CoopWS_State.sockets[handle] = ws;

            ws.onopen = function () {
                SendMessage(CoopWS_State.bridgeObject, 'JsOnOpen', String(handle));
            };

            ws.onmessage = function (e) {
                // Format the payload as "{handle}|{json}" so the C# side can split.
                SendMessage(CoopWS_State.bridgeObject, 'JsOnMessage', handle + '|' + e.data);
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

    CoopWS_Send: function (handle, dataPtr) {
        var ws = CoopWS_State.sockets[handle];
        if (!ws || ws.readyState !== 1 /* OPEN */) {
            return;
        }
        var data = UTF8ToString(dataPtr);
        ws.send(data);
    },

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
