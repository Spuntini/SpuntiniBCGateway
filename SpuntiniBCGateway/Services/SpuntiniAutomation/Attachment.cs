
using Microsoft.AspNetCore.StaticFiles;

namespace SpuntiniBCGateway;

public class Attachment
{
    public string FileName { get; set; } = string.Empty;
    public byte[] FileContent { get; set; } = [];
    public string DocumentDescription { get; set; } = string.Empty;

    public string GetContentType()
    {
        var provider = new FileExtensionContentTypeProvider();
        if (provider.TryGetContentType(FileName, out var contentType))
            return contentType;

        // Fallback wanneer extensie onbekend is:
        return "application/octet-stream";
    }
}