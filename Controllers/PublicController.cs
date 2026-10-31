using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nutragea.Api.Data;
using Nutragea.Commerce.Contracts;
using Nutragea.Commerce.Services;

namespace Nutragea.Commerce.Controllers;
[ApiController, Route("capi"), AllowAnonymous]
public sealed class PublicController(CatalogService catalog, CommerceService commerce, AdminService admin, Db db) : ControllerBase
{
    [HttpGet("settings")]
    public Task<object> Settings(CancellationToken ct) => catalog.Settings(ct);
    [HttpGet("categories")]
    public Task<List<Dictionary<string, object?>>> Categories(CancellationToken ct) => db.Query("SELECT id,name,slug FROM s_categories WHERE is_active=1 ORDER BY name", null, ct);
    [HttpGet("products")]
    public Task<object> Products(CancellationToken ct, string? search = null, long? category = null, string sort = "newest", int page = 1, int pageSize = 12, bool featured = false) => catalog.Products(search, category, sort, page, pageSize, featured, ct);
    [HttpGet("products/{slug}")]
    public Task<object> Product(string slug, CancellationToken ct) => catalog.Product(slug, ct);
    [HttpGet("content")]
    public Task<object> Content(CancellationToken ct, string? type = null) => catalog.Content(type, ct);
    [HttpGet("content/{slug}")]
    public Task<object> ContentDetail(string slug, CancellationToken ct) => catalog.ContentDetail(slug, ct);
    [HttpPost("cart"), EnableRateLimiting("public-write")]
    public async Task<object> Cart(CartRequest r, CancellationToken ct)
    {
        var result = new List<object>();
        foreach (var i in r.Items)
        {
            var rows = await db.Query(CatalogService.VariantAvailability + " JOIN s_categories c ON c.id=p.category_id JOIN products op ON op.id=p.operational_product_id WHERE v.id=@id AND v.is_active=1 AND p.published=1 AND c.is_active=1 AND op.is_active=1", new { id = i.VariantId }, ct);
            if (rows.Count == 0)
            {
                result.Add(new { variantId = i.VariantId, quantity = i.Quantity, product = "Product unavailable", label = "", slug = "", price = (decimal? )null, available = 0, image = (object? )null });
                continue;
            }

            var v = rows[0];
            var product = await db.Query("SELECT image_ids FROM s_products WHERE id=@id", new { id = v.L("product_id") }, ct);
            var images = await catalog.Images(CatalogService.ParseIds(product[0]["image_ids"]), ct);
            result.Add(new { variantId = i.VariantId, quantity = i.Quantity, product = v.S("product_name"), label = v.S("label"), slug = v.S("slug"), price = (decimal? )v.D("price"), available = v.L("available"), image = (object? )images.FirstOrDefault() });
        }

        return result;
    }

    [HttpGet("sitemap")]
    public async Task<IActionResult> Sitemap(CancellationToken ct)
    {
        var origin = HttpContext.RequestServices.GetRequiredService<IConfiguration>()["Commerce:PublicOrigin"] ?? "http://localhost:4300";
        var urls = new List<string>
        {
            "/",
            "/shop",
            "/about",
            "/impact",
            "/stories",
            "/wholesale",
            "/contact"
        };
        foreach (var p in await db.Query("SELECT p.slug FROM s_products p JOIN s_categories c ON c.id=p.category_id JOIN products op ON op.id=p.operational_product_id WHERE p.published=1 AND c.is_active=1 AND op.is_active=1 LIMIT 10000", null, ct))
            urls.Add("/shop/" + p.S("slug"));
        foreach (var p in await db.Query("SELECT slug,type FROM s_content WHERE published=1 LIMIT 10000", null, ct))
            urls.Add((p.S("type") == "Programme" ? "/impact/" : p.S("type")is "Story" or "Event" ? "/stories/" : "/pages/") + p.S("slug"));
        var xml = new System.Xml.Linq.XDocument(new System.Xml.Linq.XElement(System.Xml.Linq.XName.Get("urlset", "http://www.sitemaps.org/schemas/sitemap/0.9"), urls.Select(path => new System.Xml.Linq.XElement(System.Xml.Linq.XName.Get("url", "http://www.sitemaps.org/schemas/sitemap/0.9"), new System.Xml.Linq.XElement(System.Xml.Linq.XName.Get("loc", "http://www.sitemaps.org/schemas/sitemap/0.9"), new Uri(new Uri(origin), path).AbsoluteUri)))));
        return Content(xml.ToString(), "application/xml");
    }

    [HttpPost("quote"), EnableRateLimiting("public-write")]
    public async Task<Quote> Quote(QuoteRequest r, CancellationToken ct)
    {
        await commerce.Expire(ct);
        return await commerce.Quote(r, ct);
    }

    [HttpPost("orders"), EnableRateLimiting("public-write")]
    public async Task<IActionResult> Place(OrderRequest r, CancellationToken ct) => StatusCode(201, await commerce.Place(r, ct));
    [HttpPost("orders/lookup"), EnableRateLimiting("public-write")]
    public Task<object> Lookup(LookupRequest r, CancellationToken ct) => commerce.Lookup(r, ct);
    [HttpPost("enquiries"), EnableRateLimiting("public-write")]
    public async Task<IActionResult> Enquiry(EnquiryRequest r, CancellationToken ct) => StatusCode(201, await admin.Enquiry(r, ct));
}
