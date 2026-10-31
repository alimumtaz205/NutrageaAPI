using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nutragea.Api.Data;
using Nutragea.Commerce.Services;

namespace Nutragea.Commerce.Controllers;
[ApiController, Route("capi/media"), AllowAnonymous]
public sealed class MediaController(Db db, MediaService media) : ControllerBase
{
    [HttpGet("{name}")]
    public async Task<IActionResult> Get(string name, CancellationToken ct)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-f0-9]{32}\.(png|jpg)$"))
            return NotFound();
        var rows = await db.Query("SELECT m.file_name,m.content_type FROM s_media m WHERE m.file_name=@name AND (EXISTS(SELECT 1 FROM s_products p WHERE p.published=1 AND JSON_CONTAINS(p.image_ids,CAST(m.id AS JSON))) OR EXISTS(SELECT 1 FROM s_content c WHERE c.published=1 AND JSON_CONTAINS(c.image_ids,CAST(m.id AS JSON))))", new { name }, ct);
        if (rows.Count == 0)
            return NotFound();
        return PhysicalFile(Path.Combine(media.Root, name), rows[0].S("content_type"));
    }
}
