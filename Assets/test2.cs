using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// Streams everything Twitch sends about one channel's chat to the Unity Console, as raw JSON:
// messages, notifications (subs, gift subs, raids, announcements...), deleted messages,
// chat clears, bans/timeouts and chat setting changes. Works for any channel.
// Uses Twitch EventSub directly, not the plugin. You log in once with your own account (read-only).
public class TwitchChatToConsole : MonoBehaviour
{
    [SerializeField] string clientId = "";  // your app's Client ID from dev.twitch.tv/console/apps
    [SerializeField] string channel = "";   // login name of the channel to read

    const string Scope = "user:read:chat";
    const string EventSubUrl = "wss://eventsub.wss.twitch.tv/ws";
    const string TokenKey = "TwitchChatToConsole.Token";
    static readonly TimeSpan Silence = TimeSpan.FromSeconds(30); // Twitch sends a keepalive after 10 s without events
    static readonly string[] ChatEvents =
    {
        "channel.chat.message",
        "channel.chat.notification",
        "channel.chat.message_delete",
        "channel.chat.clear",
        "channel.chat.clear_user_messages",
        "channel.chat_settings.update",
    };
    static readonly HttpClient Http = new HttpClient();

    CancellationTokenSource stop;
    ClientWebSocket active;
    string token, userId, broadcasterId;

    void OnEnable()
    {
        clientId = clientId.Trim();
        channel = channel.Trim().TrimStart('#').ToLowerInvariant();
        token = PlayerPrefs.GetString(TokenKey, "");
        active = null;
        stop = new CancellationTokenSource();
        _ = Connect();
    }

    void OnDisable() => stop.Cancel();

    // Starts a fresh session: check the saved login, look up the channel, connect and subscribe.
    async Task Connect()
    {
        CancellationToken ct = stop.Token;
        try
        {
            if (clientId == "" || channel == "") throw new Exception("Set Client Id and Channel in the Inspector.");
            Validation me = await Validate(ct);
            if (me == null || me.client_id != clientId)
            {
                token = await LogIn(ct);
                PlayerPrefs.SetString(TokenKey, token);
                PlayerPrefs.Save();
                me = await Validate(ct) ?? throw new Exception("Twitch rejected the new login.");
            }
            userId = me.user_id;
            broadcasterId ??= await GetUserId(channel, ct);
            await Listen(EventSubUrl, null);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            Debug.LogError("[Twitch] " + e.Message);
        }
    }

    // Reads one connection. After a reconnect message, a new connection takes over
    // when it gets its welcome, so no events are missed.
    async Task Listen(string url, ClientWebSocket previous)
    {
        CancellationToken ct = stop.Token;
        var socket = new ClientWebSocket();
        string reason = "";
        try
        {
            await socket.ConnectAsync(new Uri(url), ct);
            var buffer = new byte[8192];
            var message = new MemoryStream();
            while (true)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(Silence);
                WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    reason = $"closed by Twitch, code {(int?)socket.CloseStatus} {socket.CloseStatusDescription}";
                    break;
                }
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;

                string json = Encoding.UTF8.GetString(message.ToArray());
                message.SetLength(0);
                Envelope m = JsonUtility.FromJson<Envelope>(json);
                switch (m.metadata.message_type)
                {
                    case "session_welcome":
                        active = socket;
                        if (previous != null) previous.Abort(); // subscriptions moved here
                        else await Subscribe(m.payload.session.id, ct);
                        break;
                    case "session_reconnect":
                        _ = Listen(m.payload.session.reconnect_url, socket); // keep reading here until it takes over
                        break;
                    case "notification":
                        Debug.Log($"[Twitch] {m.metadata.subscription_type}: {json}");
                        break;
                    case "revocation":
                        Debug.LogWarning("[Twitch] Subscription revoked: " + json);
                        break;
                }
            }
        }
        catch (Exception e)
        {
            reason = e.Message;
        }
        finally
        {
            socket.Dispose();
        }

        // Start over only if this was the live connection, not one replaced by a reconnect.
        bool wasLive = socket == active || (active == null && previous == null);
        if (ct.IsCancellationRequested || !wasLive) return;
        Debug.LogWarning($"[Twitch] Connection lost ({reason}). Reconnecting.");
        active = null;
        await Task.Delay(2000);
        if (!ct.IsCancellationRequested) _ = Connect();
    }

    async Task Subscribe(string sessionId, CancellationToken ct)
    {
        foreach (string type in ChatEvents)
        {
            string body = $"{{\"type\":\"{type}\",\"version\":\"1\"," +
                $"\"condition\":{{\"broadcaster_user_id\":\"{broadcasterId}\",\"user_id\":\"{userId}\"}}," +
                $"\"transport\":{{\"method\":\"websocket\",\"session_id\":\"{sessionId}\"}}}}";
            using var request = Helix(HttpMethod.Post, "eventsub/subscriptions");
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                Debug.LogError($"[Twitch] Subscribing to {type} failed: {await response.Content.ReadAsStringAsync()}");
        }
        Debug.Log($"[Twitch] Streaming #{channel} chat.");
    }

    // Twitch device code login: opens the browser, then waits for you to approve.
    async Task<string> LogIn(CancellationToken ct)
    {
        var (ok, body) = await PostForm("https://id.twitch.tv/oauth2/device",
            new Dictionary<string, string> { ["client_id"] = clientId, ["scopes"] = Scope }, ct);
        if (!ok) throw new Exception("Login request failed: " + body);
        DeviceCode device = JsonUtility.FromJson<DeviceCode>(body);
        Debug.Log($"[Twitch] Approve the login in your browser. Code: {device.user_code}");
        Application.OpenURL(device.verification_uri);

        var poll = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scopes"] = Scope,
            ["device_code"] = device.device_code,
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
        };
        int interval = Math.Max(device.interval, 1);
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            (ok, body) = await PostForm("https://id.twitch.tv/oauth2/token", poll, ct);
            TokenResponse result = JsonUtility.FromJson<TokenResponse>(body);
            if (ok) return result.access_token;
            if (result.message == "slow_down") interval += 5;
            else if (result.message != "authorization_pending") throw new Exception("Login failed: " + body);
        }
    }

    async Task<Validation> Validate(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate");
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", token);
        using HttpResponseMessage response = await Http.SendAsync(request, ct);
        return response.IsSuccessStatusCode
            ? JsonUtility.FromJson<Validation>(await response.Content.ReadAsStringAsync())
            : null;
    }

    async Task<string> GetUserId(string login, CancellationToken ct)
    {
        using var request = Helix(HttpMethod.Get, "users?login=" + Uri.EscapeDataString(login));
        using HttpResponseMessage response = await Http.SendAsync(request, ct);
        string body = await response.Content.ReadAsStringAsync();
        UserList users = JsonUtility.FromJson<UserList>(body);
        if (!response.IsSuccessStatusCode || users.data == null || users.data.Length == 0)
            throw new Exception($"Channel \"{login}\" not found: {body}");
        return users.data[0].id;
    }

    HttpRequestMessage Helix(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, "https://api.twitch.tv/helix/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Client-Id", clientId);
        return request;
    }

    static async Task<(bool ok, string body)> PostForm(string url, Dictionary<string, string> fields, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(fields);
        using HttpResponseMessage response = await Http.PostAsync(url, content, ct);
        return (response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    [Serializable] class DeviceCode { public string device_code, user_code, verification_uri; public int interval; }
    [Serializable] class TokenResponse { public string access_token, message; }
    [Serializable] class Validation { public string client_id, login, user_id; }
    [Serializable] class UserList { public UserData[] data; }
    [Serializable] class UserData { public string id; }
    [Serializable] class Envelope { public Metadata metadata; public Payload payload; }
    [Serializable] class Metadata { public string message_type, subscription_type; }
    [Serializable] class Payload { public Session session; }
    [Serializable] class Session { public string id, reconnect_url; }
}