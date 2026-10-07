// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Linq;
using Content.Shared._Ganimed.NPC.Soldier;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._Ganimed.NPC.Soldier.UI;

/// <summary>One command surface shared by the physical tablet and the existing admin NPC information panel.</summary>
public sealed class SoldierControlPanel : BoxContainer
{
    private readonly OptionButton _factions = new();
    private readonly OptionButton _squads = new();
    private readonly OptionButton _members = new();
    private readonly OptionButton _tasks = new();
    private readonly LineEdit _group = new() { PlaceHolder = Loc.GetString("soldier-control-group"), HorizontalExpand = true };
    private readonly LineEdit _target = new() { PlaceHolder = Loc.GetString("soldier-control-target"), HorizontalExpand = true };
    private readonly LineEdit _x = new() { Text = "0", MinWidth = 50 };
    private readonly LineEdit _y = new() { Text = "0", MinWidth = 50 };
    private readonly LineEdit _radius = new() { Text = "6", MinWidth = 50 };
    private readonly Label _status = new();
    private readonly Label _commander = new();
    private readonly Label _report = new() { FontColorOverride = Color.Gold };
    private List<string> _factionIds = new();
    private List<NetEntity> _squadIds = new();
    private List<NetEntity> _memberIds = new();
    private List<SoldierSquadInfo> _latest = new();
    private readonly SoldierMissionKind[] _kinds = Enum.GetValues<SoldierMissionKind>();
    public event Action<SoldierControlRequest>? Requested;

    public SoldierControlPanel()
    {
        Orientation = LayoutOrientation.Vertical;
        SeparationOverride = 3;
        AddChild(_factions);
        AddChild(_squads);
        AddChild(_members);
        AddChild(_commander);
        AddChild(_report);
        for (var i = 0; i < _kinds.Length; i++)
            _tasks.AddItem(Loc.GetString("soldier-mission-" + _kinds[i].ToString().ToLowerInvariant()), i);
        _tasks.OnItemSelected += args => _tasks.SelectId(args.Id);
        _tasks.SelectId(Array.IndexOf(_kinds, SoldierMissionKind.Escort));
        AddChild(_tasks);
        AddChild(_target);
        var point = new BoxContainer { Orientation = LayoutOrientation.Horizontal, SeparationOverride = 4 };
        point.AddChild(new Label { Text = Loc.GetString("soldier-control-point") });
        point.AddChild(_x);
        point.AddChild(_y);
        point.AddChild(new Label { Text = Loc.GetString("soldier-control-radius") });
        point.AddChild(_radius);
        AddChild(point);
        Button("soldier-control-issue", SoldierControlAction.Mission);
        AddChild(_group);
        var management = new BoxContainer { Orientation = LayoutOrientation.Horizontal, SeparationOverride = 4 };
        Button("soldier-control-create", SoldierControlAction.CreateSquad, management);
        Button("soldier-control-assign", SoldierControlAction.Assign, management);
        Button("soldier-control-hq", SoldierControlAction.Headquarters, management);
        AddChild(management);
        AddChild(_status);
        _factions.OnItemSelected += args =>
        {
            _factions.SelectId(args.Id);
            SetInfo(_latest);
        };
        _squads.OnItemSelected += args =>
        {
            _squads.SelectId(args.Id);
            ShowReport();
        };
        _members.OnItemSelected += args => _members.SelectId(args.Id);
    }

    private void Button(string text, SoldierControlAction action, BoxContainer? parent = null)
    {
        var button = new Button { Text = Loc.GetString(text), HorizontalExpand = true };
        button.OnPressed += _ => Send(action);
        (parent ?? this).AddChild(button);
    }

    private void Send(SoldierControlAction action)
    {
        if (_squads.SelectedId < 0 || _squads.SelectedId >= _squadIds.Count ||
            !Parse(_x.Text, out var x) || !Parse(_y.Text, out var y) || !Parse(_radius.Text, out var radius))
        {
            SetStatus(Loc.GetString("soldier-control-invalid"));
            return;
        }
        NetEntity? target = null;
        if (_target.Text.Length > 0)
        {
            if (!NetEntity.TryParse(_target.Text.Trim(), out var net))
            {
                SetStatus(Loc.GetString("soldier-control-invalid"));
                return;
            }
            target = net;
        }
        Requested?.Invoke(new SoldierControlRequest
        {
            Action = action, Squad = _squadIds[_squads.SelectedId],
            Member = _members.SelectedId >= 0 && _members.SelectedId < _memberIds.Count ? _memberIds[_members.SelectedId] : null,
            Target = target, Mission = _kinds[Math.Clamp(_tasks.SelectedId, 0, _kinds.Length - 1)],
            Group = _group.Text, X = x, Y = y, Radius = radius
        });
    }

    private static bool Parse(string text, out float value) =>
        float.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

    public void SetStatus(string text) => _status.Text = text;

    private void ShowReport()
    {
        var uid = _squads.SelectedId >= 0 && _squads.SelectedId < _squadIds.Count ? _squadIds[_squads.SelectedId] : default;
        var squad = _latest.FirstOrDefault(s => s.Squad == uid);
        _commander.Text = squad?.Commander ?? string.Empty;
        _report.Text = squad?.Decision ?? string.Empty;
    }

    public void SetInfo(List<SoldierSquadInfo> squads)
    {
        _latest = squads;
        var selectedFaction = _factions.SelectedId >= 0 && _factions.SelectedId < _factionIds.Count ? _factionIds[_factions.SelectedId] : "";
        _factionIds = squads.Select(s => s.Faction).Distinct().OrderBy(f => f).ToList();
        _factions.Clear();
        for (var i = 0; i < _factionIds.Count; i++)
        {
            var id = _factionIds[i];
            _factions.AddItem(Loc.TryGetString("soldier-faction-" + id, out var name) ? name : id, i);
        }
        var factionIndex = Math.Max(0, _factionIds.IndexOf(selectedFaction));
        if (_factionIds.Count > 0)
            _factions.SelectId(factionIndex);
        _factions.Visible = _factionIds.Count > 1;
        var selectedSquad = _squads.SelectedId >= 0 && _squads.SelectedId < _squadIds.Count ? _squadIds[_squads.SelectedId] : default;
        var selectedMember = _members.SelectedId >= 0 && _members.SelectedId < _memberIds.Count ? _memberIds[_members.SelectedId] : default;
        var shown = _factionIds.Count > 0 ? squads.Where(s => s.Faction == _factionIds[factionIndex]).ToList() : new();
        _squadIds = shown.Select(s => s.Squad).ToList();
        _squads.Clear();
        for (var i = 0; i < shown.Count; i++)
            _squads.AddItem(shown[i].Name, i);
        if (_squadIds.Count > 0)
            _squads.SelectId(Math.Max(0, _squadIds.IndexOf(selectedSquad)));
        var members = shown.SelectMany(s => s.Soldiers).ToList();
        _memberIds = members.Select(m => m.Entity).ToList();
        _members.Clear();
        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            var cls = Loc.TryGetString("soldier-class-" + member.Class, out var name) ? name : member.Class;
            _members.AddItem($"{member.Name} · {cls} · {member.Entity}", i);
        }
        if (_memberIds.Count > 0)
            _members.SelectId(Math.Max(0, _memberIds.IndexOf(selectedMember)));
        ShowReport();
    }
}
