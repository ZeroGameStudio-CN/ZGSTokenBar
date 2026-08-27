using System.Text.Json;

namespace ZGSTokenBar.Host;

public sealed record MigrationStatus(
    int CurrentSchemaVersion,
    bool SettingsExists,
    int? DetectedSchemaVersion,
    bool V1BackupExists,
    bool V2RollbackExists);

public sealed class SettingsMigrationManager
{
    private readonly string _dataRoot;
    private readonly string _settingsPath;

    public SettingsMigrationManager(string dataRoot)
    {
        _dataRoot = Path.GetFullPath(dataRoot);
        _settingsPath = Path.Combine(_dataRoot, "settings.json");
    }

    public MigrationStatus Status() =>
        new(
            2,
            File.Exists(_settingsPath),
            DetectSchema(_settingsPath),
            File.Exists(_settingsPath + ".v1.bak"),
            File.Exists(_settingsPath + ".v2.rollback"));

    public MigrationStatus RestoreV1()
    {
        var backup = _settingsPath + ".v1.bak";
        if (!IsValidV1(backup)) throw new InvalidDataException("Invalid v1 backup.");
        Directory.CreateDirectory(_dataRoot);
        var rollback = _settingsPath + ".v2.rollback";
        if (File.Exists(_settingsPath))
        {
            AtomicCopy(_settingsPath, rollback);
        }
        AtomicCopy(backup, _settingsPath);
        return Status();
    }

    public static bool IsValidV1(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.ValueKind is JsonValueKind.Object
                && (!document.RootElement.TryGetProperty("schemaVersion", out var schema)
                    || schema.ValueKind is JsonValueKind.Number && schema.GetInt32() == 1);
        }
        catch
        {
            return false;
        }
    }

    private static int? DetectSchema(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.TryGetProperty("schemaVersion", out var schema)
                && schema.TryGetInt32(out var value)
                    ? value
                    : 1;
        }
        catch
        {
            return null;
        }
    }

    private static void AtomicCopy(string source, string destination)
    {
        var temporary = destination + $".tmp-{Guid.NewGuid():N}";
        try
        {
            File.Copy(source, temporary, overwrite: false);
            if (File.Exists(destination))
            {
                File.Move(temporary, destination, overwrite: true);
            }
            else
            {
                File.Move(temporary, destination);
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
