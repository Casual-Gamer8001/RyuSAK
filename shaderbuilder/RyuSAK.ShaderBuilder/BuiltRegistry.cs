using System.Text.Json;
using System.Text.Json.Nodes;

namespace RyuSAK.ShaderBuilder;

/// <summary>
/// Remembers which programs in a game's shader cache RyuSAK added (games/&lt;title ID&gt;/cache/ryusak-built.json),
/// so they can be removed again and are not mistaken for settings the game really used.
/// </summary>
static class BuiltRegistry
{
    private const string FileName = "ryusak-built.json";

    private static string PathOf(string dataDir, ulong titleId) =>
        Path.Combine(dataDir, "games", titleId.ToString("x16"), "cache", FileName);

    public static HashSet<ulong> Load(string dataDir, ulong titleId) => LoadWithGuestCount(dataDir, titleId).Fingerprints;

    /// <summary>
    /// The fingerprints of every program RyuSAK added, and how many guest code entries the cache had before the
    /// first of those builds (-1 if unknown).
    /// </summary>
    public static (HashSet<ulong> Fingerprints, int GuestBefore) LoadWithGuestCount(string dataDir, ulong titleId)
    {
        HashSet<ulong> fingerprints = new();
        int guestBefore = int.MaxValue;
        string path = PathOf(dataDir, titleId);
        if (!File.Exists(path))
        {
            return (fingerprints, -1);
        }

        try
        {
            JsonNode root = JsonNode.Parse(File.ReadAllText(path));
            foreach (JsonNode build in root?["builds"]?.AsArray() ?? new JsonArray())
            {
                byte[] raw = Convert.FromBase64String((string)build["fingerprints"] ?? "");
                for (int i = 0; i + 8 <= raw.Length; i += 8)
                {
                    fingerprints.Add(BitConverter.ToUInt64(raw, i));
                }

                int before = (int?)build["guestBefore"] ?? -1;
                guestBefore = before < 0 || guestBefore < 0 ? -1 : Math.Min(guestBefore, before);
            }
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            throw new BuilderException("REGISTRY_DAMAGED", $"RyuSAK's record of the shaders it added ({path}) is damaged. Delete it to continue.");
        }

        return (fingerprints, guestBefore == int.MaxValue ? -1 : guestBefore);
    }

    public static void Add(string dataDir, ulong titleId, IReadOnlyList<ulong> fingerprints, int guestBefore)
    {
        string path = PathOf(dataDir, titleId);
        JsonObject root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        root ??= new JsonObject { ["version"] = 1 };

        if (root["builds"] is not JsonArray builds)
        {
            root["builds"] = builds = new JsonArray();
        }

        byte[] raw = new byte[fingerprints.Count * 8];
        for (int i = 0; i < fingerprints.Count; i++)
        {
            BitConverter.TryWriteBytes(raw.AsSpan(i * 8), fingerprints[i]);
        }

        builds.Add(new JsonObject
        {
            ["created"] = DateTime.UtcNow.ToString("o"),
            ["builder"] = typeof(BuiltRegistry).Assembly.GetName().Version?.ToString(3),
            ["count"] = fingerprints.Count,
            ["guestBefore"] = guestBefore,
            ["fingerprints"] = Convert.ToBase64String(raw),
        });

        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temp = path + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        File.Move(temp, path, overwrite: true);
    }

    public static void Clear(string dataDir, ulong titleId)
    {
        string path = PathOf(dataDir, titleId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
