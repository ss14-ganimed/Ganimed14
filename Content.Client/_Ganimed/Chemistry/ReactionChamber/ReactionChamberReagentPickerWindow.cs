using System.Linq;
using Content.Client.Stylesheets;
using Content.Shared.Chemistry.Reagent;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using System.Numerics;

namespace Content.Client._Ganimed.Chemistry.ReactionChamber;

public sealed class ReactionChamberReagentPickerWindow : DefaultWindow
{
    public event Action<ReagentPrototype>? ReagentSelected;

    [Dependency] private readonly IPrototypeManager _prototypes = default!;

    private readonly LineEdit _searchBar;
    private readonly ItemList _reagentList;
    private readonly Button _selectButton;

    private ReagentPrototype? _pendingReagent;
    private bool _hoveringReagent;

    public ReactionChamberReagentPickerWindow(string? selectedReagentId = null)
    {
        IoCManager.InjectDependencies(this);

        Title = Loc.GetString("reaction-chamber-program-reagent-picker-title");
        MinSize = new Vector2(320, 420);
        SetSize = new Vector2(320, 420);

        var root = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 4,
            Margin = new Thickness(6),
            HorizontalExpand = true,
            VerticalExpand = true,
        };

        _searchBar = new LineEdit
        {
            PlaceHolder = Loc.GetString("reaction-chamber-program-reagent-search-placeholder"),
            HorizontalExpand = true,
        };
        _searchBar.OnTextChanged += _ => PopulateList(_searchBar.Text);

        _reagentList = new ItemList
        {
            SelectMode = ItemList.ItemListSelectMode.Single,
            VerticalExpand = true,
            HorizontalExpand = true,
        };
        _reagentList.OnItemHover += _ =>
        {
            _hoveringReagent = true;
            UpdateSelectButtonVisuals();
        };
        _reagentList.OnItemSelected += args => SelectReagent(args.ItemIndex);

        _selectButton = new Button
        {
            Text = Loc.GetString("reaction-chamber-program-select-reagent"),
            HorizontalExpand = true,
            Disabled = true,
        };
        _selectButton.OnPressed += _ => ConfirmSelection();

        root.AddChild(_searchBar);
        root.AddChild(_reagentList);
        root.AddChild(_selectButton);

        Contents.AddChild(root);

        PopulateList();
        SelectCurrentReagent(selectedReagentId);
    }

    private void SelectReagent(int index)
    {
        if (index < 0 || index >= _reagentList.Count)
            return;

        if (_reagentList[index].Metadata is not ReagentPrototype reagent)
            return;

        _pendingReagent = reagent;
        _reagentList[index].Selected = true;
        UpdateSelectButtonVisuals();
    }

    private void UpdateSelectButtonVisuals()
    {
        _selectButton.Disabled = _pendingReagent == null;

        if (_hoveringReagent || _pendingReagent != null)
            _selectButton.AddStyleClass(StyleNano.StyleClassButtonColorGreen);
        else
            _selectButton.RemoveStyleClass(StyleNano.StyleClassButtonColorGreen);
    }

    private void ConfirmSelection()
    {
        if (_pendingReagent == null)
            return;

        var reagent = _pendingReagent;
        Timer.Spawn(0, () =>
        {
            ReagentSelected?.Invoke(reagent);
            Close();
        });
    }

    private void PopulateList(string? filter = null)
    {
        _reagentList.Clear();
        _pendingReagent = null;
        _hoveringReagent = false;
        UpdateSelectButtonVisuals();

        var filterText = filter?.Trim();
        var hasFilter = !string.IsNullOrEmpty(filterText);

        foreach (var reagent in _prototypes.EnumeratePrototypes<ReagentPrototype>().OrderBy(r => r.LocalizedName))
        {
            if (hasFilter
                && !reagent.ID.Contains(filterText!, StringComparison.CurrentCultureIgnoreCase)
                && !reagent.LocalizedName.Contains(filterText!, StringComparison.CurrentCultureIgnoreCase))
            {
                continue;
            }

            _reagentList.AddItem(reagent.LocalizedName, metadata: reagent);
        }
    }

    private void SelectCurrentReagent(string? selectedReagentId)
    {
        if (string.IsNullOrEmpty(selectedReagentId))
            return;

        for (var i = 0; i < _reagentList.Count; i++)
        {
            if (_reagentList[i].Metadata is ReagentPrototype reagent && reagent.ID == selectedReagentId)
            {
                SelectReagent(i);
                break;
            }
        }
    }
}
