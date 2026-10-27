namespace OmniLink.Core.Events;

/// <summary>
/// Event แจ้งความคืบหน้าการส่งถ่ายไฟล์ข้ามเครื่อง (Observer Pattern ผ่าน IOmniEventHub)
/// </summary>
public record FileTransferProgressEvent(
    string TransferId,
    string FileName,
    long BytesTransferred,
    long TotalBytes,
    double Percentage
);

/// <summary>
/// Event แจ้งผลลัพธ์การส่งถ่ายไฟล์ข้ามเครื่องเสร็จสิ้น
/// </summary>
public record FileTransferCompletedEvent(
    string TransferId,
    bool Success,
    int FileCount,
    long TotalBytes,
    string DestinationPath,
    string? Error
);
