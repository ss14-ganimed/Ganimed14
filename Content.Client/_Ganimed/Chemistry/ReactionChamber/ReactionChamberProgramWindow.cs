using System.Linq;
using System.Numerics;
using Content.Client.UserInterface.Controls;
using Content.Shared._Ganimed.Chemistry.ReactionChamber;
using Content.Shared.Chemistry.Reagent;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.Client._Ganimed.Chemistry.ReactionChamber;

public sealed class ReactionChamberProgramWindow : DefaultWindow
{
    public event Action<List<ReactionChamberProgram>>? ProgramsSaved;

    [Dependency] private readonly IPrototypeManager _prototypes = default!;

    private readonly ItemList _programList;
    private readonly LineEdit _programNameEdit;
    private readonly BoxContainer _stepsContainer;
    private readonly Button _addProgramButton;
    private readonly Button _deleteProgramButton;
    private readonly Button _addStepButton;
    private readonly Button _saveButton;

    private List<ReactionChamberProgram> _programs = new();
    private int _selectedProgramIndex = -1;
    private ReactionChamberReagentPickerWindow? _reagentPicker;

    public ReactionChamberProgramWindow()
    {
        IoCManager.InjectDependencies(this);

        Title = Loc.GetString("reaction-chamber-program-window-title");
        MinSize = new Vector2(760, 900);
        SetSize = new Vector2(760, 900);

        var root = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 6,
            Margin = new Thickness(6),
            HorizontalExpand = true,
            VerticalExpand = true,
        };

        var topRow = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            SeparationOverride = 8,
            HorizontalExpand = true,
            MinSize = new Vector2(0, 120),
            MaxSize = new Vector2(32767, 160),
        };

        var programListScroll = new ScrollContainer
        {
            MinSize = new Vector2(420, 120),
            MaxSize = new Vector2(32767, 160),
            HScrollEnabled = false,
            VerticalExpand = true,
            HorizontalExpand = true,
            SizeFlagsStretchRatio = 1,
        };

        _programList = new ItemList { HorizontalExpand = true, VerticalExpand = true };
        _programList.OnItemSelected += args => SelectProgram(args.ItemIndex);
        programListScroll.AddChild(_programList);

        var programButtons = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 4,
            MinSize = new Vector2(150, 0),
        };

        _addProgramButton = new Button { Text = Loc.GetString("reaction-chamber-program-add") };
        _deleteProgramButton = new Button { Text = Loc.GetString("reaction-chamber-program-delete") };
        _addProgramButton.OnPressed += _ => AddProgram();
        _deleteProgramButton.OnPressed += _ => DeleteProgram();

        programButtons.AddChild(_addProgramButton);
        programButtons.AddChild(_deleteProgramButton);

        topRow.AddChild(programListScroll);
        topRow.AddChild(programButtons);

        _programNameEdit = new LineEdit
        {
            HorizontalExpand = true,
            PlaceHolder = Loc.GetString("reaction-chamber-program-name-placeholder"),
        };
        _programNameEdit.OnTextChanged += _ => UpdateSelectedProgramName();

        _stepsContainer = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 4,
            VerticalExpand = true,
        };

        var stepsScroll = new ScrollContainer
        {
            HorizontalExpand = true,
            VerticalExpand = true,
            HScrollEnabled = false,
            MinSize = new Vector2(0, 480),
            SizeFlagsStretchRatio = 2,
        };
        stepsScroll.AddChild(_stepsContainer);

        _addStepButton = new Button { Text = Loc.GetString("reaction-chamber-program-add-step") };
        _addStepButton.OnPressed += _ => AddStep();

        _saveButton = new Button { Text = Loc.GetString("reaction-chamber-program-save") };
        _saveButton.OnPressed += _ => SavePrograms();

        root.AddChild(topRow);
        root.AddChild(new Label
        {
            Text = Loc.GetString("reaction-chamber-program-name-label"),
            StyleClasses = { "LabelKeyText" },
        });
        root.AddChild(_programNameEdit);
        root.AddChild(new Label
        {
            Text = Loc.GetString("reaction-chamber-program-steps-label"),
            StyleClasses = { "LabelKeyText" },
        });
        root.AddChild(stepsScroll);
        root.AddChild(_addStepButton);
        root.AddChild(_saveButton);

        Contents.AddChild(root);

        OnClose += CloseReagentPicker;
    }

    private void CloseReagentPicker()
    {
        _reagentPicker?.Close();
        _reagentPicker = null;
    }

    public void SetPrograms(List<ReactionChamberProgram> programs)
    {
        _programs = programs.Select(p => new ReactionChamberProgram
        {
            Name = p.Name,
            Steps = p.Steps.Select(s => new ReactionChamberStep
            {
                Type = s.Type,
                ReagentId = s.ReagentId,
                Amount = s.Amount,
            }).ToList(),
        }).ToList();

        RebuildProgramList();

        if (_selectedProgramIndex < 0 || _selectedProgramIndex >= _programs.Count)
            SelectProgram(_programs.Count > 0 ? 0 : -1);
        else
            RebuildStepsEditor();
    }

    private void RebuildProgramList()
    {
        _programList.Clear();

        for (var i = 0; i < _programs.Count; i++)
        {
            _programList.AddItem(Loc.GetString("reaction-chamber-window-program-entry",
                ("name", _programs[i].Name),
                ("steps", _programs[i].Steps.Count)));
        }
    }

    private void SelectProgram(int index)
    {
        _selectedProgramIndex = index >= 0 && index < _programs.Count ? index : -1;
        RebuildStepsEditor();
    }

    private void UpdateSelectedProgramName()
    {
        if (_selectedProgramIndex < 0 || _selectedProgramIndex >= _programs.Count)
            return;

        _programs[_selectedProgramIndex].Name = _programNameEdit.Text;
        RebuildProgramList();
        if (_selectedProgramIndex >= 0 && _selectedProgramIndex < _programList.Count)
            _programList[_selectedProgramIndex].Selected = true;
    }

    private void AddProgram()
    {
        if (_programs.Count >= ReactionChamberComponent.MaxPrograms)
            return;

        _programs.Add(new ReactionChamberProgram { Name = Loc.GetString("reaction-chamber-program-default-name") });
        RebuildProgramList();
        SelectProgram(_programs.Count - 1);
        _programList[_selectedProgramIndex].Selected = true;
    }

    private void DeleteProgram()
    {
        if (_selectedProgramIndex < 0 || _selectedProgramIndex >= _programs.Count)
            return;

        _programs.RemoveAt(_selectedProgramIndex);
        RebuildProgramList();
        SelectProgram(_programs.Count > 0 ? Math.Min(_selectedProgramIndex, _programs.Count - 1) : -1);
    }

    private void AddStep()
    {
        if (_selectedProgramIndex < 0 || _selectedProgramIndex >= _programs.Count)
            return;

        var program = _programs[_selectedProgramIndex];
        if (program.Steps.Count >= ReactionChamberComponent.MaxStepsPerProgram)
            return;

        program.Steps.Add(new ReactionChamberStep
        {
            Type = ReactionChamberStepType.AddFromBufferToBeaker,
            Amount = 10,
        });

        RebuildStepsEditor();
        RebuildProgramList();
        _programList[_selectedProgramIndex].Selected = true;
    }

    private void RebuildStepsEditor()
    {
        _stepsContainer.Children.Clear();

        if (_selectedProgramIndex < 0 || _selectedProgramIndex >= _programs.Count)
        {
            _programNameEdit.Text = string.Empty;
            _programNameEdit.Editable = false;
            _addStepButton.Disabled = true;
            _deleteProgramButton.Disabled = true;
            return;
        }

        var program = _programs[_selectedProgramIndex];
        _programNameEdit.Editable = true;
        _programNameEdit.Text = program.Name;
        _addStepButton.Disabled = program.Steps.Count >= ReactionChamberComponent.MaxStepsPerProgram;
        _deleteProgramButton.Disabled = false;

        for (var i = 0; i < program.Steps.Count; i++)
        {
            var stepIndex = i;
            var step = program.Steps[i];
            _stepsContainer.AddChild(BuildStepRow(program, stepIndex, step, program.Steps.Count));
        }
    }

    private void MoveStep(int stepIndex, int direction)
    {
        if (_selectedProgramIndex < 0 || _selectedProgramIndex >= _programs.Count)
            return;

        var program = _programs[_selectedProgramIndex];
        var targetIndex = stepIndex + direction;
        if (targetIndex < 0 || targetIndex >= program.Steps.Count)
            return;

        (program.Steps[stepIndex], program.Steps[targetIndex]) = (program.Steps[targetIndex], program.Steps[stepIndex]);
        RebuildStepsEditor();
        RebuildProgramList();
        _programList[_selectedProgramIndex].Selected = true;
    }

    private Control BuildStepRow(ReactionChamberProgram program, int stepIndex, ReactionChamberStep step, int stepCount)
    {
        var row = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            SeparationOverride = 4,
            HorizontalExpand = true,
        };

        var upButton = new Button
        {
            Text = "↑",
            MinSize = new Vector2(28, 0),
            Disabled = stepIndex == 0,
            ToolTip = Loc.GetString("reaction-chamber-program-step-up"),
        };
        upButton.OnPressed += _ => MoveStep(stepIndex, -1);
        row.AddChild(upButton);

        var downButton = new Button
        {
            Text = "↓",
            MinSize = new Vector2(28, 0),
            Disabled = stepIndex >= stepCount - 1,
            ToolTip = Loc.GetString("reaction-chamber-program-step-down"),
        };
        downButton.OnPressed += _ => MoveStep(stepIndex, 1);
        row.AddChild(downButton);

        var typeButton = new OptionButton { MinSize = new Vector2(220, 0) };
        foreach (ReactionChamberStepType type in Enum.GetValues<ReactionChamberStepType>())
            typeButton.AddItem(GetStepTypeLabel(type), (int)type);

        typeButton.SelectId((int)step.Type);
        typeButton.OnItemSelected += args =>
        {
            step.Type = (ReactionChamberStepType)args.Id;
            if (!StepNeedsReagent(step.Type))
                step.ReagentId = string.Empty;

            RebuildStepsEditor();
        };

        row.AddChild(typeButton);

        if (StepNeedsReagent(step.Type))
        {
            var reagentButton = new Button
            {
                Text = GetReagentButtonText(step.ReagentId),
                MinSize = new Vector2(160, 0),
                HorizontalExpand = true,
            };
            reagentButton.OnPressed += _ => OpenReagentPicker(step);
            row.AddChild(reagentButton);
        }

        if (StepNeedsAmount(step.Type))
        {
            var amountEdit = new LineEdit
            {
                Text = step.Amount.ToString("0.##"),
                MinSize = new Vector2(60, 0),
                PlaceHolder = step.Type switch
                {
                    ReactionChamberStepType.WaitSeconds => Loc.GetString("reaction-chamber-program-seconds-placeholder"),
                    ReactionChamberStepType.SetBeakerTemperature => Loc.GetString("reaction-chamber-program-kelvin-placeholder"),
                    _ => Loc.GetString("reaction-chamber-program-amount-placeholder"),
                },
            };
            amountEdit.OnTextChanged += _ =>
            {
                if (!float.TryParse(amountEdit.Text, out var amount))
                    return;

                if (step.Type == ReactionChamberStepType.SetBeakerTemperature)
                    amount = Math.Clamp(amount, 0f, SharedReactionChamber.MaxTargetBeakerTemperature);

                step.Amount = amount;
            };
            row.AddChild(amountEdit);
        }

        var removeButton = new Button { Text = "X", MinSize = new Vector2(28, 0) };
        removeButton.OnPressed += _ =>
        {
            program.Steps.RemoveAt(stepIndex);
            RebuildStepsEditor();
            RebuildProgramList();
            _programList[_selectedProgramIndex].Selected = true;
        };
        row.AddChild(removeButton);

        return row;
    }

    private void OpenReagentPicker(ReactionChamberStep step)
    {
        CloseReagentPicker();

        _reagentPicker = new ReactionChamberReagentPickerWindow(step.ReagentId);
        _reagentPicker.OnClose += () => _reagentPicker = null;
        _reagentPicker.ReagentSelected += reagent =>
        {
            step.ReagentId = reagent.ID;
            CloseReagentPicker();
            RebuildStepsEditor();
            RebuildProgramList();
            if (_selectedProgramIndex >= 0 && _selectedProgramIndex < _programList.Count)
                _programList[_selectedProgramIndex].Selected = true;
        };
        _reagentPicker.OpenCentered();
    }

    private string GetReagentButtonText(string reagentId)
    {
        if (string.IsNullOrWhiteSpace(reagentId))
            return Loc.GetString("reaction-chamber-program-select-reagent");

        if (_prototypes.TryIndex(reagentId, out ReagentPrototype? proto))
            return proto.LocalizedName;

        return reagentId;
    }

    private static bool StepNeedsReagent(ReactionChamberStepType type)
    {
        return type is ReactionChamberStepType.AddFromBufferToBeaker
            or ReactionChamberStepType.TakeFromBeakerToBuffer;
    }

    private static bool StepNeedsAmount(ReactionChamberStepType type)
    {
        return type is ReactionChamberStepType.AddFromBufferToBeaker
            or ReactionChamberStepType.TakeFromBeakerToBuffer
            or ReactionChamberStepType.WaitSeconds
            or ReactionChamberStepType.SetBeakerTemperature;
    }

    private static string GetStepTypeLabel(ReactionChamberStepType type)
    {
        return type switch
        {
            ReactionChamberStepType.AddFromBufferToBeaker => Loc.GetString("reaction-chamber-step-add-buffer"),
            ReactionChamberStepType.TakeFromBeakerToBuffer => Loc.GetString("reaction-chamber-step-take-buffer"),
            ReactionChamberStepType.StopBeakerReactions => Loc.GetString("reaction-chamber-step-stop"),
            ReactionChamberStepType.ResumeBeakerReactions => Loc.GetString("reaction-chamber-step-resume"),
            ReactionChamberStepType.WaitSeconds => Loc.GetString("reaction-chamber-step-wait-seconds"),
            ReactionChamberStepType.WaitForReaction => Loc.GetString("reaction-chamber-step-wait-reaction"),
            ReactionChamberStepType.SetBeakerTemperature => Loc.GetString("reaction-chamber-step-set-temperature"),
            _ => type.ToString(),
        };
    }

    private void SavePrograms()
    {
        ProgramsSaved?.Invoke(_programs);
        Close();
    }
}
