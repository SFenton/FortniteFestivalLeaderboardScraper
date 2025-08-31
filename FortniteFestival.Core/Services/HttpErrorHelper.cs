using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FortniteFestival.Core.Services
{
    public static class HttpErrorHelper
    {
        // Central error code counts (shared across service instances)
        private static readonly ConcurrentDictionary<string,int> _errorCodeCounts = new ConcurrentDictionary<string,int>(StringComparer.OrdinalIgnoreCase);

        public static (string errorCode, string errorMessage) ExtractError(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith("{")) return (null,null);
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                var root = doc.RootElement;
                string code = null;
                string msg = null;
                if (root.TryGetProperty("errorCode", out var ecProp) && ecProp.ValueKind == JsonValueKind.String)
                    code = ecProp.GetString();
                // Multiple patterns for message text
                if (root.TryGetProperty("errorMessage", out var emProp) && emProp.ValueKind == JsonValueKind.String)
                    msg = emProp.GetString();
                else if (root.TryGetProperty("message", out var mProp) && mProp.ValueKind == JsonValueKind.String)
                    msg = mProp.GetString();
                if(!string.IsNullOrEmpty(code)) _errorCodeCounts.AddOrUpdate(code,1,(_,v)=>v+1);
                return (code,msg);
                }
            }
            catch { return (null,null); }
        }

        public static string FormatHttpError(string op, HttpResponseMessage res, string body, string errorCode, string errorMessage)
        {
            var status = (int)res.StatusCode;
            string snippet = body==null?"<no-body>": body.Replace('\n',' ').Replace('\r',' ');
            if (snippet.Length > 180) snippet = snippet.Substring(0,180)+"...";
            var sb = new StringBuilder();
            sb.Append('[').Append(op).Append("] HTTP ").Append(status).Append(' ').Append('(').Append(res.StatusCode).Append(')');
            sb.Append(' ').Append("errorCode=").Append(errorCode??"<none>");
            if(!string.IsNullOrWhiteSpace(errorMessage)) sb.Append(' ').Append("msg=\"").Append(Truncate(errorMessage,120)).Append('"');
            sb.Append(' ').Append("bodySnippet=").Append(snippet);
            return sb.ToString();
        }

        private static string Truncate(string s, int len) => string.IsNullOrEmpty(s) ? s : (s.Length<=len? s : s.Substring(0,len)+"...");

        public static string BuildSummaryLine()
        {
            if(_errorCodeCounts.IsEmpty) return "ErrorCodeSummary: <none>";
            var parts = _errorCodeCounts.OrderByDescending(kv=>kv.Value).Select(kv=>$"{kv.Key}={kv.Value}");
            return "ErrorCodeSummary: "+ string.Join(", ", parts);
        }

        // Snapshot of counts for persistence/reporting
        public static IReadOnlyDictionary<string,int> GetErrorCountsSnapshot()
        {
            if(_errorCodeCounts.IsEmpty) return new Dictionary<string,int>();
            return _errorCodeCounts.ToDictionary(kv=>kv.Key, kv=>kv.Value, StringComparer.OrdinalIgnoreCase);
        }

        public static string ComputeCorrelationId(Exception ex)
        {
            if(ex==null) return "00000000";
            string sig = ex.GetType().FullName + "|" + ex.Message + "|" + (ex.StackTrace==null?"": new string(ex.StackTrace.Take(200).ToArray()));
            byte[] data = Encoding.UTF8.GetBytes(sig);
            using(var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(data);
                var sb = new StringBuilder();
                for(int i=0;i<hash.Length && i<4;i++) sb.Append(hash[i].ToString("X2")); // 8 hex chars
                return sb.ToString();
            }
        }
    }
}
