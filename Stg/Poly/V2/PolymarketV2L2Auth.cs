using System;
using System.Security.Cryptography;
using System.Text;

namespace QjySDK.Stg.Poly.V2
{
    public sealed class PolymarketL2Headers
    {
        public string Address { get; init; } = "";
        public string Signature { get; init; } = "";
        public string Timestamp { get; init; } = "";
        public string ApiKey { get; init; } = "";
        public string Passphrase { get; init; } = "";
    }

    /// <summary>
    /// Generates the 5 POLY_* HTTP headers required by all CLOB trading endpoints.
    /// Byte-exact port of src/signing/hmac.ts + src/headers/index.ts (createL2Headers).
    /// </summary>
    public static class PolymarketV2L2Auth
    {
        public static PolymarketL2Headers BuildHeaders(
            string signerAddress,
            string apiKey,
            string apiSecretBase64Url,
            string passphrase,
            string method,
            string requestPath,
            string? body,
            long? unixSeconds = null)
        {
            var ts = unixSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var sig = BuildHmacSignature(apiSecretBase64Url, ts, method, requestPath, body);

            return new PolymarketL2Headers
            {
                Address = signerAddress,
                Signature = sig,
                Timestamp = ts.ToString(),
                ApiKey = apiKey,
                Passphrase = passphrase,
            };
        }

        /// <summary>
        /// HMAC-SHA256 over (timestamp + method + requestPath + body) with the base64url-decoded secret.
        /// Output is the base64url encoding of the 32-byte HMAC result (with '=' padding preserved, '+'→'-', '/'→'_').
        /// </summary>
        public static string BuildHmacSignature(string secretB64Url, long timestamp, string method, string requestPath, string? body)
        {
            if (string.IsNullOrEmpty(secretB64Url)) throw new ArgumentException("secret is empty");

            var message = timestamp.ToString() + method + requestPath + (body ?? "");
            var key = DecodeBase64Url(secretB64Url);

            using var hmac = new HMACSHA256(key);
            var mac = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));

            // base64 then make it url-safe; KEEP '=' padding (matches TS impl which uses btoa + replace).
            var b64 = Convert.ToBase64String(mac);
            return b64.Replace('+', '-').Replace('/', '_');
        }

        /// <summary>
        /// Decodes a base64url string (with optional missing padding) to bytes.
        /// </summary>
        public static byte[] DecodeBase64Url(string s)
        {
            var normalized = s.Replace('-', '+').Replace('_', '/');
            int mod = normalized.Length % 4;
            if (mod != 0) normalized = normalized + new string('=', 4 - mod);
            return Convert.FromBase64String(normalized);
        }
    }
}
