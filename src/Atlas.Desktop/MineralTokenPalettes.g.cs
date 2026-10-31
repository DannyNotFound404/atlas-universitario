// Generated from university-only Grafito Acero tokens.
using System.Drawing;
namespace Atlas.Desktop;
internal static class MineralTokenPalettes
{
    private static Color C(string value) => ColorTranslator.FromHtml(value);
    internal static MineralPalette Light { get; } = new(C("#f2f5f8"), C("#ffffff"), C("#eaf0f5"), C("#dde7ef"), C("#1d2c39"), C("#526779"), C("#92a8b9"), C("#176d91"), C("#105673"), C("#ffffff"), C("#223749"), C("#f3f8fc"), C("#becedb"), C("#9ddcf1"), C("#286241"), C("#e8f1e1"), C("#8b601d"), C("#f8eedb"), C("#a04351"), C("#f9e9ec"), C("#366382"), C("#e9f0f6"));
    internal static Color SageLight { get; } = C("#32735b");
    internal static Color SageSoftLight { get; } = C("#e5f0e9");
    internal static MineralPalette Dark { get; } = new(C("#222b34"), C("#2e3945"), C("#26323d"), C("#394956"), C("#f3f7fb"), C("#c4d2dd"), C("#7c94a7"), C("#83cce6"), C("#a6ddf0"), C("#102b36"), C("#1c252e"), C("#f3f7fb"), C("#c4d2dd"), C("#83cce6"), C("#b0f08a"), C("#3e5c46"), C("#f1ca82"), C("#5d4f34"), C("#f2b5bd"), C("#64434b"), C("#b4d7ed"), C("#3c5262"));
    internal static Color SageDark { get; } = C("#a6d9bd");
    internal static Color SageSoftDark { get; } = C("#354f44");
}
