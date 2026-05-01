using System.Text.Json;
using Microsoft.EntityFrameworkCore;

public static class BackupEndpoints
{
    public static void MapBackupEndpoints(this WebApplication app)
    {
        app.MapPost("/api/backup/export", async (BackupExportRequest request, AlloChatDbContext db) =>
        {
            var cleanAlloCode = request.AlloCode?.Trim().ToUpperInvariant() ?? "";

            if (string.IsNullOrWhiteSpace(cleanAlloCode))
            {
                return Results.BadRequest(new StandardServerResponse(false, "AlloCode is required."));
            }

            if (request.EncryptedPayload == null)
            {
                return Results.BadRequest(new StandardServerResponse(false, "Backup payload is required."));
            }

            if (request.EncryptedPayload.Version <= 0 ||
                string.IsNullOrWhiteSpace(request.EncryptedPayload.Salt) ||
                string.IsNullOrWhiteSpace(request.EncryptedPayload.EncryptedData))
            {
                return Results.BadRequest(new StandardServerResponse(false, "Invalid backup payload."));
            }

            var userExists = await db.Users.AnyAsync(u => u.AlloCode.ToUpper() == cleanAlloCode);

            if (!userExists)
            {
                return Results.NotFound(new StandardServerResponse(false, "AlloCode not found."));
            }

            var payloadJson = JsonSerializer.Serialize(request.EncryptedPayload);

            var existingBackup = await db.Backups
                .FirstOrDefaultAsync(b => b.AlloCode == cleanAlloCode);

            if (existingBackup == null)
            {
                db.Backups.Add(new BackupEntity
                {
                    AlloCode = cleanAlloCode,
                    EncryptedPayloadJson = payloadJson,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            else
            {
                existingBackup.EncryptedPayloadJson = payloadJson;
                existingBackup.UpdatedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync();

            return Results.Ok(new StandardServerResponse(true, "Backup exported successfully."));
        })
        .WithName("ExportBackup")
        .WithOpenApi();

        app.MapPost("/api/backup/import", async (BackupImportRequest request, AlloChatDbContext db) =>
        {
            var cleanAlloCode = request.AlloCode?.Trim().ToUpperInvariant() ?? "";

            if (string.IsNullOrWhiteSpace(cleanAlloCode))
            {
                return Results.BadRequest(new BackupImportResponse(false, "AlloCode is required.", null));
            }

            var backup = await db.Backups
                .FirstOrDefaultAsync(b => b.AlloCode == cleanAlloCode);

            if (backup == null)
            {
                return Results.NotFound(new BackupImportResponse(false, "No backup found for this AlloCode.", null));
            }

            EncryptedBackupPayload? encryptedPayload;

            try
            {
                encryptedPayload = JsonSerializer.Deserialize<EncryptedBackupPayload>(backup.EncryptedPayloadJson);
            }
            catch
            {
                return Results.BadRequest(new BackupImportResponse(false, "Stored backup is invalid.", null));
            }

            if (encryptedPayload == null)
            {
                return Results.BadRequest(new BackupImportResponse(false, "Stored backup is empty.", null));
            }

            return Results.Ok(new BackupImportResponse(
                true,
                "Backup imported successfully.",
                encryptedPayload
            ));
        })
        .WithName("ImportBackup")
        .WithOpenApi();
    }
}