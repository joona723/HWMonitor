using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HWMonitor;

/// <summary>Persists user-chosen process priorities by process name so they can be reapplied automatically.</summary>
sealed class ProcessPriorityStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HWMonitor", "process-priorities.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public Dictionary<string, ProcessPriorityClass> Rules { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static ProcessPriorityStore Load()
    {
        try
        {
            string json = File.ReadAllText(FilePath);
            ProcessPriorityStore? store = JsonSerializer.Deserialize<ProcessPriorityStore>(json, JsonOptions);
            if (store is not null)
            {
                store.Rules = new Dictionary<string, ProcessPriorityClass>(store.Rules, StringComparer.OrdinalIgnoreCase);
                return store;
            }
        }
        catch
        {
            // First run or corrupt file; start fresh.
        }
        return new ProcessPriorityStore();
    }

    public ProcessPriorityClass? Get(string processName) =>
        Rules.TryGetValue(processName, out ProcessPriorityClass priority) ? priority : null;

    public void Set(string processName, ProcessPriorityClass priority)
    {
        Rules[processName] = priority;
        Save();
    }

    public void Clear(string processName)
    {
        if (Rules.Remove(processName))
        {
            Save();
        }
    }

    private void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(FilePath);
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Best-effort; a failed save just means the rule won't persist across restarts.
        }
    }
}
