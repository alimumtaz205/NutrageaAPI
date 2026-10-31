using Nutragea.Api.Data;
using System.Text.Json;
namespace Nutragea.Commerce.Services;

/// <summary>Explicit local catalogue samples. Never fabricates operational inventory.</summary>
public static class DemoSeeder
{
    public static async Task Run(IConfiguration config, IWebHostEnvironment environment)
    {
        if (!environment.IsDevelopment()) throw new InvalidOperationException("Demo seeding is allowed only in Development.");
        var db = new Db(config);
        var admins = await db.Query("SELECT id FROM users WHERE role='Admin' AND is_active=1 ORDER BY id LIMIT 1", null, default);
        if (admins.Count == 0) throw new InvalidOperationException("Create your existing Batch Tracker administrator first.");
        var actor = admins[0].L("id");
        const string alt = "Demo catalogue: illustrative dried fruit and nuts still life, not an actual product photograph";
        var images = await db.Query("SELECT id FROM s_media WHERE alt_text=@alt ORDER BY id LIMIT 1", new { alt }, default);
        long mediaId;
        if (images.Count > 0) mediaId = images[0].L("id");
        else
        {
            await using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "demo/editorial-food.png"));
            var file = new FormFile(stream, 0, stream.Length, "file", "editorial-food.png") { Headers = new HeaderDictionary(), ContentType = "image/png" };
            var uploaded = await new MediaService(db, config, environment).Upload(actor, file, alt, true, false, false, default);
            mediaId = JsonSerializer.SerializeToElement(uploaded).GetProperty("id").GetInt64();
        }
        await db.Write(async sql =>
        {
            foreach (var sample in new[] {
                new { Name="Dried Apricots", Slug="demo-apricots", Category="dried-fruit", Price=650m, Ingredient="Apricots", Allergen="Demo only: verify processing and cross-contact information before sale." },
                new { Name="Walnut Kernels", Slug="demo-walnuts", Category="nuts", Price=950m, Ingredient="Walnuts", Allergen="Contains walnuts (tree nuts)." },
                new { Name="Almonds", Slug="demo-almonds", Category="nuts", Price=850m, Ingredient="Almonds", Allergen="Contains almonds (tree nuts)." }
            })
            {
                if ((await sql.Rows("SELECT id FROM s_products WHERE slug=@slug", new { slug=sample.Slug })).Count > 0) continue;
                var category = await sql.One("SELECT id FROM s_categories WHERE slug=@slug", new { slug=sample.Category });
                var freshName = "[DEMO] Raw " + sample.Name;
                var fresh = await sql.Rows("SELECT id FROM products WHERE name=@name", new { name=freshName });
                var freshId = fresh.Count > 0 ? fresh[0].L("id") : await sql.Insert("INSERT INTO products(name,stage) VALUES(@name,'Fresh')", new { name=freshName });
                var driedName = "[DEMO] " + sample.Name;
                var dried = await sql.Rows("SELECT id FROM products WHERE name=@name", new { name=driedName });
                var operational = dried.Count > 0 ? dried[0].L("id") : await sql.Insert("INSERT INTO products(name,stage,source_product_id) VALUES(@name,'Dried',@source)", new { name=driedName, source=freshId });
                var id = await sql.Insert("INSERT INTO s_products(name,slug,category_id,operational_product_id,summary,description,origin,ingredients,allergens,storage,image_ids,published,featured) VALUES(@name,@slug,@category,@operational,@summary,@description,@origin,@ingredients,@allergens,@storage,@images,1,1)", new {
                    name=sample.Name+" (Demo)", slug=sample.Slug, category=category.L("id"), operational,
                    summary="Sample 250 g pack. Demonstration price only; not available for purchase.",
                    description="Dummy product for previewing the Nutragea store. Price, pack details and origin are examples, not a sales offer. The shared food image is illustrative. Replace with verified details and your actual product photography before selling.",
                    origin="Gilgit-Baltistan — sample origin, unverified", ingredients=sample.Ingredient, allergens=sample.Allergen,
                    storage="Sample guidance: store in a cool, dry place in a sealed container. Verify your actual label.", images=JsonSerializer.Serialize(new[]{mediaId}) });
                await sql.Insert("INSERT INTO s_variants(product_id,label,sku,price,net_weight_kg,is_active) VALUES(@id,'250 g · demo',@sku,@price,0.250,1)", new { id, sku=sample.Slug.ToUpperInvariant()+"-250", price=sample.Price });
                await sql.Audit(actor, "DemoCatalogueSeeded", "StoreProduct", id);
            }
            return true;
        });
        Console.WriteLine("Demo apricots, walnuts and almonds are ready. Existing samples are left unchanged. Stock remains zero; no receipts, quality releases or allocations were fabricated.");
    }
}
