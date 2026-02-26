using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace WowProxy.App.Services;

public class GeoIpResolutionService
{
    private static readonly GeoIpResolutionService _instance = new();
    public static GeoIpResolutionService Instance => _instance;

    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, GeoIpInfo> _cache = new();

    private GeoIpResolutionService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    public async Task<GeoIpInfo?> ResolveIpAsync(string ip)
    {
        // Ignore local or invalid IPs
        if (string.IsNullOrWhiteSpace(ip) || ip == "127.0.0.1" || ip == "localhost" || 
            ip.StartsWith("192.168.") || ip.StartsWith("10.") || ip.StartsWith("172.16."))
        {
            return null;
        }

        if (_cache.TryGetValue(ip, out var cachedInfo))
        {
            return cachedInfo;
        }

        try
        {
            // Using ip-api.com free tier (no API key required)
            var url = $"http://ip-api.com/json/{ip}?fields=status,country,lat,lon";
            var result = await _httpClient.GetFromJsonAsync<IpApiResult>(url);

            if (result != null && result.Status == "success")
            {
                var info = new GeoIpInfo
                {
                    Ip = ip,
                    Country = result.Country,
                    Latitude = result.Lat,
                    Longitude = result.Lon
                };
                
                _cache.TryAdd(ip, info);
                return info;
            }
            else
            {
                // Cache negative results briefly to prevent API spamming
                _cache.TryAdd(ip, new GeoIpInfo { Ip = ip }); 
            }
        }
        catch (Exception)
        {
            // Ignore resolution errors
        }

        return null;
    }

    private class IpApiResult
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }
        
        [JsonPropertyName("country")]
        public string? Country { get; set; }
        
        [JsonPropertyName("lat")]
        public double Lat { get; set; }
        
        [JsonPropertyName("lon")]
        public double Lon { get; set; }
    }
}

public class GeoIpInfo
{
    public string Ip { get; set; } = string.Empty;
    public string? Country { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public bool IsResolved => Country != null;
}
