using System.Net;
using System.Text.Json;

namespace Sayserne.Tests;

public class ReceiptArabicTests
{
    [Fact]
    public async Task A_receipt_also_carries_its_description_and_payment_method_in_Arabic_while_the_English_one_is_unchanged()
    {
        await using var s = await AdTests.SceneHandle.StartAsync();
        var ad = await s.NewAd("Cedar Cafe"); var (bs, batch) = await s.Book(ad, 0, 1);
        Assert.Equal(HttpStatusCode.Created, bs);
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PostAsync($"/api/admin/ads/batches/{batch.GetProperty("id").GetInt64()}/status", new { status = "paid", billedTo = "Cedar Co", method = "cash" })).Item1);
        var (pro, _, _) = await s.Host.RegisterProfessionalAsync("Rana", "Arabic", "Arabville", "LB");
        var listing = await s.Host.AddProfessionAsync(pro, extra: new() { ["city"] = "Arabville", ["country"] = "LB" });
        await ApiHost.MakePremiumAsync(s.Admin, listing);

        var list = (await s.Admin.GetAsync("/api/admin/payments?pageSize=10")).Body.GetProperty("items").EnumerateArray().OrderBy(i => i.GetProperty("id").GetInt64()).ToList();
        Assert.Equal(2, list.Count);
        var adReceipt = (await s.Admin.GetAsync($"/api/admin/payments/{list[0].GetProperty("id").GetInt64()}")).Body;
        var premiumReceipt = (await s.Admin.GetAsync($"/api/admin/payments/{list[1].GetProperty("id").GetInt64()}")).Body;

        Assert.StartsWith("Advertisement \"Cedar Cafe\"", adReceipt.GetProperty("description").GetString());             // English, as before
        var adAr = adReceipt.GetProperty("descriptionAr").GetString()!;
        Assert.StartsWith("إعلان \"Cedar Cafe\"", adAr);                                                                  // Arabic, with the ad's own title
        Assert.Contains(" إلى ", adAr);
        Assert.DoesNotContain("Advertisement", adAr);
        Assert.Matches(@"\p{IsArabic}", adReceipt.GetProperty("methodNameAr").GetString() ?? "");                       // the payment method has an Arabic name too

        Assert.StartsWith("Premium listing:", premiumReceipt.GetProperty("description").GetString());
        var premAr = premiumReceipt.GetProperty("descriptionAr").GetString()!;
        Assert.StartsWith("إدراج مميّز:", premAr);
        Assert.Contains("Rana Arabic", premAr);                                                                          // names are not translated, only the words around them
        Assert.DoesNotContain("Premium listing", premAr);
        Assert.Equal(adReceipt.GetProperty("total").GetDecimal(), 10m);                                                   // the money is untouched
    }
}
