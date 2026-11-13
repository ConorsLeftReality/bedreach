using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace BedReach;

/// <summary>
/// TCP control server + UDP discovery responder. Protocol details: PROTOCOL.md.
/// </summary>
public sealed class RemoteServer
{
    public const string DiscoverMagic = "BEDREACH_DISCOVER_V1";
    private const int MaxFrame = 64 * 1024;

    private readonly AppConfig _cfg;
    private readonly Func<string, string, Task<bool>> _confirmPair; // (deviceName, code) → allowed?
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _pairLock = new(1, 1);
    private readonly ConcurrentDictionary<IPAddress, Throttle> _throttle = new();
    private int _connected;

    private static IPAddress BindAddress => TestMode.On ? IPAddress.Loopback : IPAddress.Any;

    public event Action? StatusChanged;
    public int ConnectedCount => _connected;
    public string? LastError { get; private set; }
    public string? LastActionError { get; private set; }

    private readonly IPlatform _platform;

    public RemoteServer(AppConfig cfg, IPlatform platform, Func<string, string, Task<bool>> confirmPair)
    {
        _cfg = cfg;
        _platform = platform;
        _confirmPair = confirmPair;
    }

    public void Start()
    {
        _ = Task.Run(AcceptLoop);
        _ = Task.Run(DiscoveryLoop);
    }

    public void Stop() => _cts.Cancel();

    // ------------------------------------------------------------------ discovery

    private async Task DiscoveryLoop()
    {
        try
        {
            using var udp = new UdpClient(new IPEndPoint(BindAddress, _cfg.DiscoveryPort));
            var reply = Encoding.UTF8.GetBytes(new JsonObject
            {
                ["app"] = "bedreach", ["v"] = 1, ["name"] = _cfg.Name, ["id"] = _cfg.ServerId, ["port"] = _cfg.Port,
            }.ToJsonString());
            while (!_cts.IsCancellationRequested)
            {
                var r = await udp.ReceiveAsync(_cts.Token);
                if (Encoding.UTF8.GetString(r.Buffer) == DiscoverMagic)
                    await udp.SendAsync(reply, r.RemoteEndPoint, _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { LastError = "Discovery: " + e.Message; StatusChanged?.Invoke(); }
    }

    // ------------------------------------------------------------------ TCP

    private async Task AcceptLoop()
    {
        TcpListener listener;
        try
        {
            listener = new TcpListener(BindAddress, _cfg.Port);
            listener.Start();
        }
        catch (Exception e) { LastError = $"Port {_cfg.Port}: {e.Message}"; StatusChanged?.Invoke(); return; }

        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await listener.AcceptTcpClientAsync(_cts.Token); }
            catch (OperationCanceledException) { break; }
            catch { continue; }
            _ = Task.Run(() => HandleClient(c));
        }
        listener.Stop();
    }

    private async Task HandleClient(TcpClient client)
    {
        using var _ = client;
        client.NoDelay = true;
        var ip = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
        var throttle = _throttle.GetOrAdd(ip, _ => new Throttle());
        if (throttle.IsBlocked) return;

        var stream = client.GetStream();
        try
        {
            using var firstTimeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            firstTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var first = ParseJson(await ReadFrame(stream, firstTimeout.Token));
            switch ((string?)first["t"])
            {
                case "pair_hello": await HandlePairing(stream, first, throttle); break;
                case "auth": await HandleAuth(stream, first, throttle); break;
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or SocketException or CryptographicException or FormatException or InvalidDataException or System.Text.Json.JsonException)
        {
            // Bad/closed connection — just drop it.
        }
    }

    // ------------------------------------------------------------------ pairing

    private async Task HandlePairing(NetworkStream s, JsonObject hello, Throttle throttle)
    {
        if (!await _pairLock.WaitAsync(0))
        {
            await WriteJson(s, new JsonObject { ["t"] = "error", ["reason"] = "busy" });
            return;
        }
        try
        {
            var deviceName = CleanName((string?)hello["device"]);
            var commit = B64((string?)hello["commit"]);
            if (commit.Length != 32) throw new InvalidDataException();

            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var pcPub = ecdh.ExportSubjectPublicKeyInfo();
            var pcNonce = Crypto.Random(16);
            await WriteJson(s, new JsonObject
            {
                ["t"] = "pair_server", ["pub"] = Convert.ToBase64String(pcPub), ["nonce"] = Convert.ToBase64String(pcNonce),
                ["name"] = _cfg.Name, ["id"] = _cfg.ServerId,
            });

            using var revealTimeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            revealTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            var reveal = ParseJson(await ReadFrame(s, revealTimeout.Token));
            if ((string?)reveal["t"] != "pair_reveal") throw new InvalidDataException();
            var phonePub = B64((string?)reveal["pub"]);
            var phoneNonce = B64((string?)reveal["nonce"]);
            if (phoneNonce.Length != 16 || !Crypto.FixedEquals(Crypto.Sha256(phonePub, phoneNonce), commit))
            {
                throttle.Fail();
                return;
            }

            using var peer = ECDiffieHellman.Create();
            peer.ImportSubjectPublicKeyInfo(phonePub, out _);
            var shared = ecdh.DeriveRawSecretAgreement(peer.PublicKey);
            var pair = Crypto.DerivePairing(shared, phonePub, phoneNonce, pcPub, pcNonce);

            bool allowed;
            var confirm = _confirmPair(deviceName, pair.Code);
            allowed = await Task.WhenAny(confirm, Task.Delay(TimeSpan.FromSeconds(90))) == confirm && confirm.Result;

            if (!allowed)
            {
                throttle.Fail();
                await WriteJson(s, new JsonObject { ["t"] = "pair_denied" });
                return;
            }

            var device = new PairedDevice { Id = Guid.NewGuid().ToString("N"), Name = deviceName, PairedAt = DateTime.Now };
            device.SetSecret(pair.Secret);
            Storage.UpdateDevices(d => d.Add(device));
            var mac = Crypto.Hmac(pair.Secret, Crypto.Utf8("paired"), Crypto.Utf8(device.Id));
            await WriteJson(s, new JsonObject { ["t"] = "pair_ok", ["deviceId"] = device.Id, ["mac"] = Convert.ToBase64String(mac) });
            StatusChanged?.Invoke();
        }
        finally { _pairLock.Release(); }
    }

    // ------------------------------------------------------------------ auth + session

    private async Task HandleAuth(NetworkStream s, JsonObject auth, Throttle throttle)
    {
        var deviceId = (string?)auth["deviceId"] ?? "";
        var phoneNonce = B64((string?)auth["nonce"]);
        if (phoneNonce.Length != 16) throw new InvalidDataException();

        var device = Storage.LoadDevices().FirstOrDefault(d => d.Id == deviceId);
        if (device == null)
        {
            throttle.Fail();
            await WriteJson(s, new JsonObject { ["t"] = "auth_fail", ["reason"] = "unknown_device" });
            return;
        }
        var secret = device.GetSecret();
        var pcNonce = Crypto.Random(16);
        await WriteJson(s, new JsonObject { ["t"] = "challenge", ["nonce"] = Convert.ToBase64String(pcNonce) });

        using var proofTimeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        proofTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        var proof = ParseJson(await ReadFrame(s, proofTimeout.Token));
        var expected = Crypto.Hmac(secret, Crypto.Utf8("client"), phoneNonce, pcNonce);
        if ((string?)proof["t"] != "auth_proof" || !Crypto.FixedEquals(B64((string?)proof["mac"]), expected))
        {
            throttle.Fail();
            await WriteJson(s, new JsonObject { ["t"] = "auth_fail", ["reason"] = "bad_proof" });
            return;
        }

        var serverMac = Crypto.Hmac(secret, Crypto.Utf8("server"), phoneNonce, pcNonce);
        await WriteJson(s, new JsonObject { ["t"] = "auth_ok", ["mac"] = Convert.ToBase64String(serverMac) });

        var key = Crypto.Hkdf(secret, Crypto.Concat(phoneNonce, pcNonce), "bedreach-session-v1");
        using var chan = new SecureChannel(key, isServer: true);
        Storage.UpdateDevices(d => { var x = d.FirstOrDefault(v => v.Id == deviceId); if (x != null) x.LastSeen = DateTime.Now; });

        Interlocked.Increment(ref _connected);
        StatusChanged?.Invoke();
        try
        {
            await SendSecure(s, chan, InfoMessage());
            while (!_cts.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                idle.CancelAfter(TimeSpan.FromSeconds(30)); // phone pings every ~5s
                var msg = ParseJson(chan.Open(await ReadFrame(s, idle.Token)));
                var reply = Dispatch(msg);
                if (reply != null) await SendSecure(s, chan, reply);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _connected);
            StatusChanged?.Invoke();
        }
    }

    private string? InstalledApp(ServiceEntry s)
    {
        if (s.Apps.Count == 0) return null;
        try { return _platform.FindApp(s.Apps); }
        catch { return null; } // detection failing just means "use the website"
    }

    private JsonObject InfoMessage()
    {
        var arr = new JsonArray();
        foreach (var sv in _cfg.Services)
            arr.Add(new JsonObject { ["id"] = sv.Id, ["name"] = sv.Name, ["color"] = sv.Color, ["app"] = InstalledApp(sv) != null });
        return new JsonObject { ["t"] = "info", ["name"] = _cfg.Name, ["services"] = arr };
    }

    /// <summary>Executes one command. Unknown commands are ignored.</summary>
    private JsonObject? Dispatch(JsonObject m)
    {
        var type = (string?)m["t"];
        if (type == "ping") return new JsonObject { ["t"] = "pong" };
        if (TestMode.On)
        {
            // Never touch the real keyboard/mouse in tests; just log what would have happened.
            File.AppendAllText(Path.Combine(Storage.Dir, "commands.log"), m.ToJsonString() + Environment.NewLine);
            return null;
        }
        try
        {
            switch (type)
            {
                case "key":
                    var k = (string?)m["k"] ?? "";
                    if (Keys.All.Contains(k)) _platform.PressKey(k);
                    break;
                case "text": _platform.TypeText(Keys.Clamp((string?)m["s"] ?? "")); break;
                case "move": _platform.MoveMouse(Math.Clamp((int?)m["dx"] ?? 0, -500, 500), Math.Clamp((int?)m["dy"] ?? 0, -500, 500)); break;
                case "click":
                    var b = (string?)m["b"];
                    _platform.Click(b is "right" or "middle" ? b : "left");
                    break;
                case "scroll": _platform.Scroll(Math.Clamp((int?)m["d"] ?? 0, -1200, 1200)); break;
                case "launch":
                    var svc = _cfg.Services.FirstOrDefault(x => x.Id == (string?)m["id"]);
                    if (svc != null)
                    {
                        // Prefer the installed app; otherwise open the website.
                        var app = InstalledApp(svc);
                        if (app != null) _platform.OpenApp(app); else _platform.Launch(svc);
                    }
                    break;
                case "power":
                    var a = (string?)m["a"] ?? "";
                    if (Keys.PowerActions.Contains(a)) _platform.Power(a);
                    break;
            }
        }
        catch (Exception e)
        {
            // A failed action (missing helper tool, app not installed…) must not drop the phone's connection.
            LastActionError = $"{type}: {e.Message}";
        }
        return null;
    }

    // ------------------------------------------------------------------ framing helpers

    private static async Task<byte[]> ReadFrame(Stream s, CancellationToken ct)
    {
        var hdr = new byte[4];
        await s.ReadExactlyAsync(hdr, ct);
        int len = BinaryPrimitives.ReadInt32BigEndian(hdr);
        if (len <= 0 || len > MaxFrame) throw new InvalidDataException("bad frame length");
        var buf = new byte[len];
        await s.ReadExactlyAsync(buf, ct);
        return buf;
    }

    private static async Task WriteFrame(Stream s, byte[] payload)
    {
        var buf = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(buf, payload.Length);
        payload.CopyTo(buf, 4);
        await s.WriteAsync(buf);
    }

    private static Task WriteJson(Stream s, JsonObject o) => WriteFrame(s, Encoding.UTF8.GetBytes(o.ToJsonString()));
    private static Task SendSecure(Stream s, SecureChannel c, JsonObject o) => WriteFrame(s, c.Seal(Encoding.UTF8.GetBytes(o.ToJsonString())));

    private static JsonObject ParseJson(byte[] b) =>
        JsonNode.Parse(b) as JsonObject ?? throw new InvalidDataException("not an object");

    private static byte[] B64(string? s) => Convert.FromBase64String(s ?? throw new InvalidDataException());

    private static string CleanName(string? n)
    {
        var s = new string((n ?? "Phone").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (s.Length == 0) s = "Phone";
        return s.Length > 40 ? s[..40] : s;
    }

    /// <summary>5 failures within 5 minutes blocks an IP for 5 minutes.</summary>
    private sealed class Throttle
    {
        private readonly Queue<DateTime> _fails = new();
        private DateTime _blockedUntil;

        public bool IsBlocked { get { lock (this) return DateTime.UtcNow < _blockedUntil; } }

        public void Fail()
        {
            lock (this)
            {
                var now = DateTime.UtcNow;
                _fails.Enqueue(now);
                while (_fails.Count > 0 && now - _fails.Peek() > TimeSpan.FromMinutes(5)) _fails.Dequeue();
                if (_fails.Count >= 5) { _blockedUntil = now.AddMinutes(5); _fails.Clear(); }
            }
        }
    }
}
