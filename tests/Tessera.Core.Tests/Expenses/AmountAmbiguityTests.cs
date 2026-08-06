using System.Globalization;
using Tessera.Core.Expenses;

namespace Tessera.Core.Tests.Expenses;

public class AmountAmbiguityTests
{
    private static readonly CultureInfo It = new("it-IT");
    private static readonly CultureInfo En = new("en-US");

    [Theory]
    [InlineData("1.500")] // it: looks grouped (1500), but could be a mistyped "1.5"
    [InlineData("1.50")]  // it: parses to 150 as grouped — a real, silent 100x bug otherwise
    public void IsAmbiguous_vero_per_separatore_di_raggruppamento_senza_decimale_in_italiano(string text)
    {
        Assert.True(AmountAmbiguity.IsAmbiguous(text, It));
    }

    [Theory]
    [InlineData("20,50")]     // normal it decimal amount
    [InlineData("1.200,50")] // fully-qualified it amount: grouping AND decimal both present
    [InlineData("20")]        // plain integer, no separator at all
    public void IsAmbiguous_falso_per_importi_non_ambigui_in_italiano(string text)
    {
        Assert.False(AmountAmbiguity.IsAmbiguous(text, It));
    }

    [Theory]
    [InlineData("1,5")]   // en: parses to 15 as grouped — silent 10x bug otherwise
    [InlineData("1,50")]
    public void IsAmbiguous_vero_per_separatore_di_raggruppamento_senza_decimale_in_inglese(string text)
    {
        Assert.True(AmountAmbiguity.IsAmbiguous(text, En));
    }

    [Theory]
    [InlineData("20.50")]
    [InlineData("1,500.50")]
    [InlineData("20")]
    public void IsAmbiguous_falso_per_importi_non_ambigui_in_inglese(string text)
    {
        Assert.False(AmountAmbiguity.IsAmbiguous(text, En));
    }

    [Fact]
    public void GetCandidates_calcola_entrambe_le_letture_in_italiano()
    {
        var (asGrouped, asDecimal) = AmountAmbiguity.GetCandidates("1.500", It);

        Assert.Equal(1500m, asGrouped);
        Assert.Equal(1.5m, asDecimal);
    }

    [Fact]
    public void GetCandidates_calcola_entrambe_le_letture_in_inglese()
    {
        var (asGrouped, asDecimal) = AmountAmbiguity.GetCandidates("1,5", En);

        Assert.Equal(15m, asGrouped);
        Assert.Equal(1.5m, asDecimal);
    }
}
