using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SportsCenter.API.Controllers;

// Uploads images/videos to Cloudinary and returns the public URL. Keeps the API secret server-side only.
[ApiController, Authorize]
[Route("api/media")]
public class MediaController(Cloudinary cloudinary) : ControllerBase
{
    private static readonly string[] AllowedTypes = ["image/jpeg", "image/png", "image/webp", "image/gif", "video/mp4", "video/webm"];
    private const long MaxBytes = 20 * 1024 * 1024;

    [HttpPost("upload")]
    [RequestSizeLimit(MaxBytes)]
    public async Task<ActionResult<UploadResult>> Upload(IFormFile file)
    {
        if (file.Length == 0) return BadRequest(new { error = "File rỗng." });
        if (file.Length > MaxBytes) return BadRequest(new { error = "File vượt quá 20MB." });
        if (!AllowedTypes.Contains(file.ContentType)) return BadRequest(new { error = "Chỉ chấp nhận ảnh (jpg/png/webp/gif) hoặc video (mp4/webm)." });

        var isVideo = file.ContentType.StartsWith("video/");
        await using var stream = file.OpenReadStream();
        RawUploadResult result = isVideo
            ? await cloudinary.UploadAsync(new VideoUploadParams { File = new FileDescription(file.FileName, stream), Folder = "sports-center" })
            : await cloudinary.UploadAsync(new ImageUploadParams { File = new FileDescription(file.FileName, stream), Folder = "sports-center" });

        if (result.Error != null) return StatusCode(502, new { error = result.Error.Message });
        return new UploadResult(result.SecureUrl.ToString());
    }

    public record UploadResult(string Url);
}
