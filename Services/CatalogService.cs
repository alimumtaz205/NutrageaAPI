using Nutragea.Api.Data;
using Nutragea.Api.Services;
using Nutragea.Commerce.Contracts;
using System.Text.Json;

namespace Nutragea.Commerce.Services;
public sealed class CatalogService(Db db)
{
    public const string VariantAvailability = "SELECT v.*,p.name AS product_name,p.slug,p.published,p.category_id,COALESCE((SELECT SUM(a.quantity-a.reserved) FROM s_allocations a JOIN packaging_lots l ON l.id=a.lot_id JOIN batches b ON b.id=l.batch_id JOIN products op ON op.id=b.output_product_id WHERE a.variant_id=v.id AND b.quality='Released' AND op.is_active=1 AND l.best_before>=UTC_DATE()),0) AS available FROM s_variants v JOIN s_products p ON p.id=v.product_id";
    public async Task<object> Settings(CancellationToken ct)
    {
        var s = (await db.Query("SELECT currency,checkout_enabled,cod_enabled,support_email,support_phone,company_address FROM s_settings WHERE id=1", null, ct))[0];
        var policies = await db.Query("SELECT slug FROM s_content WHERE type='Policy' AND published=1 AND LENGTH(TRIM(body))>0 AND slug IN ('shipping','returns','privacy','terms')", null, ct);
        var shipping = await db.Query("SELECT id,name,country_code,charge FROM s_shipping WHERE is_active=1", null, ct);
        return new
        {
            currency = s.S("currency"),
            checkoutAvailable = s.B("checkout_enabled") && s.B("cod_enabled") && policies.Count == 4 && shipping.Count > 0,
            paymentMethod = "Cash on delivery",
            supportEmail = s["support_email"],
            supportPhone = s["support_phone"],
            companyAddress = s["company_address"],
            shipping
        };
    }

    public async Task<object> Products(string? search, long? category, string sort, int page, int size, bool featured, CancellationToken ct)
    {
        Rules.Require(page >= 1 && page <= 100000 && size >= 1 && size <= 48, "Invalid pagination.");
        Rules.Require((search?.Length ?? 0) <= 150, "Search is too long.");
        var order = sort switch
        {
            "price-asc" => "min_price ASC,id DESC",
            "price-desc" => "min_price DESC,id DESC",
            "name" => "name ASC",
            _ => "id DESC"
        };
        var rows = await db.Query($"SELECT p.*,c.name AS category_name,(SELECT MIN(v.price) FROM s_variants v WHERE v.product_id=p.id AND v.is_active=1) AS min_price FROM s_products p JOIN s_categories c ON c.id=p.category_id JOIN products op ON op.id=p.operational_product_id WHERE p.published=1 AND c.is_active=1 AND op.is_active=1 AND (@featured=0 OR p.featured=1) AND (@category IS NULL OR p.category_id=@category) AND (@search IS NULL OR p.name LIKE CONCAT('%',@search,'%') OR p.summary LIKE CONCAT('%',@search,'%')) ORDER BY {order} LIMIT @take OFFSET @offset", new { featured, category, search = string.IsNullOrWhiteSpace(search) ? null : search.Trim(), take = size + 1, offset = (page - 1) * size }, ct);
        var more = rows.Count > size;
        rows = rows.Take(size).ToList();
        foreach (var p in rows)
        {
            p.Remove("operational_product_id");
            p.Remove("version");
            p["images"] = await Images(ParseIds(p["image_ids"]), ct);
            p.Remove("image_ids");
        }

        return new
        {
            page,
            pageSize = size,
            hasMore = more,
            items = rows
        };
    }

    public async Task<object> Product(string slug, CancellationToken ct)
    {
        var rows = await db.Query("SELECT p.*,c.name AS category_name FROM s_products p JOIN s_categories c ON c.id=p.category_id JOIN products op ON op.id=p.operational_product_id WHERE p.slug=@slug AND p.published=1 AND c.is_active=1 AND op.is_active=1", new { slug }, ct);
        Rules.Require(rows.Count == 1, "Product not found.", 404);
        var p = rows[0];
        p.Remove("operational_product_id");
        p.Remove("version");
        p["images"] = await Images(ParseIds(p["image_ids"]), ct);
        p.Remove("image_ids");
        var variants = await db.Query(VariantAvailability + " WHERE v.product_id=@id AND v.is_active=1 ORDER BY v.price", new { id = p.L("id") }, ct);
        return new
        {
            product = p,
            variants = variants.Select(v => new { id = v.L("id"), label = v.S("label"), sku = v.S("sku"), price = v.D("price"), netWeightKg = v.D("net_weight_kg"), available = v.L("available") })
        };
    }

    public async Task<List<Dictionary<string, object?>>> Images(List<long> ids, CancellationToken ct)
    {
        var list = new List<Dictionary<string, object?>>();
        foreach (var id in ids)
        {
            var rows = await db.Query("SELECT id,file_name,alt_text FROM s_media WHERE id=@id", new { id }, ct);
            if (rows.Count > 0)
            {
                var m = rows[0];
                list.Add(new() { { "id", id }, { "url", "/capi/media/" + m.S("file_name") }, { "alt", m.S("alt_text") } });
            }
        }

        return list;
    }

    public static List<long> ParseIds(object? value) => JsonSerializer.Deserialize<List<long>>(Convert.ToString(value) ?? "[]") ?? [];
    public async Task<object> Content(string? type, CancellationToken ct)
    {
        var rows = await db.Query("SELECT * FROM s_content WHERE published=1 AND (@type IS NULL OR type=@type) ORDER BY updated_at DESC LIMIT 200", new { type }, ct);
        foreach (var row in rows)
        {
            row["images"] = await Images(ParseIds(row["image_ids"]), ct);
            row.Remove("image_ids");
        }

        return rows;
    }

    public async Task<object> ContentDetail(string slug, CancellationToken ct)
    {
        var rows = await db.Query("SELECT * FROM s_content WHERE slug=@slug AND published=1", new { slug }, ct);
        Rules.Require(rows.Count == 1, "Page not found.", 404);
        var row = rows[0];
        row["images"] = await Images(ParseIds(row["image_ids"]), ct);
        row.Remove("image_ids");
        return row;
    }

    public static async Task MediaCheck(Sql sql, List<long> ids, bool documentary)
    {
        Rules.Require(ids.Distinct().Count() == ids.Count, "Duplicate media IDs.");
        foreach (var id in ids)
        {
            var m = await sql.One("SELECT * FROM s_media WHERE id=@id", new { id });
            Rules.Require(m.B("rights_confirmed") && (!documentary || (m.B("consent_confirmed") && m.B("is_documentary"))), "Published programme/story/event photographs require confirmed rights, consent, and documentary status.");
        }
    }

    public async Task<object> SaveProduct(long actor, long? id, StoreProductRequest r, CancellationToken ct) => await db.Write<object>(async sql =>
    {
        await sql.One("SELECT id FROM s_categories WHERE id=@id", new { id = r.CategoryId });
        await sql.One("SELECT id FROM products WHERE id=@id", new { id = r.OperationalProductId });
        if (r.Published)
        {
            Rules.Require(r.ImageIds.Count > 0, "Add an approved product photograph before publication.");
            await MediaCheck(sql, r.ImageIds, false);
        }

        long result;
        if (id.HasValue)
        {
            var old = await sql.One("SELECT * FROM s_products WHERE id=@id", new { id });
            Rules.Require(old.L("version") == r.Version, "This product changed. Reload before saving.", 409);
            Rules.Require(old.L("operational_product_id") == r.OperationalProductId, "Operational product mapping is immutable.");
            await sql.Exec("UPDATE s_products SET name=@Name,slug=@Slug,category_id=@CategoryId,summary=@Summary,description=@Description,origin=@Origin,ingredients=@Ingredients,allergens=@Allergens,storage=@Storage,image_ids=@images,published=@Published,featured=@Featured,version=version+1 WHERE id=@id", new { id, r.Name, r.Slug, r.CategoryId, r.Summary, r.Description, r.Origin, r.Ingredients, r.Allergens, r.Storage, images = JsonSerializer.Serialize(r.ImageIds), r.Published, r.Featured });
            result = id.Value;
        }
        else
            result = await sql.Insert("INSERT INTO s_products(name,slug,category_id,operational_product_id,summary,description,origin,ingredients,allergens,storage,image_ids,published,featured) VALUES(@Name,@Slug,@CategoryId,@OperationalProductId,@Summary,@Description,@Origin,@Ingredients,@Allergens,@Storage,@images,@Published,@Featured)", new { r.Name, r.Slug, r.CategoryId, r.OperationalProductId, r.Summary, r.Description, r.Origin, r.Ingredients, r.Allergens, r.Storage, images = JsonSerializer.Serialize(r.ImageIds), r.Published, r.Featured });
        await sql.Audit(actor, "StoreProductSaved", "StoreProduct", result);
        return new
        {
            id = result
        };
    }, ct);
    public async Task<object> SaveVariant(long actor, long? id, VariantRequest r, CancellationToken ct) => await db.Write<object>(async sql =>
    {
        Rules.Weight(r.NetWeightKg);
        Rules.Require(decimal.Round(r.Price, 2) == r.Price, "Prices support two decimal places.");
        await sql.One("SELECT id FROM s_products WHERE id=@id", new { id = r.ProductId });
        long result;
        if (id.HasValue)
        {
            var old = await sql.One("SELECT * FROM s_variants WHERE id=@id", new { id });
            Rules.Require(old.L("version") == r.Version, "Variant changed. Reload first.", 409);
            Rules.Require(old.L("product_id") == r.ProductId && old.D("net_weight_kg") == r.NetWeightKg, "Variant product and pack size are immutable.");
            await sql.Exec("UPDATE s_variants SET label=@Label,sku=@Sku,price=@Price,is_active=@IsActive,version=version+1 WHERE id=@id", new { id, r.Label, r.Sku, r.Price, r.IsActive });
            result = id.Value;
        }
        else
            result = await sql.Insert("INSERT INTO s_variants(product_id,label,sku,price,net_weight_kg,is_active) VALUES(@ProductId,@Label,@Sku,@Price,@NetWeightKg,@IsActive)", r);
        await sql.Audit(actor, "VariantSaved", "StoreVariant", result);
        return new
        {
            id = result
        };
    }, ct);
    public async Task<object> SaveContent(long actor, long? id, ContentRequest r, CancellationToken ct) => await db.Write<object>(async sql =>
    {
        Rules.Require(!r.Published || (!string.IsNullOrWhiteSpace(r.Body) && !string.IsNullOrWhiteSpace(r.Summary)), "Published content requires a summary and body.");
        if (r.Published)
            await MediaCheck(sql, r.ImageIds, r.Type is "Programme" or "Story" or "Event");
        if (id.HasValue)
        {
            var old = await sql.One("SELECT * FROM s_content WHERE id=@id", new { id });
            Rules.Require(old.L("version") == r.Version, "Content changed. Reload first.", 409);
            if (old.S("type") == "Policy")
                Rules.Require(old.S("slug") == r.Slug && r.Type == "Policy", "Policy identity is immutable.");
        }

        var values = new
        {
            id,
            r.Type,
            r.Slug,
            r.Title,
            r.Summary,
            r.Body,
            r.ProgrammeStatus,
            date = r.EventDate?.UtcDateTime,
            images = JsonSerializer.Serialize(r.ImageIds),
            r.Published
        };
        long result;
        if (id.HasValue)
        {
            await sql.Exec("UPDATE s_content SET type=@Type,slug=@Slug,title=@Title,summary=@Summary,body=@Body,programme_status=@ProgrammeStatus,event_date=@date,image_ids=@images,published=@Published,version=version+1,updated_at=UTC_TIMESTAMP(6) WHERE id=@id", values);
            result = id.Value;
        }
        else
            result = await sql.Insert("INSERT INTO s_content(type,slug,title,summary,body,programme_status,event_date,image_ids,published) VALUES(@Type,@Slug,@Title,@Summary,@Body,@ProgrammeStatus,@date,@images,@Published)", values);
        await sql.Audit(actor, "ContentSaved", "Content", result);
        return new
        {
            id = result
        };
    }, ct);
}
