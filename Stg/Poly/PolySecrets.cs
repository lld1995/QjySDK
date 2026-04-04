using System;
using System.Collections.Generic;
using System.IO;

namespace QjySDK.Stg
{
    /// <summary>
    /// 从 poly_secrets.txt 读取 Polymarket 密钥配置
    /// 文件格式: KEY=VALUE（每行一个）
    /// </summary>
    public static class PolySecrets
    {
        private static readonly Lazy<Dictionary<string, string>> _secrets = new(() => Load());

        public static string PrivateKey => Get("PRIVATE_KEY");
        public static string FunderAddress => Get("FUNDER_ADDRESS");
        public static string RelayerApiKey => Get("RELAYER_API_KEY");

        public static string Get(string key)
        {
            _secrets.Value.TryGetValue(key, out var val);
            return val ?? string.Empty;
        }

        private static Dictionary<string, string> Load()
        {
            var dic = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "poly_secrets.txt"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "poly_secrets.txt"),
                Path.Combine(AppContext.BaseDirectory, "poly_secrets.txt"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "poly_secrets.txt"),
            };

            foreach (var p in candidates)
            {
                var full = Path.GetFullPath(p);
                if (!File.Exists(full)) continue;

                foreach (var line in File.ReadAllLines(full))
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;
                    var idx = trimmed.IndexOf('=');
                    if (idx > 0)
                        dic[trimmed[..idx].Trim()] = trimmed[(idx + 1)..].Trim();
                }
                break;
            }

            return dic;
        }
    }
}
