using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aictiq.IntegrationTests.Auth;

/// <summary>
/// A WebAuthn authenticator in a few dozen lines: one P-256 key, "none" attestation, and
/// enough CBOR to say so. It answers the options the API hands out exactly as a browser's
/// <c>navigator.credentials.create()</c> / <c>get()</c> would, serialized with
/// <c>PublicKeyCredential.toJSON()</c> - so the API's real verification runs end to end.
/// </summary>
public sealed class FakeAuthenticator : IDisposable
{
    /// <summary>What the test server sees as its origin, and therefore what the browser would report.</summary>
    public const string Origin = "http://localhost";

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private uint _signCount;
    private string? _userHandle;

    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(16);

    public string Id => Base64Url.EncodeToString(CredentialId);

    /// <summary>The response to <c>navigator.credentials.create(options)</c>.</summary>
    public JsonElement Create(JsonElement options)
    {
        var challenge = options.GetProperty("challenge").GetString()!;
        var rpId = options.GetProperty("rp").TryGetProperty("id", out var id) ? id.GetString()! : "localhost";
        _userHandle = options.GetProperty("user").GetProperty("id").GetString();

        var clientData = ClientData("webauthn.create", challenge);
        var parameters = _key.ExportParameters(false);
        var coseKey = Cbor.Map(
            (Cbor.Int(1), Cbor.Int(2)),          // kty: EC2
            (Cbor.Int(3), Cbor.Int(-7)),         // alg: ES256
            (Cbor.Int(-1), Cbor.Int(1)),         // crv: P-256
            (Cbor.Int(-2), Cbor.Bytes(parameters.Q.X!)),
            (Cbor.Int(-3), Cbor.Bytes(parameters.Q.Y!)));

        var authData = new List<byte>();
        authData.AddRange(SHA256.HashData(Encoding.UTF8.GetBytes(rpId)));
        authData.Add(0x01 | 0x04 | 0x40); // user present, user verified, attested credential data
        authData.AddRange(BigEndian(_signCount));
        authData.AddRange(new byte[16]); // AAGUID
        authData.Add((byte)(CredentialId.Length >> 8));
        authData.Add((byte)CredentialId.Length);
        authData.AddRange(CredentialId);
        authData.AddRange(coseKey);

        var attestationObject = Cbor.Map(
            (Cbor.Text("fmt"), Cbor.Text("none")),
            (Cbor.Text("attStmt"), Cbor.Map()),
            (Cbor.Text("authData"), Cbor.Bytes([.. authData])));

        return Serialize(new JsonObject
        {
            ["id"] = Id,
            ["rawId"] = Id,
            ["type"] = "public-key",
            ["authenticatorAttachment"] = "platform",
            ["clientExtensionResults"] = new JsonObject(),
            ["response"] = new JsonObject
            {
                ["clientDataJSON"] = Base64Url.EncodeToString(clientData),
                ["attestationObject"] = Base64Url.EncodeToString(attestationObject),
                ["transports"] = new JsonArray("internal"),
            },
        });
    }

    /// <summary>The response to <c>navigator.credentials.get(options)</c>.</summary>
    public JsonElement Get(JsonElement options)
    {
        var challenge = options.GetProperty("challenge").GetString()!;
        var rpId = options.TryGetProperty("rpId", out var id) ? id.GetString()! : "localhost";

        var clientData = ClientData("webauthn.get", challenge);
        _signCount++;

        var authData = new List<byte>();
        authData.AddRange(SHA256.HashData(Encoding.UTF8.GetBytes(rpId)));
        authData.Add(0x01 | 0x04);
        authData.AddRange(BigEndian(_signCount));

        var signed = authData.Concat(SHA256.HashData(clientData)).ToArray();
        var signature = _key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        return Serialize(new JsonObject
        {
            ["id"] = Id,
            ["rawId"] = Id,
            ["type"] = "public-key",
            ["authenticatorAttachment"] = "platform",
            ["clientExtensionResults"] = new JsonObject(),
            ["response"] = new JsonObject
            {
                ["clientDataJSON"] = Base64Url.EncodeToString(clientData),
                ["authenticatorData"] = Base64Url.EncodeToString([.. authData]),
                ["signature"] = Base64Url.EncodeToString(signature),
                ["userHandle"] = _userHandle,
            },
        });
    }

    public void Dispose() => _key.Dispose();

    private static byte[] ClientData(string type, string challenge) =>
        JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["type"] = type,
            ["challenge"] = challenge,
            ["origin"] = Origin,
            ["crossOrigin"] = false,
        });

    private static byte[] BigEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static JsonElement Serialize(JsonObject node) =>
        JsonSerializer.Deserialize<JsonElement>(node.ToJsonString());

    /// <summary>The handful of CBOR shapes WebAuthn needs, and nothing else.</summary>
    private static class Cbor
    {
        public static byte[] Int(long value) =>
            value >= 0 ? Head(0, (ulong)value) : Head(1, (ulong)(-1 - value));

        public static byte[] Bytes(byte[] value) => [.. Head(2, (ulong)value.Length), .. value];

        public static byte[] Text(string value)
        {
            var utf8 = Encoding.UTF8.GetBytes(value);
            return [.. Head(3, (ulong)utf8.Length), .. utf8];
        }

        public static byte[] Map(params (byte[] Key, byte[] Value)[] entries) =>
            [.. Head(5, (ulong)entries.Length), .. entries.SelectMany(e => e.Key.Concat(e.Value))];

        private static byte[] Head(int major, ulong length)
        {
            var type = (byte)(major << 5);
            return length switch
            {
                < 24 => [(byte)(type | (byte)length)],
                <= byte.MaxValue => [(byte)(type | 24), (byte)length],
                <= ushort.MaxValue => [(byte)(type | 25), (byte)(length >> 8), (byte)length],
                _ => throw new NotSupportedException("Longer CBOR items are not needed here."),
            };
        }
    }
}
