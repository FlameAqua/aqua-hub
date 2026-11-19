using System.Text.Json;
using AquaHub.Core.Models;
using AquaHub.Core.Reminders;
using AquaHub.Core.Util;

namespace AquaHub.Core.Data;

public sealed partial class HubDatabase
{
    internal Reminder[] LoadReminders()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN length(data) <= 524288 THEN data ELSE '' END FROM reminder_state WHERE id=1;";
        return command.ExecuteScalar() is string json
            ? JsonSerializer.Deserialize<Reminder[]>(json, JsonUtil.Options) ?? throw new JsonException()
            : [];
    }

    internal List<HubAlert> SaveReminders(Reminder[] items, List<string> withdrawn, List<HubAlert> alerts)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO reminder_state(id, data) VALUES(1, $data) ON CONFLICT(id) DO UPDATE SET data=excluded.data;";
        command.Parameters.AddWithValue("$data", JsonUtil.Serialize(items));
        command.ExecuteNonQuery();

        command.Parameters.Clear();
        command.CommandText = "DELETE FROM alerts WHERE id=$id AND kind='reminder';";
        var id = command.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);
        foreach (var alertId in withdrawn)
        {
            id.Value = alertId;
            command.ExecuteNonQuery();
        }

        var inserted = alerts.Where(alert => InsertAlert(connection, transaction, alert)).ToList();
        transaction.Commit();
        return inserted;
    }
}
