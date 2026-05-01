using System.Text.Json;
using Microsoft.EntityFrameworkCore;

public static class BackupEndpoints
{
    private static readonly TimeSpan BackupValidityDuration = TimeSpan.FromHours(24);

    public static void MapBackupEndpoints(this WebApplication app)
    {
        app.MapPost("/api/backup/export", async (BackupExportRequest request, AlloChatDbContext db) =>
        {
            var cleanAlloCode = request.AlloCode?.Trim().ToUpperInvariant() ?? "";

            if (string.IsNullOrWhiteSpace(cleanAlloCode))
            {
                return Results.BadRequest(new StandardServerResponse(
                    false,
                    "AlloCode is required to create a backup."
                ));
            }

            if (request.EncryptedPayload == null)
            {
                return Results.BadRequest(new StandardServerResponse(
                    false,
                    "Backup data is missing. Please try again."
                ));
            }

            if (request.EncryptedPayload.Version <= 0 ||
                string.IsNullOrWhiteSpace(request.EncryptedPayload.Salt) ||
                string.IsNullOrWhiteSpace(request.EncryptedPayload.EncryptedData))
            {
                return Results.BadRequest(new StandardServerResponse(
                    false,
                    "Backup data is invalid. Please create a new backup."
                ));
            }

            var userExists = await db.Users.AnyAsync(u => u.AlloCode.ToUpper() == cleanAlloCode);

            if (!userExists)
            {
                return Results.NotFound(new StandardServerResponse(
                    false,
                    "No AlloChat profile was found for this AlloCode."
                ));
            }

            var now = DateTime.UtcNow;
            var payloadJson = JsonSerializer.Serialize(request.EncryptedPayload);

            var existingBackup = await db.Backups
                .FirstOrDefaultAsync(b => b.AlloCode == cleanAlloCode);

            if (existingBackup == null)
            {
                db.Backups.Add(new BackupEntity
                {
                    AlloCode = cleanAlloCode,
                    EncryptedPayloadJson = payloadJson,
                    UpdatedAt = now
                });
            }
            else
            {
                existingBackup.EncryptedPayloadJson = payloadJson;
                existingBackup.UpdatedAt = now;
            }

            await db.SaveChangesAsync();

            return Results.Ok(new StandardServerResponse(
                true,
                "Backup created successfully. It will remain valid for 24 hours."
            ));
        })
        .WithName("ExportBackup")
        .WithOpenApi();

        app.MapPost("/api/backup/import", async (BackupImportRequest request, AlloChatDbContext db) =>
        {
            var cleanAlloCode = request.AlloCode?.Trim().ToUpperInvariant() ?? "";

            if (string.IsNullOrWhiteSpace(cleanAlloCode))
            {
                return Results.BadRequest(new BackupImportResponse(
                    false,
                    "AlloCode is required to restore a backup.",
                    null
                ));
            }

            var backup = await db.Backups
                .FirstOrDefaultAsync(b => b.AlloCode == cleanAlloCode);

            if (backup == null)
            {
                return Results.NotFound(new BackupImportResponse(
                    false,
                    "No backup was found for this AlloCode. Please create a new backup first.",
                    null
                ));
            }

            var backupAge = DateTime.UtcNow - backup.UpdatedAt;

            if (backupAge > BackupValidityDuration)
            {
                db.Backups.Remove(backup);
                await db.SaveChangesAsync();

                return Results.BadRequest(new BackupImportResponse(
                    false,
                    "This backup has expired. Backups are valid for 24 hours only. Please create a new backup.",
                    null
                ));
            }

            EncryptedBackupPayload? encryptedPayload;

            try
            {
                encryptedPayload = JsonSerializer.Deserialize<EncryptedBackupPayload>(backup.EncryptedPayloadJson);
            }
            catch
            {
                return Results.BadRequest(new BackupImportResponse(
                    false,
                    "This backup is damaged and cannot be restored. Please create a new backup.",
                    null
                ));
            }

            if (encryptedPayload == null ||
                encryptedPayload.Version <= 0 ||
                string.IsNullOrWhiteSpace(encryptedPayload.Salt) ||
                string.IsNullOrWhiteSpace(encryptedPayload.EncryptedData))
            {
                return Results.BadRequest(new BackupImportResponse(
                    false,
                    "This backup is incomplete and cannot be restored. Please create a new backup.",
                    null
                ));
            }

            return Results.Ok(new BackupImportResponse(
                true,
                "Backup found. You can now restore it on this watch.",
                encryptedPayload
            ));
        })
        .WithName("ImportBackup")
        .WithOpenApi();
    }
}