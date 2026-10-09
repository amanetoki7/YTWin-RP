using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace YTWin_RichPresence {

    /// <summary>What Discord publicly knows about one of our applications: its icon and uploaded Rich Presence assets.</summary>
    internal sealed class DiscordAppInfo {
        public string AppId = "";
        public string? Name;
        public string? IconUrl;
        public HashSet<string> AssetNames = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>An uploaded asset with this name wins, otherwise the application icon, otherwise the fallback.</summary>
        public string? ImageFor(string assetName, string? fallbackUrl = null) {
            if (AssetNames.Contains(assetName)) {
                return assetName;
            }
            return IconUrl ?? fallbackUrl;
        }
    }

    /// <summary>
    /// Reads application icon and asset names through Discord's unauthenticated application endpoints, so the
    /// icon configured in the Developer Portal can be shown without uploading or hard-coding any image.
    /// </summary>
    internal static class DiscordAppInfoCache {
        private static readonly ConcurrentDictionary<string, DiscordAppInfo> cache = new();
        private static readonly ConcurrentDictionary<string, Task> inflight = new();

        public static DiscordAppInfo? Get(string? appId) {
            return appId != null && cache.TryGetValue(appId, out var info) ? info : null;
        }

        public static void Prefetch(string? appId, Logger? logger = null) {
            if (string.IsNullOrWhiteSpace(appId) || cache.ContainsKey(appId)) {
                return;
            }
            inflight.GetOrAdd(appId, id => FetchAsync(id, logger));
        }

        private static async Task<string> GetAsync(string url) {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "YTWin-RP");
            using var response = await Constants.HttpClient.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }

        private static async Task FetchAsync(string appId, Logger? logger) {
            try {
                var info = new DiscordAppInfo { AppId = appId };
                using (var doc = JsonDocument.Parse(await GetAsync($"https://discord.com/api/v10/oauth2/applications/{appId}/rpc").ConfigureAwait(false))) {
                    info.Name = doc.RootElement.Get("name").Str();
                    var icon = doc.RootElement.Get("icon").Str();
                    if (!string.IsNullOrEmpty(icon)) {
                        info.IconUrl = $"https://cdn.discordapp.com/app-icons/{appId}/{icon}.png?size=256";
                    }
                }
                try {
                    using var assets = JsonDocument.Parse(await GetAsync($"https://discord.com/api/v10/oauth2/applications/{appId}/assets").ConfigureAwait(false));
                    foreach (var asset in ((JsonElement?)assets.RootElement).Arr()) {
                        var name = asset.Get("name").Str();
                        if (!string.IsNullOrEmpty(name)) {
                            info.AssetNames.Add(name);
                        }
                    }
                } catch (Exception ex) {
                    logger?.Log($"Could not list assets of Discord application {appId}: {ex.Message}");
                }
                cache[appId] = info;
                logger?.Log($"Discord application {appId}: name='{info.Name}', icon={(info.IconUrl != null ? "yes" : "no")}, assets={string.Join(",", info.AssetNames)}");
            } catch (Exception ex) {
                logger?.Log($"Could not read Discord application {appId}: {ex.Message}");
            } finally {
                inflight.TryRemove(appId, out _);
            }
        }
    }
}
