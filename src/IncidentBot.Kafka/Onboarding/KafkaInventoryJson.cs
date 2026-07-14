using System.Text.Json;

namespace IncidentBot.Kafka.Onboarding;

public static class KafkaInventoryJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string Serialize(KafkaApplicationInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        inventory.EnsureSupportedVersion();
        return JsonSerializer.Serialize(inventory, Options).ReplaceLineEndings("\n") + "\n";
    }

    public static KafkaApplicationInventory Deserialize(string json)
    {
        var inventory = JsonSerializer.Deserialize<KafkaApplicationInventory>(json, Options)
            ?? throw new InvalidOperationException("Kafka inventory JSON is empty.");
        inventory.EnsureSupportedVersion();
        return inventory;
    }
}
