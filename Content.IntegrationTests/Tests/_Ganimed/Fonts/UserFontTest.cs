// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Client._Ganimed.Fonts;
using Content.Client.Resources;
using Content.Shared._Ganimed.CCVar;
using Content.Shared._Ganimed.Fonts.Prototypes;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._Ganimed.Fonts;

[TestFixture]
public sealed class UserFontTest
{
    private const string RegularOnlyId = "UserFontTestRegularOnly";

    [TestPrototypes]
    private const string Prototypes = @"
- type: uiFont
  id: UserFontTestRegularOnly
  name: Regular only
  regular: /Fonts/_Ganimed/Liberation/LiberationSans-Regular.ttf
";

    private static readonly ResPath StockRegular = new("/Fonts/NotoSans/NotoSans-Regular.ttf");
    private static readonly ResPath StockBold = new("/Fonts/NotoSans/NotoSans-Bold.ttf");
    private static readonly ResPath StockItalic = new("/Fonts/NotoSans/NotoSans-Italic.ttf");
    private static readonly ResPath StockBoldItalic = new("/Fonts/NotoSans/NotoSans-BoldItalic.ttf");
    private static readonly ResPath StockDisplayBold = new("/Fonts/NotoSansDisplay/NotoSansDisplay-Bold.ttf");
    private static readonly ResPath StockSymbols = new("/Fonts/NotoSans/NotoSansSymbols-Regular.ttf");
    private static readonly ResPath Mono = new("/EngineFonts/NotoSans/NotoSansMono-Regular.ttf");

    /// <summary>
    ///     Every <c>uiFont</c> prototype must point at fonts that exist, and be put in front of the stock faces.
    /// </summary>
    [Test]
    public async Task EveryFontFamilyLoadsAndReplacesTheStockFaces()
    {
        await using var pair = await PoolManager.GetServerClient();
        var client = pair.Client;
        var protoMan = client.ResolveDependency<IPrototypeManager>();
        var fonts = client.ResolveDependency<UserFontManager>();

        await client.WaitAssertion(() =>
        {
            Assert.That(fonts.Current, Is.Null, "The stock font must be the default");

            try
            {
                foreach (var family in protoMan.EnumeratePrototypes<UiFontPrototype>())
                {
                    client.CfgMan.SetCVar(GanimedCCVars.UiFont, family.ID);

                    // A missing or broken file makes the manager fall back to the stock font.
                    Assert.That(fonts.Current, Is.SameAs(family), $"Font family {family.ID} can't be loaded");
                    Assert.That(family.Name, Is.Not.Empty, $"Font family {family.ID} has no name");

                    // The player's font goes first, the whole stock stack stays behind it as the glyph fallback.
                    var stack = new[] { StockBold, StockSymbols, Mono };
                    var substituted = fonts.Substitute(stack);
                    Assert.That(substituted, Is.EqualTo(new[] { family.Bold ?? family.Regular, StockBold, StockSymbols, Mono }));
                }
            }
            finally
            {
                client.CfgMan.SetCVar(GanimedCCVars.UiFont, string.Empty);
            }
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MissingFacesFallBackAndOnlyTheStockFacesAreReplaced()
    {
        await using var pair = await PoolManager.GetServerClient();
        var client = pair.Client;
        var protoMan = client.ResolveDependency<IPrototypeManager>();
        var fonts = client.ResolveDependency<UserFontManager>();
        var cache = client.ResolveDependency<IResourceCache>();

        await client.WaitAssertion(() =>
        {
            var family = protoMan.Index<UiFontPrototype>(RegularOnlyId);

            try
            {
                client.CfgMan.SetCVar(GanimedCCVars.UiFont, family.ID);
                Assert.That(fonts.Current, Is.SameAs(family));

                // Faces the family doesn't have fall back to its regular one.
                foreach (var stock in new[] { StockRegular, StockBold, StockItalic, StockBoldItalic, StockDisplayBold })
                {
                    Assert.That(fonts.Substitute(new[] { stock })[0], Is.EqualTo(family.Regular), $"Face {stock}");
                }

                // Fonts that are not the default Noto Sans are chosen on purpose and must stay.
                var untouched = new[] { Mono };
                Assert.That(fonts.Substitute(untouched), Is.SameAs(untouched));
                Assert.That(fonts.TrySubstitute(Mono, out _), Is.False);

                // A lone stock face has no fallback of its own, so it gets the stock face and Japanese behind it.
                Assert.That(fonts.TrySubstitute(StockBold, out var lone), Is.True);
                Assert.That(lone!, Has.Length.EqualTo(3));
                Assert.That(lone![0], Is.EqualTo(family.Regular));
                Assert.That(lone[1], Is.EqualTo(StockBold));
                Assert.That(lone[2], Is.EqualTo(new ResPath(GanimedFontStack.JapaneseBold)));

                // What the whole game uses to create fonts.
                var font = cache.GetFont(StockBold, 12);
                Assert.That(font, Is.TypeOf<StackedFont>());
                Assert.That(((StackedFont) font).Stack, Has.Length.EqualTo(3));
                Assert.That(((StackedFont) font).Stack[0], Is.TypeOf<VectorFont>());
            }
            finally
            {
                client.CfgMan.SetCVar(GanimedCCVars.UiFont, string.Empty);
            }

            // Back to the stock font: nothing is substituted and fonts are created like before.
            Assert.That(fonts.Current, Is.Null);
            Assert.That(cache.GetFont(StockBold, 12), Is.TypeOf<VectorFont>());
        });

        await pair.CleanReturnAsync();
    }
}
