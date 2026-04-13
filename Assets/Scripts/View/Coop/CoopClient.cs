using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
#if !UNITY_WEBGL || UNITY_EDITOR
using System.Net.WebSockets;
using System.Text;
#else
using System.Runtime.InteropServices;
using AOT;
#endif

/// <summary>
/// WebSocket client for co-op lobbies. Cross-platform: desktop/editor uses
/// <see cref="System.Net.WebSockets.ClientWebSocket"/>; WebGL uses a thin
/// JS bridge (<c>Assets/Plugins/WebGL/CoopWebSocket.jslib</c>) to the browser
/// WebSocket API.
///
/// Phase 3 skeleton — connect, send, receive, exponential reconnect. No
/// gameplay logic. Events are dispatched on the main thread via a queue
/// flushed in <see cref="Update"/> (call from a MonoBehaviour).
/// </summary>
public class CoopClient : IDisposable
{
    public event Action Connected;
    public event Action<CoopMessage> MessageReceived;
    public event Action<string> Disconnected;

    private readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();
    private bool _disposed;

#if !UNITY_WEBGL || UNITY_EDITOR
    // -- Desktop / Editor: System.Net.WebSockets.ClientWebSocket -------------

    private ClientWebSocket _socket;
    private CancellationTokenSource _cts;
    private Task _receiveLoop;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public async Task ConnectAsync(string baseWsUrl, string code, string token)
    {
        if (_socket != null)
            throw new InvalidOperationException("Already connected.");

        _socket = new ClientWebSocket();
        _cts = new CancellationTokenSource();

        var uri = new Uri($"{baseWsUrl}/ws/coop/{code}?token={Uri.EscapeDataString(token)}");
        try
        {
            await _socket.ConnectAsync(uri, _cts.Token);
        }
        catch (Exception e)
        {
            EnqueueDisconnected($"Connect failed: {e.Message}");
            CleanupSocket();
            throw;
        }

        EnqueueConnected();
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    public async Task SendAsync(CoopMessage msg)
    {
        if (_socket == null || _socket.State != WebSocketState.Open)
            return;
        var bytes = Encoding.UTF8.GetBytes(msg.Serialize());
        await _socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            _cts.Token
        );
    }

    public async Task DisconnectAsync()
    {
        if (_socket == null)
            return;
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "client disconnect",
                    CancellationToken.None
                );
        }
        catch { }
        CleanupSocket();
        EnqueueDisconnected("client disconnect");
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        try
        {
            while (_socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var ms = new System.IO.MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    EnqueueDisconnected("server closed");
                    break;
                }

                var json = Encoding.UTF8.GetString(ms.ToArray());
                if (string.IsNullOrEmpty(json))
                    continue;

                CoopMessage msg = null;
                try
                {
                    msg = CoopMessage.Deserialize(json);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[CoopClient] Bad JSON: {e.Message}");
                }
                if (msg != null)
                    EnqueueReceived(msg);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            EnqueueDisconnected($"Receive error: {e.Message}");
        }
    }

    private void CleanupSocket()
    {
        try
        {
            _cts?.Cancel();
        }
        catch { }
        _socket?.Dispose();
        _socket = null;
        _cts = null;
        _receiveLoop = null;
    }
#else
    // -- WebGL: JS bridge ----------------------------------------------------

    [DllImport("__Internal")]
    private static extern int CoopWS_Connect(string url);

    [DllImport("__Internal")]
    private static extern void CoopWS_Send(int handle, string data);

    [DllImport("__Internal")]
    private static extern void CoopWS_Close(int handle);

    private static readonly ConcurrentDictionary<int, CoopClient> _instances =
        new ConcurrentDictionary<int, CoopClient>();
    private int _handle = -1;

    public bool IsConnected => _handle >= 0;

    public Task ConnectAsync(string baseWsUrl, string code, string token)
    {
        if (_handle >= 0)
            throw new InvalidOperationException("Already connected.");

        CoopWebGLBridge.EnsureExists();

        var url = $"{baseWsUrl}/ws/coop/{code}?token={Uri.EscapeDataString(token)}";
        _handle = CoopWS_Connect(url);
        if (_handle < 0)
            throw new InvalidOperationException("CoopWS_Connect failed.");

        _instances[_handle] = this;
        return Task.CompletedTask;
    }

    public Task SendAsync(CoopMessage msg)
    {
        if (_handle < 0)
            return Task.CompletedTask;
        CoopWS_Send(_handle, msg.Serialize());
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        if (_handle < 0)
            return Task.CompletedTask;
        CoopWS_Close(_handle);
        _instances.TryRemove(_handle, out _);
        _handle = -1;
        EnqueueDisconnected("client disconnect");
        return Task.CompletedTask;
    }

    // jslib calls these via SendMessage, routed by handle.
    public static void OnJsOpen(int handle)
    {
        if (_instances.TryGetValue(handle, out var inst))
            inst.EnqueueConnected();
    }

    public static void OnJsMessage(int handle, string json)
    {
        if (_instances.TryGetValue(handle, out var inst))
        {
            try
            {
                var msg = CoopMessage.Deserialize(json);
                if (msg != null)
                    inst.EnqueueReceived(msg);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CoopClient] Bad JSON from JS: {e.Message}");
            }
        }
    }

    public static void OnJsClose(int handle, string reason)
    {
        if (_instances.TryRemove(handle, out var inst))
        {
            inst._handle = -1;
            inst.EnqueueDisconnected(reason ?? "closed");
        }
    }
#endif

    // -- Main-thread dispatch -----------------------------------------------

    /// <summary>Call from a MonoBehaviour Update to flush queued events on the main thread.</summary>
    public void Update()
    {
        while (_mainThreadQueue.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Debug.LogError($"[CoopClient] Event handler threw: {e}");
            }
        }
    }

    private void EnqueueConnected() => _mainThreadQueue.Enqueue(() => Connected?.Invoke());

    private void EnqueueReceived(CoopMessage msg) =>
        _mainThreadQueue.Enqueue(() => MessageReceived?.Invoke(msg));

    private void EnqueueDisconnected(string reason) =>
        _mainThreadQueue.Enqueue(() => Disconnected?.Invoke(reason));

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
#if !UNITY_WEBGL || UNITY_EDITOR
        CleanupSocket();
#else
        if (_handle >= 0)
        {
            CoopWS_Close(_handle);
            _instances.TryRemove(_handle, out _);
            _handle = -1;
        }
#endif
    }
}
