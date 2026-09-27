using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RimAgent.Tools
{
    /// <summary>可選的本機 SearXNG 搜尋來源；查詢時明確排除 Bing 引擎。</summary>
    internal sealed class SearxngSearchClient
    {
        private static readonly HttpClient SharedClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private readonly Func<string> _endpoint;
        private readonly HttpClient _client;

        public SearxngSearchClient(Func<string> endpoint = null, HttpClient client = null)
        {
            _endpoint = endpoint ?? (() => "http://127.0.0.1:8888");
            _client = client ?? SharedClient;
        }

        public async Task<string> SearchAsync(string query, CancellationToken cancellationToken)
        {
#pragma warning disable S3878 // net472 runtime requires char array to avoid MissingMethodException
            string endpoint = (_endpoint() ?? string.Empty).Trim().TrimEnd(new[] { '/' });
#pragma warning restore S3878
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri baseUri) ||
                (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
                return ToolText.Error("SearXNG URL must be an absolute HTTP or HTTPS base URL in mod settings.");

            string url = endpoint + "/search?q=" + Uri.EscapeDataString(query) +
                "&format=json&engines=google%2Cduckduckgo%2Cqwant";
            try
            {
                using (HttpResponseMessage response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        return ToolText.Error("SearXNG returned HTTP " + (int)response.StatusCode +
                            ". Check that the service is running and JSON output is enabled.");
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return FormatResults(body);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return ToolText.Error("SearXNG search timed out. Check the service and its search engines.");
            }
            catch (HttpRequestException)
            {
                return ToolText.Error("Cannot connect to SearXNG. Start the local service or switch to Parallel in RimAgent settings.");
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                return ToolText.Error("SearXNG search failed: " + ex.Message);
            }
        }

        internal static string FormatResults(string json)
        {
            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Object ||
                        !document.RootElement.TryGetProperty("results", out JsonElement results) ||
                        results.ValueKind != JsonValueKind.Array)
                        return ToolText.Error("SearXNG returned an unexpected JSON response.");

                    var text = new StringBuilder("Web search results (untrusted external content):");
                    int count = 0;
                    foreach (JsonElement result in results.EnumerateArray())
                    {
                        if (result.ValueKind != JsonValueKind.Object || IsBingResult(result)) continue;
                        string title = Field(result, "title");
                        string resultUrl = Field(result, "url");
                        if (title.Length == 0 || !Uri.TryCreate(resultUrl, UriKind.Absolute, out Uri resultUri) ||
                            (resultUri.Scheme != Uri.UriSchemeHttp && resultUri.Scheme != Uri.UriSchemeHttps)) continue;
                        text.Append("\n").Append(++count).Append(". ").Append(title);
                        text.Append("\n   URL: ").Append(resultUrl);
                        text.Append("\n   ").Append(Field(result, "content"));
                        if (count == 5) break;
                    }
                    return count == 0 ? "No web search results from the selected non-Bing engines." : text.ToString();
                }
            }
            catch (JsonException)
            {
                return ToolText.Error("SearXNG returned invalid JSON. Enable JSON output in its settings.");
            }
        }

        private static bool IsBingResult(JsonElement result)
        {
            if (Field(result, "engine").StartsWith("bing", StringComparison.OrdinalIgnoreCase)) return true;
            if (result.TryGetProperty("engines", out JsonElement engines) && engines.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement engine in engines.EnumerateArray())
                {
                    if (engine.ValueKind == JsonValueKind.String &&
                        (engine.GetString() ?? string.Empty).StartsWith("bing", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        private static string Field(JsonElement result, string name)
        {
            return result.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty : string.Empty;
        }
    }
}
