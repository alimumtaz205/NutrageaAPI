using Nutragea.Api.Data;
using Nutragea.Api.Services;
using Nutragea.Api.Security;
using Nutragea.Commerce.Contracts;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nutragea.Commerce.Services;
public interface IPaymentProvider
{
    string Method { get; }

    string InitialStatus { get; }
}

public sealed class CodPaymentProvider : IPaymentProvider
{
    public string Method => "Cash on delivery";
    public string InitialStatus => "DueOnDelivery";
}

public sealed record QuoteLine(long VariantId, string Product, string Label, int Quantity, decimal UnitPrice, decimal LineTotal);
public sealed record Quote(List<QuoteLine> Items, decimal Subtotal, decimal Shipping, decimal Discount, decimal Total, string Currency, string PolicyRevision);
public sealed class CommerceService(Db db, IDataProtectionProvider protection, IConfiguration configuration, IPaymentProvider payment)
{
    private readonly IDataProtector protector = protection.CreateProtector("Nutragea.Commerce.OrderAccess.v1");
    static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    static async Task Move(Sql sql, long actor, long lot, int qty, string reason, string reference, long id) => await sql.Exec("INSERT INTO stock_movements(entity,entity_id,quantity,unit,reason,reference_type,reference_id,actor_id) VALUES('Lot',@lot,@qty,'Pack',@reason,@reference,@id,@actor)", new { actor, lot, qty, reason, reference, id });
    public async Task<object> Allocate(long actor, AllocationRequest r, CancellationToken ct) => await db.Write<object>(async sql =>
    {
        var v = await sql.One("SELECT v.*,p.operational_product_id FROM s_variants v JOIN s_products p ON p.id=v.product_id WHERE v.id=@id", new { id = r.VariantId });
        var l = await sql.One("SELECT l.*,b.quality,b.output_product_id,p.is_active FROM packaging_lots l JOIN batches b ON b.id=l.batch_id JOIN products p ON p.id=b.output_product_id WHERE l.id=@id", new { id = r.LotId });
        Rules.Require(v.B("is_active") && l.B("is_active") && l.S("quality") == "Released" && ((DateTime)l["best_before"]!).Date >= DateTime.UtcNow.Date, "Only active, released, nonexpired stock can be allocated.", 409);
        Rules.Require(l.L("output_product_id") == v.L("operational_product_id") && l.D("net_weight_kg") == v.D("net_weight_kg"), "Lot product/pack size must exactly match the storefront variant.");
        Rules.Available(l.D("available_packs"), r.Quantity);
        await sql.Exec("UPDATE packaging_lots SET available_packs=available_packs-@qty WHERE id=@id", new { qty = r.Quantity, id = r.LotId });
        await sql.Exec("INSERT INTO s_allocations(variant_id,lot_id,quantity) VALUES(@variant,@lot,@qty) ON DUPLICATE KEY UPDATE quantity=quantity+@qty", new { variant = r.VariantId, lot = r.LotId, qty = r.Quantity });
        var allocation = await sql.One("SELECT id FROM s_allocations WHERE variant_id=@variant AND lot_id=@lot", new { variant = r.VariantId, lot = r.LotId });
        await Move(sql, actor, r.LotId, -r.Quantity, "StoreAllocation", "StoreAllocation", allocation.L("id"));
        await sql.Audit(actor, "AllocatedToStore", "StoreAllocation", allocation.L("id"), r);
        return new
        {
            id = allocation.L("id")
        };
    }, ct);
    public async Task Release(long actor, long id, ReleaseRequest r, CancellationToken ct) => await db.Write(async sql =>
    {
        var a = await sql.One("SELECT * FROM s_allocations WHERE id=@id", new { id });
        Rules.Available(a.D("quantity") - a.D("reserved"), r.Quantity);
        await sql.Exec("UPDATE s_allocations SET quantity=quantity-@qty WHERE id=@id", new { qty = r.Quantity, id });
        await sql.Exec("UPDATE packaging_lots SET available_packs=available_packs+@qty WHERE id=@lot", new { qty = r.Quantity, lot = a.L("lot_id") });
        await Move(sql, actor, a.L("lot_id"), r.Quantity, "StoreAllocationReturned", "StoreAllocation", id);
        await sql.Audit(actor, "ReturnedToWarehouse", "StoreAllocation", id, r);
        return true;
    }, ct);
    static async Task<List<Dictionary<string, object?>>> Eligible(Sql sql, long variant) => await sql.Rows("SELECT a.*,l.best_before FROM s_allocations a JOIN packaging_lots l ON l.id=a.lot_id JOIN batches b ON b.id=l.batch_id JOIN products p ON p.id=b.output_product_id WHERE a.variant_id=@variant AND a.quantity>a.reserved AND b.quality='Released' AND p.is_active=1 AND l.best_before>=UTC_DATE() ORDER BY l.best_before,a.id", new { variant });
    static async Task<Quote> BuildQuote(Sql sql, List<CartLine> items, long shipping)
    {
        Rules.Require(items.Select(i => i.VariantId).Distinct().Count() == items.Count, "Duplicate variants are not allowed.");
        var settings = await sql.One("SELECT * FROM s_settings WHERE id=1");
        Rules.Require(settings.B("checkout_enabled") && settings.B("cod_enabled"), "Online ordering is currently unavailable.", 409);
        var policies = await sql.Rows("SELECT slug,version FROM s_content WHERE type='Policy' AND published=1 AND LENGTH(TRIM(body))>0 AND slug IN ('shipping','returns','privacy','terms') ORDER BY slug");
        Rules.Require(policies.Count == 4, "Online ordering is currently unavailable.", 409);
        var zone = await sql.One("SELECT * FROM s_shipping WHERE id=@id AND is_active=1 AND country_code='PK'", new { id = shipping });
        var lines = new List<QuoteLine>();
        foreach (var i in items)
        {
            var v = await sql.One("SELECT v.*,p.name,p.published,c.is_active AS category_active,op.is_active AS product_active FROM s_variants v JOIN s_products p ON p.id=v.product_id JOIN s_categories c ON c.id=p.category_id JOIN products op ON op.id=p.operational_product_id WHERE v.id=@id", new { id = i.VariantId });
            Rules.Require(v.B("is_active") && v.B("published") && v.B("category_active") && v.B("product_active"), "A selected product is no longer available.", 409);
            var stock = await Eligible(sql, i.VariantId);
            Rules.Available(stock.Sum(a => a.D("quantity") - a.D("reserved")), i.Quantity);
            lines.Add(new(i.VariantId, v.S("name"), v.S("label"), i.Quantity, v.D("price"), v.D("price") * i.Quantity));
        }

        var subtotal = lines.Sum(i => i.LineTotal);
        return new(lines, subtotal, zone.D("charge"), 0, subtotal + zone.D("charge"), settings.S("currency"), Hash(JsonSerializer.Serialize(policies)));
    }

    public Task<Quote> Quote(QuoteRequest r, CancellationToken ct) => db.Write(sql => BuildQuote(sql, r.Items, r.ShippingId), ct);
    public async Task<object> Place(OrderRequest r, CancellationToken ct) => await db.Write<object>(async sql =>
    {
        Rules.Require(r.RequestId != Guid.Empty && r.AcceptTerms, "Accept the terms and provide a nonempty request ID.");
        var hash = Hash(JsonSerializer.Serialize(r));
        var existing = await sql.Rows("SELECT id,reference,request_hash,protected_token,total,currency,status FROM s_orders WHERE request_id=@request", new { request = r.RequestId.ToString() });
        if (existing.Count > 0)
        {
            var old = existing[0];
            Rules.Require(old.S("request_hash") == hash, "This checkout request ID was already used with different details. Locate the original order before placing another.", 409);
            return new
            {
                reference = old.S("reference"),
                accessCode = protector.Unprotect(old.S("protected_token")),
                total = old.D("total"),
                currency = old.S("currency"),
                status = old.S("status")
            };
        }

        var q = await BuildQuote(sql, r.Items, r.ShippingId);
        Rules.Require(q.PolicyRevision == r.ExpectedPolicyRevision, "Policies changed. Review the updated policies and refresh your quote.", 409);
        Rules.Require(q.Total == r.ExpectedTotal, "Prices or delivery charges changed. Refresh your quote before ordering.", 409);
        var reference = "NUT-" + DateTime.UtcNow.ToString("yyyyMMdd") + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(5));
        var token = TokenService.RandomToken();
        var customer = await sql.Insert("INSERT INTO customers(name,contact,address) VALUES(@Name,@Phone,@address)", new { r.Name, r.Phone, address = $"{r.Address}, {r.City} {r.PostalCode}, {r.CountryCode}"[..Math.Min(500, $"{r.Address}, {r.City} {r.PostalCode}, {r.CountryCode}".Length)] });
        var policies = await sql.Rows("SELECT slug,title,body,version FROM s_content WHERE type='Policy' AND published=1 AND slug IN ('shipping','returns','privacy','terms') ORDER BY slug");
        var minutes = Math.Clamp(configuration.GetValue("Commerce:ReservationMinutes", 60), 5, 1440);
        var expiry = DateTime.UtcNow.AddMinutes(minutes);
        var id = await sql.Insert("INSERT INTO s_orders(reference,request_id,request_hash,token_hash,protected_token,policy_snapshot,name,email,phone,address,city,postal_code,country_code,currency,subtotal,shipping,total,expires_at,customer_id,payment_status) VALUES(@reference,@request,@hash,@tokenHash,@protectedToken,@policySnapshot,@Name,@Email,@Phone,@Address,@City,@PostalCode,@CountryCode,@Currency,@Subtotal,@Shipping,@Total,@expiry,@customer,@payment)", new { reference, request = r.RequestId.ToString(), hash, tokenHash = Hash(token), protectedToken = protector.Protect(token), policySnapshot = JsonSerializer.Serialize(policies), r.Name, r.Email, r.Phone, r.Address, r.City, r.PostalCode, r.CountryCode, q.Currency, q.Subtotal, q.Shipping, q.Total, expiry, customer, payment = payment.InitialStatus });
        foreach (var line in q.Items)
        {
            await sql.Exec("INSERT INTO s_order_items(order_id,variant_id,product_name,variant_label,quantity,unit_price,line_total) VALUES(@id,@VariantId,@Product,@Label,@Quantity,@UnitPrice,@LineTotal)", new { id, line.VariantId, line.Product, line.Label, line.Quantity, line.UnitPrice, line.LineTotal });
            var left = line.Quantity;
            foreach (var a in await Eligible(sql, line.VariantId))
            {
                var take = Math.Min(left, (int)(a.L("quantity") - a.L("reserved")));
                if (take == 0)
                    continue;
                await sql.Exec("UPDATE s_allocations SET reserved=reserved+@take WHERE id=@allocation", new { take, allocation = a.L("id") });
                await sql.Exec("INSERT INTO s_reservations(order_id,allocation_id,quantity) VALUES(@id,@allocation,@take)", new { id, allocation = a.L("id"), take });
                left -= take;
                if (left == 0)
                    break;
            }

            Rules.Require(left == 0, "Stock changed. Refresh your cart.", 409);
        }

        await sql.Exec("INSERT INTO s_order_events(order_id,event,details) VALUES(@id,'Created',@details)", new { id, details = JsonSerializer.Serialize(new { payment = payment.Method }) });
        // Public order creation is associated with its newly created legacy customer, not a fabricated staff actor.
        return new
        {
            reference,
            accessCode = token,
            total = q.Total,
            currency = q.Currency,
            status = "Pending",
            expiresAt = expiry
        };
    }, ct);
    public async Task<object> Lookup(LookupRequest r, CancellationToken ct)
    {
        var rows = await db.Query("SELECT id,reference,token_hash,name,status,payment_status,currency,subtotal,shipping,discount,total,expires_at,created_at FROM s_orders WHERE reference=@reference", new { reference = r.Reference }, ct);
        Rules.Require(rows.Count == 1, "Order not found or access code is incorrect.", 404);
        var order = rows[0];
        Rules.Require(CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Hash(r.AccessCode)), Encoding.UTF8.GetBytes(order.S("token_hash"))), "Order not found or access code is incorrect.", 404);
        order.Remove("token_hash");
        var items = await db.Query("SELECT product_name,variant_label,quantity,unit_price,line_total FROM s_order_items WHERE order_id=@id", new { id = order.L("id") }, ct);
        order.Remove("id");
        return new
        {
            order,
            items
        };
    }

    public async Task Expire(CancellationToken ct) => await db.Write(async sql =>
    {
        var rows = await sql.Rows("SELECT id FROM s_orders WHERE status='Pending' AND expires_at<=UTC_TIMESTAMP(6) LIMIT 200");
        foreach (var o in rows)
        {
            await Cancel(sql, o.L("id"), "Expired");
            await sql.Exec("INSERT INTO s_order_events(order_id,event,details) VALUES(@id,'Expired','{}')", new { id = o.L("id") });
        }

        return true;
    }, ct);
    static async Task Cancel(Sql sql, long id, string status)
    {
        var rows = await sql.Rows("SELECT * FROM s_reservations WHERE order_id=@id", new { id });
        foreach (var row in rows)
            await sql.Exec("UPDATE s_allocations SET reserved=reserved-@quantity WHERE id=@allocation", new { quantity = row.L("quantity"), allocation = row.L("allocation_id") });
        await sql.Exec("DELETE FROM s_reservations WHERE order_id=@id", new { id });
        await sql.Exec("UPDATE s_orders SET status=@status WHERE id=@id", new { status, id });
    }

    public async Task Action(long actor, long id, OrderActionRequest r, CancellationToken ct) => await db.Write(async sql =>
    {
        var order = await sql.One("SELECT * FROM s_orders WHERE id=@id", new { id });
        var state = order.S("status");
        if (r.Action == "Confirm")
        {
            Rules.Require(state == "Pending" && (DateTime)order["expires_at"]! > DateTime.UtcNow, "Order is not awaiting confirmation or its reservation expired.", 409);
            await sql.Exec("UPDATE s_orders SET status='Confirmed' WHERE id=@id", new { id });
        }

        if (r.Action == "Cancel")
        {
            Rules.Require(state is "Pending" or "Confirmed", "Only unshipped orders can be cancelled.", 409);
            await Cancel(sql, id, "Cancelled");
        }

        if (r.Action == "Deliver")
        {
            Rules.Require(state == "Shipped", "Only shipped COD orders can be delivered/collected.", 409);
            await sql.Exec("UPDATE s_orders SET status='Delivered',payment_status='Collected' WHERE id=@id", new { id });
        }

        if (r.Action == "Ship")
        {
            Rules.Require(state == "Confirmed", "Confirm the order before shipment.", 409);
            var customer = await sql.One("SELECT is_active FROM customers WHERE id=@customer", new { customer = order.L("customer_id") });
            Rules.Require(customer.B("is_active"), "The linked customer is inactive.", 409);
            var reservations = await sql.Rows("SELECT r.*,a.lot_id,l.best_before,b.quality,p.is_active FROM s_reservations r JOIN s_allocations a ON a.id=r.allocation_id JOIN packaging_lots l ON l.id=a.lot_id JOIN batches b ON b.id=l.batch_id JOIN products p ON p.id=b.output_product_id WHERE r.order_id=@id", new { id });
            Rules.Require(reservations.Count > 0, "No stock reservation remains.", 409);
            foreach (var row in reservations)
                Rules.Require(row.S("quality") == "Released" && row.B("is_active") && ((DateTime)row["best_before"]!).Date >= DateTime.UtcNow.Date, "Reserved stock is now held, inactive, or past best-before. Resolve the quality issue or cancel the order.", 409);
            var dispatch = await sql.Insert("INSERT INTO dispatches(code,customer_id,reference,created_by) VALUES(@code,@customer,@reference,@actor)", new { code = "WEB-" + Guid.NewGuid().ToString("N"), customer = order.L("customer_id"), reference = "STORE-" + order.S("reference"), actor });
            foreach (var group in reservations.GroupBy(x => x.L("lot_id")))
                await sql.Exec("INSERT INTO dispatch_items(dispatch_id,lot_id,packs) VALUES(@dispatch,@lot,@packs)", new { dispatch, lot = group.Key, packs = group.Sum(x => x.L("quantity")) });
            foreach (var row in reservations)
            {
                await sql.Exec("UPDATE s_allocations SET quantity=quantity-@qty,reserved=reserved-@qty WHERE id=@allocation", new { qty = row.L("quantity"), allocation = row.L("allocation_id") });
                await Move(sql, actor, row.L("lot_id"), 0, "StoreAllocatedStockDispatched", "Dispatch", dispatch);
            }

            await sql.Exec("DELETE FROM s_reservations WHERE order_id=@id", new { id });
            await sql.Exec("UPDATE s_orders SET status='Shipped',dispatch_id=@dispatch WHERE id=@id", new { dispatch, id });
        }

        await sql.Exec("INSERT INTO s_order_events(order_id,event,details,actor_id) VALUES(@id,@action,@details,@actor)", new { id, action = r.Action, details = JsonSerializer.Serialize(new { r.Reason }), actor });
        await sql.Audit(actor, "StoreOrder" + r.Action, "StoreOrder", id, new { r.Reason });
        return true;
    }, ct);
}

public sealed class ReservationExpiry(IServiceScopeFactory scopes, ILogger<ReservationExpiry> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<CommerceService>().Expire(stoppingToken);
            }
            catch (OperationCanceledException)when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reservation expiry failed.");
            }
        }
    }
}
