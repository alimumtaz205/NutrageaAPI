using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nutragea.Api.Data;
using Nutragea.Commerce.Contracts;
using Nutragea.Commerce.Services;

namespace Nutragea.Commerce.Controllers;
[ApiController, Route("capi/admin"), Authorize(Roles = "Admin")]
public sealed class AdminController(CatalogService catalog, CommerceService commerce, AdminService admin, MediaService media, Db db) : ControllerBase
{
    long Actor => long.Parse(User.FindFirst("sub")!.Value);

    [HttpGet("settings")]
    public Task<object> Settings(CancellationToken ct) => admin.Settings(ct);
    [HttpPut("settings")]
    public async Task<object> Settings(SettingsRequest r, CancellationToken ct)
    {
        await admin.SaveSettings(Actor, r, ct);
        return new
        {
            message = "Saved"
        };
    }

    [HttpGet("{kind}")]
    public Task<object> List(string kind, CancellationToken ct, int page = 1) => admin.List(kind, page, ct);
    [HttpGet("orders/{id:long}")]
    public Task<object> Order(long id, CancellationToken ct) => admin.Order(id, ct);
    [HttpPost("orders/{id:long}/actions")]
    public async Task<object> OrderAction(long id, OrderActionRequest r, CancellationToken ct)
    {
        await commerce.Action(Actor, id, r, ct);
        return new
        {
            message = "Updated"
        };
    }

    [HttpPost("orders/expire")]
    public async Task<object> Expire(CancellationToken ct)
    {
        await commerce.Expire(ct);
        return new
        {
            message = "Expired reservations released"
        };
    }

    [HttpPost("products")]
    public async Task<IActionResult> Product(StoreProductRequest r, CancellationToken ct) => StatusCode(201, await catalog.SaveProduct(Actor, null, r, ct));
    [HttpPut("products/{id:long}")]
    public Task<object> Product(long id, StoreProductRequest r, CancellationToken ct) => catalog.SaveProduct(Actor, id, r, ct);
    [HttpPost("variants")]
    public async Task<IActionResult> Variant(VariantRequest r, CancellationToken ct) => StatusCode(201, await catalog.SaveVariant(Actor, null, r, ct));
    [HttpPut("variants/{id:long}")]
    public Task<object> Variant(long id, VariantRequest r, CancellationToken ct) => catalog.SaveVariant(Actor, id, r, ct);
    [HttpPost("categories")]
    public async Task<IActionResult> Category(CategoryRequest r, CancellationToken ct) => StatusCode(201, await admin.Category(Actor, null, r, ct));
    [HttpPut("categories/{id:long}")]
    public Task<object> Category(long id, CategoryRequest r, CancellationToken ct) => admin.Category(Actor, id, r, ct);
    [HttpPost("shipping")]
    public async Task<IActionResult> Shipping(ShippingRequest r, CancellationToken ct) => StatusCode(201, await admin.Shipping(Actor, null, r, ct));
    [HttpPut("shipping/{id:long}")]
    public Task<object> Shipping(long id, ShippingRequest r, CancellationToken ct) => admin.Shipping(Actor, id, r, ct);
    [HttpPost("content")]
    public async Task<IActionResult> Content(ContentRequest r, CancellationToken ct) => StatusCode(201, await catalog.SaveContent(Actor, null, r, ct));
    [HttpPut("content/{id:long}")]
    public Task<object> Content(long id, ContentRequest r, CancellationToken ct) => catalog.SaveContent(Actor, id, r, ct);
    [HttpPost("allocations")]
    public async Task<IActionResult> Allocate(AllocationRequest r, CancellationToken ct) => StatusCode(201, await commerce.Allocate(Actor, r, ct));
    [HttpPost("allocations/{id:long}/release")]
    public async Task<object> Release(long id, ReleaseRequest r, CancellationToken ct)
    {
        await commerce.Release(Actor, id, r, ct);
        return new
        {
            message = "Returned"
        };
    }

    [HttpPut("enquiries/{id:long}")]
    public async Task<object> Enquiry(long id, EnquiryStatusRequest r, CancellationToken ct)
    {
        await admin.EnquiryStatus(Actor, id, r, ct);
        return new
        {
            message = "Updated"
        };
    }

    [HttpPost("media"), Consumes("multipart/form-data"), RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<IActionResult> Upload(IFormFile file, [FromForm] string alt, [FromForm] bool rightsConfirmed, [FromForm] bool consentConfirmed, [FromForm] bool isDocumentary, CancellationToken ct) => StatusCode(201, await media.Upload(Actor, file, alt, rightsConfirmed, consentConfirmed, isDocumentary, ct));
    [HttpGet("media/{id:long}/preview")]
    public async Task<IActionResult> Preview(long id, CancellationToken ct)
    {
        var rows = await db.Query("SELECT file_name,content_type FROM s_media WHERE id=@id", new { id }, ct);
        if (rows.Count == 0)
            return NotFound();
        return PhysicalFile(Path.Combine(media.Root, rows[0].S("file_name")), rows[0].S("content_type"));
    }
}
