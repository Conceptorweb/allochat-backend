using System.Text.Json;
using Microsoft.EntityFrameworkCore;

public static class BackupEndpoints
{
    private static readonly TimeSpan BackupValidityDuration = TimeSpan.FromHours(24);
    private const int MaxEncryptedPayloadJsonLength = 2_000_000;

    public static void MapBackupEndpoints(this WebApplication app)
    {
        app.MapPost("/api/backup/export", async (BackupExportRequest request, AlloChatDbContext db) =>
        {
            var cleanAlloCode = request.AlloCode?.Trim().ToUpperInvariant() ?? "";
            var cleanUserID = request.UserID?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(cleanAlloCode) || string.IsNullOrWhiteSpace(cleanUserID))
            {
                return Results.BadRequest(new StandardServerResponse(
                    false,
                    "AlloCode and UserID are required to create a backup."
                ));
            }

            if (request.EncryptedPayload == null)
            {
                return Results.BadRequest(new StandardServerResponse(
                    false,
                    "Backup data is missing. Please try again."
                ));
            }

            if (!IsValidEncryptedPayload(request.EncryptedPayload))
            {
                return Results.BadRequest(new StandardServerResponse(
                    false,
                    "Backup data is invalid. Please create a new backup."
                ));
            }

            var userExists = await db.Users.AnyAsync(u =>
                u.UserID == cleanUserID &&
                u.AlloCode.ToUpper() == cleanAlloCode
            );

            if (!userExists)
            {
                Console.WriteLine($"SECURITY_BLOCK backup_export identity_mismatch userID={ShortID(cleanUserID)} alloCode={ShortID(cleanAlloCode)}");
                return Results.BadRequest(new StandardServerResponse(
                    false,
                    "Backup identity could not be verified."
                ));
            }

            var payloadJson = JsonSerializer.Serialize(request.EncryptedPayload);

            if (payloadJson.Length > MaxEncryptedPayloadJsonLength)
            {
                return Results.BadRequest(new StandardServerResponse(
                    false,
                    "Backup data is too large. Please reduce saved data and try again."
                ));
            }

            var now = DateTime.UtcNow;
            var existingBackup = await db.Backups.FirstOrDefaultAsync(b => b.AlloCode == cleanAlloCode);

            if (existingBackup == null)
            {
                db.Backups.Add(new BackupEntity
                {
                    AlloCode = cleanAlloCode,
                    UserID = cleanUserID,
                    EncryptedPayloadJson = payloadJson,
                    UpdatedAt = now
                });
            }
            else
            {
                existingBackup.UserID = cleanUserID;
                existingBackup.EncryptedPayloadJson = payloadJson;
                existingBackup.UpdatedAt = now;
            }

            await db.SaveChangesAsync();

            Console.WriteLine($"BACKUP_EXPORT userID={ShortID(cleanUserID)} alloCode={ShortID(cleanAlloCode)} size={payloadJson.Length}");

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
            var cleanUserID = request.UserID?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(cleanAlloCode) || string.IsNullOrWhiteSpace(cleanUserID))
            {
                return Results.BadRequest(new BackupImportResponse(
                    false,
                    "AlloCode and UserID are required to restore a backup.",
                    null
                ));
            }

            var identityValid = await db.Users.AnyAsync(u =>
                u.UserID == cleanUserID &&
                u.AlloCode.ToUpper() == cleanAlloCode
            );

            if (!identityValid)
            {
                Console.WriteLine($"SECURITY_BLOCK backup_import identity_mismatch userID={ShortID(cleanUserID)} alloCode={ShortID(cleanAlloCode)}");
                return Results.BadRequest(new BackupImportResponse(
                    false,
                    "Backup identity could not be verified.",
                    null
                ));
            }

            var backup = await db.Backups.FirstOrDefaultAsync(b =>
                b.AlloCode == cleanAlloCode &&
                b.UserID == cleanUserID
            );

            if (backup == null)
            {
                return Results.NotFound(new BackupImportResponse(
                    false,
                    "No backup was found for this profile. Please create a new backup first.",
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

            if (encryptedPayload == null || !IsValidEncryptedPayload(encryptedPayload))
            {
                return Results.BadRequest(new BackupImportResponse(
                    false,
                    "This backup is incomplete and cannot be restored. Please create a new backup.",
                    null
                ));
            }

            Console.WriteLine($"BACKUP_IMPORT userID={ShortID(cleanUserID)} alloCode={ShortID(cleanAlloCode)} ageMinutes={(int)backupAge.TotalMinutes}");

            return Results.Ok(new BackupImportResponse(
                true,
                "Backup found. You can now restore it on this watch.",
                encryptedPayload
            ));
        })
        .WithName("ImportBackup")
        .WithOpenApi();
    }

    private static bool IsValidEncryptedPayload(EncryptedBackupPayload payload)
    {
        return payload.Version > 0 &&
               !string.IsNullOrWhiteSpace(payload.Salt) &&
               payload.Salt.Length <= 512 &&
               !string.IsNullOrWhiteSpace(payload.EncryptedData) &&
               payload.EncryptedData.Length <= MaxEncryptedPayloadJsonLength;
    }

    private static string ShortID(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) { return "empty"; }
        return value.Length <= 8 ? value : value.Substring(0, 8);
    }
}
