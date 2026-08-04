using Content.Client.Examine;
using Content.Client.Stylesheets.Fonts;
using Content.Client.Stylesheets.SheetletConfigs;
using Content.Client.Stylesheets.Stylesheets;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Maths;
using static Content.Client.Stylesheets.StylesheetHelpers;

namespace Content.Client.Stylesheets.Sheetlets.Hud;

[CommonSheetlet]
public sealed class TooltipSheetlet<T> : Sheetlet<T> where T: PalettedStylesheet, ITooltipConfig
{
    public override StyleRule[] GetRules(T sheet, object config)
    {
        ITooltipConfig tooltipCfg = sheet;

        var tooltipBox = sheet.GetTextureOr(tooltipCfg.TooltipBoxPath, NanotrasenStylesheet.TextureRoot)
            .IntoPatch(StyleBox.Margin.All, 2);
        tooltipBox.SetContentMarginOverride(StyleBox.Margin.Horizontal, 7);

        var whisperBox = sheet.GetTextureOr(tooltipCfg.WhisperBoxPath, NanotrasenStylesheet.TextureRoot)
            .IntoPatch(StyleBox.Margin.All, 2);
        whisperBox.SetContentMarginOverride(StyleBox.Margin.Horizontal, 7);

        // Ganimed-Edit: separate speech bubbles for emotes (emoteBox) and LOOC (loocBox), issue #283.
        var emoteBox = new StyleBoxFlat
        {
            BackgroundColor = new Color(43, 43, 49, 255),
            BorderColor = new Color(90, 90, 100, 255),
            BorderThickness = new Thickness(1),
        };
        emoteBox.SetContentMarginOverride(StyleBox.Margin.Horizontal, 7);

        var loocBox = new StyleBoxFlat
        {
            BackgroundColor = new Color(27, 27, 31, 255),
            BorderColor = Color.FromHex("#48d1cc"),
            BorderThickness = new Thickness(1),
        };
        loocBox.SetContentMarginOverride(StyleBox.Margin.Horizontal, 7);

        return
        [
            E<PanelContainer>()
                .Class(StyleClass.TooltipPanel)
                .Modulate(Color.Gray.WithAlpha(0.9f)) // TODO: you know the drill by now
                .Panel(tooltipBox),
            E<RichTextLabel>()
                .Class(StyleClass.TooltipTitle)
                .Font(sheet.BaseFont.GetFont(14, FontKind.Bold)),
            E<RichTextLabel>()
                .Class(StyleClass.TooltipDesc)
                .Font(sheet.BaseFont.GetFont(12)),

            E<Tooltip>()
                // ReSharper disable once AccessToStaticMemberViaDerivedType
                .Prop(Tooltip.StylePropertyPanel, tooltipBox),
            E<PanelContainer>()
                .Class(ExamineSystem.StyleClassEntityTooltip)
                .Panel(tooltipBox),
            E<PanelContainer>()
                .Class("speechBox", "sayBox")
                .Panel(tooltipBox),
            E<PanelContainer>()
                .Class("speechBox", "whisperBox")
                .Panel(whisperBox),

            // Ganimed-Edit: separate speech bubbles for emotes and LOOC, issue #283.
            E<PanelContainer>()
                .Class("speechBox", "emoteBox")
                .Panel(emoteBox),
            E<PanelContainer>()
                .Class("speechBox", "loocBox")
                .Panel(loocBox),

            E<PanelContainer>()
                .Class("speechBox", "whisperBox")
                .ParentOf(E<RichTextLabel>().Class("bubbleContent"))
                .Prop(Label.StylePropertyFont, sheet.BaseFont.GetFont(12, FontKind.Italic)),
            E<PanelContainer>()
                .Class("speechBox", "emoteBox")
                .ParentOf(E<RichTextLabel>().Class("bubbleContent"))
                .Prop(Label.StylePropertyFont, sheet.BaseFont.GetFont(12, FontKind.Italic)),
        ];
    }
}
