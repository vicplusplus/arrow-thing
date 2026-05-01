using System;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// Environment-specific backend URLs, sourced from
/// <c>Assets/Resources/BackendConfig.json</c> at runtime. Build pipelines
/// (CI, deploy scripts) can swap the JSON per target environment without a
/// code change. Falls back to compile-time defaults if the JSON is missing
/// or malformed so a misdeployed build still boots.
///
/// WebGL builds run the JS resolver in <c>ApiUrlOverride.jslib</c> first;
/// this config only kicks in when that resolver returns empty (the
/// production hostname case).
///
/// See <c>docs/Networking.md</c> for the full env matrix.
/// </summary>
internal static class BackendConfig
{
    private const string FallbackEditorApiBaseUrl = "http://localhost:5000";
    private const string FallbackRuntimeApiBaseUrl = "https://api.arrow-thing.com";
    private const string ResourceName = "BackendConfig";

    private static Settings _cached;

    public static string ApiBaseUrl
    {
#if UNITY_EDITOR
        get => Get().EditorApiBaseUrl;
#else
        get => Get().RuntimeApiBaseUrl;
#endif
    }

    private static Settings Get()
    {
        if (_cached != null)
            return _cached;

        var asset = Resources.Load<TextAsset>(ResourceName);
        if (asset == null)
        {
            Debug.LogWarning(
                $"[BackendConfig] Resources/{ResourceName}.json missing; using fallback URLs."
            );
            _cached = Settings.Defaults();
            return _cached;
        }

        try
        {
            _cached = JsonConvert.DeserializeObject<Settings>(asset.text) ?? Settings.Defaults();
        }
        catch (Exception e)
        {
            Debug.LogError(
                $"[BackendConfig] Failed to parse {ResourceName}.json: {e.Message}. Using fallback URLs."
            );
            _cached = Settings.Defaults();
        }

        if (string.IsNullOrEmpty(_cached.EditorApiBaseUrl))
            _cached.EditorApiBaseUrl = FallbackEditorApiBaseUrl;
        if (string.IsNullOrEmpty(_cached.RuntimeApiBaseUrl))
            _cached.RuntimeApiBaseUrl = FallbackRuntimeApiBaseUrl;

        return _cached;
    }

    private sealed class Settings
    {
        [JsonProperty("editorApiBaseUrl")]
        public string EditorApiBaseUrl { get; set; }

        [JsonProperty("runtimeApiBaseUrl")]
        public string RuntimeApiBaseUrl { get; set; }

        public static Settings Defaults() =>
            new Settings
            {
                EditorApiBaseUrl = FallbackEditorApiBaseUrl,
                RuntimeApiBaseUrl = FallbackRuntimeApiBaseUrl,
            };
    }
}
