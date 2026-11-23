using StemCode.Application.Models;
using System.Text;

namespace StemCode.Desktop.Services;

/// <summary>Builds conversation attachments from files or raw image bytes, mirroring the CLI rules.</summary>
public static class AttachmentFactory
{
    public const long MaxAttachmentBytes = 20 * 1024 * 1024;

    private static readonly Dictionary<string, string> MediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".pdf"] = "application/pdf",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".md"] = "text/markdown",
        [".csv"] = "text/csv"
    };

    public static bool TryCreateFromFile(string path, out ConversationAttachment? attachment, out string? error)
    {
        attachment = null;
        error = null;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception)
        {
            error = $"Invalid attachment path: {exception.Message}";
            return false;
        }

        FileInfo file = new(fullPath);
        if (!file.Exists)
        {
            error = $"Attachment not found: {path}";
            return false;
        }

        if (file.Length > MaxAttachmentBytes)
        {
            error = $"Skipped '{file.Name}': larger than 20 MB.";
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(fullPath);
        }
        catch (Exception exception)
        {
            error = $"Could not read '{file.Name}': {exception.Message}";
            return false;
        }

        string base64 = Convert.ToBase64String(bytes.Length == 0 ? [0x20] : bytes);
        string mediaType = MediaTypes.TryGetValue(file.Extension, out string? known) ? known : "application/octet-stream";

        if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mediaType, "application/pdf", StringComparison.OrdinalIgnoreCase) &&
            TryDecodeText(bytes, out string? text))
        {
            attachment = new ConversationAttachment(
                file.Name,
                mediaType == "application/octet-stream" ? "text/plain" : mediaType,
                base64,
                text);
            return true;
        }

        attachment = new ConversationAttachment(file.Name, mediaType, base64);
        return true;
    }

    public static ConversationAttachment CreateImage(string name, byte[] pngBytes)
    {
        return new ConversationAttachment(name, "image/png", Convert.ToBase64String(pngBytes));
    }

    private static bool TryDecodeText(byte[] bytes, out string? text)
    {
        text = null;
        if (bytes.Length == 0)
        {
            text = string.Empty;
            return false;
        }

        int sample = Math.Min(bytes.Length, 8192);
        for (int index = 0; index < sample; index++)
        {
            if (bytes[index] == 0)
            {
                return false;
            }
        }

        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            return !string.IsNullOrWhiteSpace(text);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
