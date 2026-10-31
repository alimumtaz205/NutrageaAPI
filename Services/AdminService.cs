using Nutragea.Api.Data;
using Nutragea.Api.Services;
using Nutragea.Commerce.Contracts;

namespace Nutragea.Commerce.Services;
public sealed class AdminService(Db db)
{
    public static readonly Dictionary<string, string> Tables = new()
    {
        ["products"] = "s_products",
        ["variants"] = "s_variants",
        ["categories"] = "s_categories",
        ["content"] = "s_content",
        ["shipping"] = "s_shipping",
        ["enquiries"] = "s_enquiries",
        ["media"] = "s_media"
    };
    public async Task<object> List(string kind, int page, CancellationToken ct)
    {
        Rules.Require(page >= 1 && page <= 100000, "Invalid page.");
        string query;
        if (kind == "orders")
            query = "SELECT id,reference,name,email,phone,city,total,currency,status,payment_status,expires_at,created_at FROM s_orders";
        else if (kind == "allocations")
            query = "SELECT a.*,v.label,v.sku,p.name,l.code,b.quality,l.best_before FROM s_allocations a JOIN s_variants v ON v.id=a.variant_id JOIN s_products p ON p.id=v.product_id JOIN packaging_lots l ON l.id=a.lot_id JOIN batches b ON b.id=l.batch_id";
        else if (kind == "operational-products")
            query = "SELECT id,name,stage,is_active FROM products";
        else if (kind == "lots")
            query = "SELECT l.id,l.code,l.available_packs,l.net_weight_kg,l.best_before,b.quality,b.output_product_id FROM packaging_lots l JOIN batches b ON b.id=l.batch_id";
        else
        {
            Rules.Require(Tables.ContainsKey(kind), "Unknown collection.", 404);
            query = "SELECT * FROM " + Tables[kind];
        }

        var rows = await db.Query($"SELECT * FROM ({query}) x ORDER BY id DESC LIMIT 101 OFFSET @offset", new { offset = (page - 1) * 100 }, ct);
        return new
        {
            items = rows.Take(100),
            hasMore = rows.Count > 100,
            page
        };
    }

    public async Task<object> Order(long id, CancellationToken ct)
    {
        var rows = await db.Query("SELECT id,reference,name,email,phone,address,city,postal_code,country_code,total,subtotal,shipping,discount,currency,status,payment_status,expires_at,created_at,dispatch_id,policy_snapshot FROM s_orders WHERE id=@id", new { id }, ct);
        Rules.Require(rows.Count == 1, "Order not found.", 404);
        return new
        {
            order = rows[0],
            events = await db.Query("SELECT event,details,actor_id,created_at FROM s_order_events WHERE order_id=@id ORDER BY id", new { id }, ct),
            items = await db.Query("SELECT product_name,variant_label,quantity,unit_price,line_total FROM s_order_items WHERE order_id=@id", new { id }, ct)
        };
    }

    public async Task<object> Category(long actor, long? id, CategoryRequest r, CancellationToken ct) => await db.Write<object>(async sql =>
    {
        long result;
        if (id.HasValue)
        {
            await Version(sql, "s_categories", id.Value, r.Version);
            await sql.Exec("UPDATE s_categories SET name=@Name,slug=@Slug,is_active=@IsActive,version=version+1 WHERE id=@id", new { id, r.Name, r.Slug, r.IsActive });
            result = id.Value;
        }
        else
            result = await sql.Insert("INSERT INTO s_categories(name,slug,is_active) VALUES(@Name,@Slug,@IsActive)", r);
        await sql.Audit(actor, "CategorySaved", "StoreCategory", result);
        return new
        {
            id = result
        };
    }, ct);
    public async Task<object> Shipping(long actor, long? id, ShippingRequest r, CancellationToken ct) => await db.Write<object>(async sql =>
    {
        Rules.Require(decimal.Round(r.Charge, 2) == r.Charge, "Use two decimal places for delivery charges.");
        long result;
        if (id.HasValue)
        {
            await Version(sql, "s_shipping", id.Value, r.Version);
            await sql.Exec("UPDATE s_shipping SET name=@Name,country_code=@CountryCode,charge=@Charge,is_active=@IsActive,version=version+1 WHERE id=@id", new { id, r.Name, r.CountryCode, r.Charge, r.IsActive });
            result = id.Value;
        }
        else
            result = await sql.Insert("INSERT INTO s_shipping(name,country_code,charge,is_active) VALUES(@Name,@CountryCode,@Charge,@IsActive)", r);
        await sql.Audit(actor, "ShippingSaved", "Shipping", result);
        return new
        {
            id = result
        };
    }, ct);
    public async Task<object> Settings(CancellationToken ct) => (await db.Query("SELECT * FROM s_settings WHERE id=1", null, ct))[0];
    public async Task SaveSettings(long actor, SettingsRequest r, CancellationToken ct) => await db.Write(async sql =>
    {
        await Version(sql, "s_settings", 1, r.Version);
        if (r.CheckoutEnabled)
        {
            Rules.Require(r.CodEnabled, "Enable COD before opening checkout.");
            var policies = await sql.Rows("SELECT id FROM s_content WHERE type='Policy' AND slug IN ('shipping','returns','privacy','terms') AND published=1 AND LENGTH(TRIM(body))>0");
            Rules.Require(policies.Count == 4, "Publish your four approved policy pages before enabling checkout.");
            var shipping = await sql.Rows("SELECT id FROM s_shipping WHERE is_active=1");
            Rules.Require(shipping.Count > 0, "Configure an active shipping option first.");
            Rules.Require(!string.IsNullOrWhiteSpace(r.SupportEmail) && !string.IsNullOrWhiteSpace(r.CompanyAddress), "Provide a real support email and business address before opening checkout.");
        }

        await sql.Exec("UPDATE s_settings SET checkout_enabled=@CheckoutEnabled,cod_enabled=@CodEnabled,support_email=@SupportEmail,support_phone=@SupportPhone,company_address=@CompanyAddress,version=version+1 WHERE id=1", r);
        await sql.Audit(actor, "StoreSettingsSaved", "StoreSettings", 1);
        return true;
    }, ct);
    static async Task Version(Sql sql, string table, long id, int version)
    {
        var old = await sql.One($"SELECT version FROM {table} WHERE id=@id", new { id });
        Rules.Require(old.L("version") == version, "This record changed. Reload before saving.", 409);
    }

    public async Task EnquiryStatus(long actor, long id, EnquiryStatusRequest r, CancellationToken ct) => await db.Write(async sql =>
    {
        await sql.One("SELECT id FROM s_enquiries WHERE id=@id", new { id });
        await sql.Exec("UPDATE s_enquiries SET status=@Status WHERE id=@id", new { id, r.Status });
        await sql.Audit(actor, "EnquiryStatusChanged", "Enquiry", id, new { r.Status });
        return true;
    }, ct);
    public async Task<object> Enquiry(EnquiryRequest r, CancellationToken ct)
    {
        Rules.Require(string.IsNullOrEmpty(r.Website), "Unable to accept this enquiry.");
        if (r.Type == "Wholesale")
            Rules.Require(!string.IsNullOrWhiteSpace(r.Business) && !string.IsNullOrWhiteSpace(r.Country) && !string.IsNullOrWhiteSpace(r.Products) && !string.IsNullOrWhiteSpace(r.Quantity), "Wholesale enquiries require business, country, products, and approximate quantity.");
        return await db.Write<object>(async sql =>
        {
            await sql.Insert("INSERT INTO s_enquiries(type,name,email,business,country,products,quantity,message) VALUES(@Type,@Name,@Email,@Business,@Country,@Products,@Quantity,@Message)", r);
            return new
            {
                message = "Your enquiry has been received."
            };
        }, ct);
    }
}
