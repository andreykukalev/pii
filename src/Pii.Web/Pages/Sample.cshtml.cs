using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Pii.Web.Pages;

public sealed class SampleModel : PageModel
{
    public const string Csv = """
        id,instrumentId,symbol,exchange,currency,kind,sector,time,type,quantity,gross,fee,eurPerUnit,cik,splitRatio
        synthetic-buy-1,SAMPLE:XNAS:USD,SAMPLE,XNAS,USD,Stock,Technology,2026-01-01T10:00:00Z,Buy,10,1000,2,0.9,,
        synthetic-sell-1,SAMPLE:XNAS:USD,SAMPLE,XNAS,USD,Stock,Technology,2026-01-02T10:00:00Z,Sell,2,240,1,0.92,,
        """;
    public IActionResult OnGet() => File(Encoding.UTF8.GetBytes(Csv + "\n"), "text/csv; charset=utf-8", "pii-synthetic-sample-v1.csv");
}