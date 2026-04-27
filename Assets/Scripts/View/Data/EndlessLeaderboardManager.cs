using System;
using System.IO;
using System.IO.Compression;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// View-layer singleton that wraps <see cref="EndlessLeaderboardStore"/> with
/// file-based persistence. Sibling of <see cref="LeaderboardManager"/>; the
/// two stores are kept on separate index files so endless caps + ordering
/// don't compete with classic for the same disk slots.
///
/// Index stored as <c>endless-leaderboard.json</c>; replays stored individually
/// as GZip-compressed JSON at <c>endless-replays/{gameId}.json.gz</c>. Lives
/// across scenes via <c>DontDestroyOnLoad</c>.
/// </summary>
public sealed class EndlessLeaderboardManager : MonoBehaviour
{
    private const string IndexFileName = "endless-leaderboard.json";
    private const string ReplayDirectory = "endless-replays";

    private static EndlessLeaderboardManager _instance;
    public static EndlessLeaderboardManager Instance => _instance;

    private EndlessLeaderboardStore _store;
    public EndlessLeaderboardStore Store => _store;

    private string IndexPath => Path.Combine(Application.persistentDataPath, IndexFileName);
    private string ReplayDir => Path.Combine(Application.persistentDataPath, ReplayDirectory);

#if UNITY_WEBGL && !UNITY_EDITOR
    [System.Runtime.InteropServices.DllImport("__Internal")]
    private static extern void SyncFilesystem();
#endif

    private static void SyncFS()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        SyncFilesystem();
#endif
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void AutoCreate()
    {
        if (_instance != null)
            return;
        var go = new GameObject("EndlessLeaderboardManager");
        go.AddComponent<EndlessLeaderboardManager>();
    }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);
        LoadIndex();
    }

    /// <summary>
    /// Records a topped-out endless run. Builds an
    /// <see cref="EndlessLeaderboardEntry"/>, adds it to the store, persists
    /// the index, and writes the replay file. Returns the created entry.
    /// </summary>
    public EndlessLeaderboardEntry RecordEndlessResult(ReplayData replayData)
    {
        var entry = new EndlessLeaderboardEntry(
            replayData,
            replayData.gameVersion ?? Application.version
        );
        string pruned = _store.AddEntry(entry);

        SaveIndex();
        SaveReplay(entry.gameId, replayData);

        if (pruned != null)
            DeleteReplay(pruned);

        return entry;
    }

    /// <summary>
    /// Returns true if the given (clears, durationSeconds) tuple beats the
    /// current personal best for this board size. First entry for a config
    /// is always a PB.
    /// </summary>
    public bool IsPersonalBest(int width, int height, int clears, double durationSeconds)
    {
        var best = _store.GetPersonalBest(width, height);
        if (best == null)
            return true;
        if (clears != best.clears)
            return clears > best.clears;
        return durationSeconds < best.durationSeconds;
    }

    public bool IsFavorite(string gameId)
    {
        var entry = _store.FindEntry(gameId);
        return entry != null && entry.isFavorite;
    }

    public void SetFavorite(string gameId, bool isFavorite)
    {
        _store.SetFavorite(gameId, isFavorite);
        SaveIndex();
    }

    public void RemoveEntry(string gameId)
    {
        _store.RemoveEntry(gameId);
        SaveIndex();
        DeleteReplay(gameId);
    }

    /// <summary>
    /// Loads a replay from disk. Returns null if the file is missing or corrupted.
    /// </summary>
    public ReplayData LoadReplay(string gameId)
    {
        string gzPath = Path.Combine(ReplayDir, gameId + ".json.gz");
        if (!File.Exists(gzPath))
            return null;

        try
        {
            byte[] compressed = File.ReadAllBytes(gzPath);
            string json = DecompressGZip(compressed);
            return JsonConvert.DeserializeObject<ReplayData>(json);
        }
        catch (Exception e)
        {
            Debug.LogWarning(
                $"EndlessLeaderboardManager: failed to load replay {gameId} — {e.Message}"
            );
            return null;
        }
    }

    // --- Persistence helpers ---

    private void LoadIndex()
    {
        string path = IndexPath;
        if (File.Exists(path))
        {
            try
            {
                string json = File.ReadAllText(path);
                _store = EndlessLeaderboardStore.FromJson(json);
            }
            catch (Exception e)
            {
                Debug.LogWarning(
                    $"EndlessLeaderboardManager: failed to load index — {e.Message}. Starting fresh."
                );
                _store = new EndlessLeaderboardStore();
            }
        }
        else
        {
            _store = new EndlessLeaderboardStore();
        }
    }

    private void SaveIndex()
    {
        try
        {
            string json = _store.ToJson();
            File.WriteAllText(IndexPath, json);
            SyncFS();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"EndlessLeaderboardManager: failed to save index — {e.Message}");
        }
    }

    private void SaveReplay(string gameId, ReplayData data)
    {
        try
        {
            if (!Directory.Exists(ReplayDir))
                Directory.CreateDirectory(ReplayDir);

            string json = JsonConvert.SerializeObject(data);
            byte[] compressed = CompressGZip(json);
            File.WriteAllBytes(Path.Combine(ReplayDir, gameId + ".json.gz"), compressed);
            SyncFS();
        }
        catch (Exception e)
        {
            Debug.LogWarning(
                $"EndlessLeaderboardManager: failed to save replay {gameId} — {e.Message}"
            );
        }
    }

    private void DeleteReplay(string gameId)
    {
        try
        {
            string gzPath = Path.Combine(ReplayDir, gameId + ".json.gz");
            if (File.Exists(gzPath))
                File.Delete(gzPath);
            SyncFS();
        }
        catch (Exception e)
        {
            Debug.LogWarning(
                $"EndlessLeaderboardManager: failed to delete replay {gameId} — {e.Message}"
            );
        }
    }

    private static byte[] CompressGZip(string text)
    {
        byte[] raw = System.Text.Encoding.UTF8.GetBytes(text);
        using (var output = new MemoryStream())
        {
            using (var gz = new GZipStream(output, CompressionMode.Compress))
            {
                gz.Write(raw, 0, raw.Length);
            }
            return output.ToArray();
        }
    }

    private static string DecompressGZip(byte[] compressed)
    {
        using (var input = new MemoryStream(compressed))
        using (var gz = new GZipStream(input, CompressionMode.Decompress))
        using (var reader = new StreamReader(gz, System.Text.Encoding.UTF8))
        {
            return reader.ReadToEnd();
        }
    }
}
