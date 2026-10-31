using Nutragea.Api.Data;
using Nutragea.Api.Services;
using System.Buffers.Binary;

namespace Nutragea.Commerce.Services;
public sealed class MediaService(Db db, IConfiguration config, IWebHostEnvironment environment)
{
    public string Root => Path.GetFullPath(Path.Combine(environment.ContentRootPath, config["Commerce:DataDirectory"] ?? "Data", "media"));

    public async Task<object> Upload(long actor, IFormFile file, string alt, bool rights, bool consent, bool documentary, CancellationToken ct)
    {
        Rules.Require(rights && !string.IsNullOrWhiteSpace(alt) && alt.Length <= 250, "Confirm image rights and provide meaningful alt text.");
        Rules.Require(file.Length > 0 && file.Length <= 5 * 1024 * 1024, "Images must be 1 byte to 5 MB.");
        using var input = new MemoryStream();
        await file.CopyToAsync(input, ct);
        var bytes = input.ToArray();
        string ext, type;
        if (bytes.Length >= 24 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            ext = ".png";
            type = "image/png";
            var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
            var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
            Rules.Require(width > 0 && height > 0 && (ulong)width * height <= 32000000, "Image dimensions exceed the supported limit.");
            bytes = StripPng(bytes);
        }
        else if (bytes.Length > 4 && bytes[0] == 255 && bytes[1] == 216 && bytes[^2] == 255 && bytes[^1] == 217)
        {
            ext = ".jpg";
            type = "image/jpeg";
            bytes = StripJpeg(bytes);
        }
        else
            throw new ApiException(400, "Only PNG and JPEG images are supported. SVG and other formats are not accepted.");
        Directory.CreateDirectory(Root);
        var name = Guid.NewGuid().ToString("N") + ext;
        var path = Path.Combine(Root, name);
        await File.WriteAllBytesAsync(path, bytes, ct);
        try
        {
            return await db.Write<object>(async sql =>
            {
                var id = await sql.Insert("INSERT INTO s_media(file_name,content_type,alt_text,rights_confirmed,consent_confirmed,is_documentary,created_by) VALUES(@name,@type,@alt,@rights,@consent,@documentary,@actor)", new { name, type, alt, rights, consent, documentary, actor });
                await sql.Audit(actor, "MediaUploaded", "StoreMedia", id);
                return new
                {
                    id,
                    fileName = name,
                    alt
                };
            }, ct);
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    static byte[] StripPng(byte[] bytes)
    {
        using var output = new MemoryStream();
        output.Write(bytes, 0, 8);
        var at = 8;
        bool ended = false;
        while (at + 12 <= bytes.Length)
        {
            var len = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at, 4));
            Rules.Require(len <= bytes.Length - at - 12, "Invalid PNG structure.");
            var length = (int)len + 12;
            var name = System.Text.Encoding.ASCII.GetString(bytes, at + 4, 4);
            if (name is not ("eXIf" or "tEXt" or "iTXt" or "zTXt"))
                output.Write(bytes, at, length);
            at += length;
            if (name == "IEND")
            {
                ended = true;
                break;
            }
        }

        Rules.Require(ended, "Invalid PNG structure.");
        return output.ToArray();
    }

    static byte[] StripJpeg(byte[] bytes)
    {
        using var output = new MemoryStream();
        output.Write(bytes, 0, 2);
        int at = 2;
        bool dimensions = false;
        while (at + 1 < bytes.Length)
        {
            Rules.Require(bytes[at] == 255, "Invalid JPEG marker.");
            var start = at;
            while (at < bytes.Length && bytes[at] == 255)
                at++;
            Rules.Require(at < bytes.Length, "Invalid JPEG.");
            var marker = bytes[at++];
            if (marker == 0xDA)
            {
                Rules.Require(dimensions, "JPEG dimensions could not be validated.");
                output.Write(bytes, start, bytes.Length - start);
                return output.ToArray();
            }

            Rules.Require(at + 2 <= bytes.Length, "Invalid JPEG.");
            int length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at, 2));
            Rules.Require(length >= 2 && at + length <= bytes.Length, "Invalid JPEG segment.");
            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                Rules.Require(length >= 7, "Invalid JPEG dimensions.");
                var h = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at + 3, 2));
                var w = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at + 5, 2));
                Rules.Require(w > 0 && h > 0 && (long)w * h <= 32000000, "Image dimensions exceed the supported limit.");
                dimensions = true;
            }

            if (marker is not (0xE1 or 0xED or 0xFE))
                output.Write(bytes, start, at + length - start);
            at += length;
        }

        throw new ApiException(400, "Invalid JPEG.");
    }
}
