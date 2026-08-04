using System.Net;
using System.Net.Sockets;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math.EC.Rfc7748;

namespace Valenius.Backend.Services;

/// <summary>
/// Performs a real WireGuard® handshake against a server, entirely in userspace over a plain UDP
/// socket — no network interface, no routing changes, and no elevated privileges.
///
/// <para>
/// This is the only check that proves what actually matters end to end, because a completed
/// handshake can only happen if <em>all</em> of the following hold:
/// <list type="bullet">
///   <item>the UDP port is genuinely reachable from here — unlike an ICMP probe, which cannot
///   distinguish an open port from a firewall silently dropping packets;</item>
///   <item>the server's static public key is exactly what we believe it is, since the response
///   is only decryptable by someone holding the matching private key;</item>
///   <item><b>our peer was accepted</b> — WireGuard does not answer a handshake from an unknown
///   public key at all, so a reply is proof the peer registration took effect.</item>
/// </list>
/// </para>
///
/// <para>
/// What it deliberately does NOT prove: that traffic flows once connected. Forwarding, NAT and
/// routing live inside the sidecar host and are covered by its own <c>/selftest</c>.
/// </para>
/// </summary>
/// <remarks>
/// Implements the initiator half of Noise_IKpsk2 as specified by WireGuard, with an all-zero
/// pre-shared key (the sidecar does not use one) and no cookie handling — a cookie reply only
/// appears when the server is under load-based DoS mitigation, which for a self-test is
/// correctly reported as "no answer" rather than silently retried forever.
/// </remarks>
public sealed class WireGuardHandshakeProbe
{
    private static readonly byte[] Construction = Encoding.ASCII.GetBytes("Noise_IKpsk2_25519_ChaChaPoly_BLAKE2s");
    private static readonly byte[] Identifier   = Encoding.ASCII.GetBytes("WireGuard v1 zx2c4 Jason@zx2c4.com");
    private static readonly byte[] LabelMac1    = Encoding.ASCII.GetBytes("mac1----");

    private const int MsgInitiation = 1;
    private const int MsgResponse   = 2;
    private const int InitiationLen = 148;
    private const int ResponseLen   = 92;

    public sealed record Result(bool Ok, string Message, long? RoundTripMs = null);

    /// <summary>Generates an X25519 keypair in WireGuard's base64 wire format.</summary>
    public static (byte[] Private, byte[] Public, string PublicBase64) GenerateKeyPair()
    {
        var priv = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var pub  = new byte[32];
        // BouncyCastle clamps the scalar internally (DecodeScalar), so raw random bytes are fine.
        X25519.ScalarMultBase(priv, 0, pub, 0);
        return (priv, pub, Convert.ToBase64String(pub));
    }

    /// <summary>
    /// Sends a handshake initiation to <paramref name="host"/>:<paramref name="port"/> and verifies
    /// the response cryptographically. Never throws — every failure is a <see cref="Result"/> whose
    /// message is written for an administrator, not a developer.
    /// </summary>
    public async Task<Result> TryHandshakeAsync(
        string host, int port, string serverPublicKeyBase64,
        byte[] ourPrivate, byte[] ourPublic,
        TimeSpan timeout, CancellationToken ct = default)
    {
        byte[] serverStatic;
        try
        {
            serverStatic = Convert.FromBase64String(serverPublicKeyBase64);
            if (serverStatic.Length != 32)
                return new Result(false, "The server's public key is not a valid 32-byte WireGuard key.");
        }
        catch
        {
            return new Result(false, "The server's public key is not valid base64.");
        }

        try
        {
            var (packet, state) = BuildInitiation(serverStatic, ourPrivate, ourPublic);

            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.ReceiveTimeout = (int)timeout.TotalMilliseconds;

            var started = System.Diagnostics.Stopwatch.StartNew();
            await udp.SendAsync(packet, packet.Length, host, port);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            UdpReceiveResult reply;
            try
            {
                reply = await udp.ReceiveAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Silence is genuinely ambiguous and must not be reported as one specific cause.
                // WireGuard answers a handshake it accepts and says nothing at all otherwise — it
                // drops packets whose mac1 doesn't match (wrong server key) and packets from an
                // unregistered public key, identically to a firewall discarding them. Verified
                // against a real server: all three produce exactly this timeout. Blaming the
                // firewall here would send an operator to the wrong place two times out of three.
                return new Result(false,
                    $"No handshake response from {host}:{port}. WireGuard answers every handshake it "
                    + "accepts and is silent otherwise, so this is one of: the UDP port is not "
                    + "reachable from this backend, the test peer was not really registered on the "
                    + "server, or the server's public key differs from the one on record.");
            }

            var elapsed = started.ElapsedMilliseconds;

            if (reply.Buffer.Length < 4)
                return new Result(false, $"Received a {reply.Buffer.Length}-byte reply, too short to be WireGuard.");

            if (reply.Buffer[0] == 3)
                return new Result(false,
                    "The server replied with a cookie challenge, which means it is rate-limiting "
                    + "handshakes (usually under load or an active flood). Try again shortly.");

            if (reply.Buffer[0] != MsgResponse)
                return new Result(false,
                    $"Expected a handshake response but the server sent message type {reply.Buffer[0]}.");

            if (reply.Buffer.Length != ResponseLen)
                return new Result(false,
                    $"Handshake response was {reply.Buffer.Length} bytes, expected {ResponseLen}.");

            return VerifyResponse(reply.Buffer, state, ourPrivate, elapsed);
        }
        catch (SocketException ex)
        {
            return new Result(false, $"Could not reach {host}:{port} — {ex.SocketErrorCode}.");
        }
        catch (Exception ex)
        {
            return new Result(false, "The handshake could not be completed: " + ex.Message);
        }
    }

    /// <summary>Initiator state carried between message 1 and message 2.</summary>
    private sealed record HandshakeState(byte[] ChainingKey, byte[] Hash, byte[] EphemeralPrivate, uint SenderIndex);

    private static (byte[] Packet, HandshakeState State) BuildInitiation(
        byte[] serverStatic, byte[] ourPrivate, byte[] ourPublic)
    {
        var ck = Hash(Construction);
        var h  = Hash(Concat(ck, Identifier));
        h      = Hash(Concat(h, serverStatic));

        var ephPrivate = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var ephPublic  = new byte[32];
        X25519.ScalarMultBase(ephPrivate, 0, ephPublic, 0);

        h  = Hash(Concat(h, ephPublic));
        ck = Kdf1(ck, ephPublic);

        // es = DH(our ephemeral, server static)
        var es = Dh(ephPrivate, serverStatic);
        var (ck2, k1) = Kdf2(ck, es);
        ck = ck2;

        var encryptedStatic = Aead(true, k1, 0, ourPublic, h);
        h = Hash(Concat(h, encryptedStatic));

        // ss = DH(our static, server static)
        var ss = Dh(ourPrivate, serverStatic);
        var (ck3, k2) = Kdf2(ck, ss);
        ck = ck3;

        var encryptedTimestamp = Aead(true, k2, 0, Tai64N(), h);
        h = Hash(Concat(h, encryptedTimestamp));

        var senderIndex = BitConverter.ToUInt32(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));

        var packet = new byte[InitiationLen];
        packet[0] = MsgInitiation;                                   // type
        // packet[1..4] reserved, already zero
        BinaryPrimitivesWriteUInt32LE(packet, 4, senderIndex);
        Buffer.BlockCopy(ephPublic,          0, packet,  8, 32);
        Buffer.BlockCopy(encryptedStatic,    0, packet, 40, 48);
        Buffer.BlockCopy(encryptedTimestamp, 0, packet, 88, 28);

        // mac1 covers everything before it. Getting this wrong makes the server drop the packet
        // silently, which would surface as a false "port blocked" — the most misleading possible
        // failure for a diagnostic tool.
        var mac1Key = Hash(Concat(LabelMac1, serverStatic));
        var mac1    = KeyedBlake2s128(mac1Key, packet.AsSpan(0, 116).ToArray());
        Buffer.BlockCopy(mac1, 0, packet, 116, 16);
        // mac2 stays zero: only required after the server has issued a cookie.

        return (packet, new HandshakeState(ck, h, ephPrivate, senderIndex));
    }

    private static Result VerifyResponse(byte[] msg, HandshakeState state, byte[] ourPrivate, long elapsedMs)
    {
        var receiverIndex = BitConverter.ToUInt32(msg, 8);
        if (receiverIndex != state.SenderIndex)
            return new Result(false,
                "The handshake response was addressed to a different session — another client may be "
                + "using the same source port.");

        var respEphemeral = msg.AsSpan(12, 32).ToArray();
        var encryptedNothing = msg.AsSpan(44, 16).ToArray();

        var ck = state.ChainingKey;
        var h  = state.Hash;

        h  = Hash(Concat(h, respEphemeral));
        ck = Kdf1(ck, respEphemeral);
        ck = Kdf1(ck, Dh(state.EphemeralPrivate, respEphemeral)); // ee
        ck = Kdf1(ck, Dh(ourPrivate, respEphemeral));             // se

        // psk is all-zero: the sidecar provisions peers without a pre-shared key.
        var (ck4, tau, key) = Kdf3(ck, new byte[32]);
        _ = ck4;
        h = Hash(Concat(h, tau));

        try
        {
            // Zero-length plaintext: success is entirely the Poly1305 tag verifying, which is the
            // cryptographic proof that the responder holds the private key for the public key we
            // encrypted to, and that it recognised our peer.
            Aead(false, key, 0, encryptedNothing, h);
        }
        catch (Exception)
        {
            return new Result(false,
                "A handshake response arrived but failed to authenticate. The server's public key is "
                + "not the one this backend has on record — the sidecar may have been re-created with "
                + "new keys, in which case re-enrolling and re-syncing peers will fix it.");
        }

        return new Result(true, $"Handshake completed in {elapsedMs} ms.", elapsedMs);
    }

    // ── Noise / WireGuard primitives ──────────────────────────────────────────

    private static byte[] Hash(byte[] data)
    {
        var d = new Blake2sDigest(256);
        d.BlockUpdate(data, 0, data.Length);
        var o = new byte[32];
        d.DoFinal(o, 0);
        return o;
    }

    private static byte[] Hmac(byte[] key, byte[] data)
    {
        var mac = new HMac(new Blake2sDigest(256));
        mac.Init(new KeyParameter(key));
        mac.BlockUpdate(data, 0, data.Length);
        var o = new byte[mac.GetMacSize()];
        mac.DoFinal(o, 0);
        return o;
    }

    private static byte[] KeyedBlake2s128(byte[] key, byte[] data)
    {
        var d = new Blake2sDigest(key, 16, null, null);
        d.BlockUpdate(data, 0, data.Length);
        var o = new byte[16];
        d.DoFinal(o, 0);
        return o;
    }

    private static byte[] Kdf1(byte[] chainingKey, byte[] input)
    {
        var temp = Hmac(chainingKey, input);
        return Hmac(temp, [0x01]);
    }

    private static (byte[], byte[]) Kdf2(byte[] chainingKey, byte[] input)
    {
        var temp = Hmac(chainingKey, input);
        var o1   = Hmac(temp, [0x01]);
        var o2   = Hmac(temp, Concat(o1, [0x02]));
        return (o1, o2);
    }

    private static (byte[], byte[], byte[]) Kdf3(byte[] chainingKey, byte[] input)
    {
        var temp = Hmac(chainingKey, input);
        var o1   = Hmac(temp, [0x01]);
        var o2   = Hmac(temp, Concat(o1, [0x02]));
        var o3   = Hmac(temp, Concat(o2, [0x03]));
        return (o1, o2, o3);
    }

    private static byte[] Dh(byte[] privateKey, byte[] publicKey)
    {
        var shared = new byte[32];
        X25519.ScalarMult(privateKey, 0, publicKey, 0, shared, 0);
        // An all-zero result means the peer key was a small-order point; continuing would produce
        // a handshake that "works" against an attacker-chosen key, so refuse it outright.
        var acc = 0;
        foreach (var b in shared) acc |= b;
        if (acc == 0)
            throw new CryptoException("X25519 produced an all-zero shared secret (invalid public key).");
        return shared;
    }

    /// <summary>
    /// ChaCha20-Poly1305 with WireGuard's nonce layout: 4 zero bytes followed by the 64-bit
    /// little-endian counter. BouncyCastle is used rather than the platform primitive so the
    /// behaviour is identical on every host the backend might run on.
    /// </summary>
    private static byte[] Aead(bool encrypt, byte[] key, ulong counter, byte[] input, byte[] aad)
    {
        var nonce = new byte[12];
        BitConverter.TryWriteBytes(nonce.AsSpan(4), counter);
        if (!BitConverter.IsLittleEndian) Array.Reverse(nonce, 4, 8);

        var cipher = new ChaCha20Poly1305();
        cipher.Init(encrypt, new AeadParameters(new KeyParameter(key), 128, nonce));
        cipher.ProcessAadBytes(aad, 0, aad.Length);

        var output = new byte[cipher.GetOutputSize(input.Length)];
        var len    = cipher.ProcessBytes(input, 0, input.Length, output, 0);
        len       += cipher.DoFinal(output, len);
        return len == output.Length ? output : output.AsSpan(0, len).ToArray();
    }

    /// <summary>TAI64N timestamp: seconds since the TAI epoch (big-endian) plus nanoseconds.</summary>
    private static byte[] Tai64N()
    {
        var now   = DateTimeOffset.UtcNow;
        var secs  = 0x400000000000000AUL + (ulong)now.ToUnixTimeSeconds();
        var nanos = (uint)(now.Ticks % TimeSpan.TicksPerSecond * 100);

        var b = new byte[12];
        for (var i = 0; i < 8; i++) b[7 - i]  = (byte)(secs  >> (8 * i));
        for (var i = 0; i < 4; i++) b[11 - i] = (byte)(nanos >> (8 * i));
        return b;
    }

    private static void BinaryPrimitivesWriteUInt32LE(byte[] buf, int offset, uint value)
    {
        buf[offset]     = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
        buf[offset + 2] = (byte)(value >> 16);
        buf[offset + 3] = (byte)(value >> 24);
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }
}
