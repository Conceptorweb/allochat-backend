using System;

public class BackupEntity
{
    public string AlloCode { get; set; } = "";
    public string UserID { get; set; } = "";
    public string EncryptedPayloadJson { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}

public record BackupExportRequest(
    string AlloCode,
    string UserID,
    EncryptedBackupPayload EncryptedPayload
);

public record BackupImportRequest(
    string AlloCode,
    string UserID
);

public record BackupImportResponse(
    bool Success,
    string Message,
    EncryptedBackupPayload? EncryptedPayload
);

public record EncryptedBackupPayload(
    int Version,
    string Salt,
    string EncryptedData
);
