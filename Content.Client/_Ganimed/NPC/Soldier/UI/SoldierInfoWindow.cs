// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Client.Stylesheets;
using Content.Shared._Ganimed.NPC.Soldier;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Utility;

namespace Content.Client._Ganimed.NPC.Soldier.UI;

/// <summary>
/// The info panel of the soldiers: the alert level of a squad, who commands it, the decisions of the commander in one line
/// and the feed of its thoughts. The panel has the settings an admin needs to keep it cheap: how often the server sends the
/// information, and whether the overlay over the soldiers is drawn.
/// </summary>
public sealed class SoldierInfoWindow : DefaultWindow
{
    /// <summary>
    /// How often the server may be asked for the information (seconds), and which of them is the default.
    /// </summary>
    private static readonly float[] Intervals = { 1f, 2f, 3f, 4f, 6f, 10f };
    private const int DefaultInterval = 2;

    /// <summary>
    /// The feed keeps this many lines, then it starts over.
    /// </summary>
    private const int FeedCapacity = 400;

    private readonly Label _alert = new() { StyleClasses = { StyleClass.LabelHeading } };
    private readonly Label _commander = new();
    private readonly Label _decision = new() { FontColorOverride = Color.Gold };
    private readonly OutputPanel _feed = new() { VerticalExpand = true, MinHeight = 180 };
    private readonly OptionButton _squads = new() { Visible = false };
    public readonly SoldierControlPanel Commands = new();
    private readonly CheckBox _zones = new() { Text = Loc.GetString("soldier-control-zones") };
    public event Action<bool>? ZonesChanged;

    private readonly OptionButton _interval = new();
    private readonly CheckBox _overlay = new() { Text = Loc.GetString("soldier-info-overlay"), Pressed = true };

    /// <summary>
    /// The squad on show, and the squads the selector offers.
    /// </summary>
    private NetEntity? _selected;
    private readonly List<NetEntity> _squadIds = new();

    /// <summary>
    /// What the feed has shown already (a thought is sent again with every answer of the server).
    /// </summary>
    private readonly HashSet<(NetEntity Squad, double Time, string Text)> _shown = new();
    private SoldierInfoEvent? _last;

    /// <summary>
    /// The admin has chosen another interval (seconds), or has switched the overlay.
    /// </summary>
    public event Action<float>? IntervalChanged;
    public event Action<bool>? OverlayChanged;

    public float Interval => Intervals[Math.Clamp(_interval.SelectedId, 0, Intervals.Length - 1)];

    public bool OverlayShown => _overlay.Pressed;
    public bool ZonesShown => _zones.Pressed;

    public SoldierInfoWindow()
    {
        Title = Loc.GetString("soldier-info-title");
        MinSize = new Vector2(440, 380);
        SetSize = new Vector2(600, 740);

        var root = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 4,
            VerticalExpand = true,
        };

        var settings = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            SeparationOverride = 6,
        };

        settings.AddChild(new Label { Text = Loc.GetString("soldier-info-interval") });

        for (var i = 0; i < Intervals.Length; i++)
        {
            _interval.AddItem(Loc.GetString("soldier-info-interval-option", ("seconds", (int) Intervals[i])), i);
        }

        _interval.SelectId(DefaultInterval);
        _interval.OnItemSelected += args =>
        {
            _interval.SelectId(args.Id);
            IntervalChanged?.Invoke(Interval);
        };

        settings.AddChild(_interval);
        settings.AddChild(_overlay);
        settings.AddChild(_zones);
        _zones.OnToggled += args => ZonesChanged?.Invoke(args.Pressed);
        _overlay.OnToggled += args => OverlayChanged?.Invoke(args.Pressed);

        _squads.OnItemSelected += args =>
        {
            _squads.SelectId(args.Id);
            _selected = args.Id >= 0 && args.Id < _squadIds.Count ? _squadIds[args.Id] : null;

            // Another squad: the feed starts over.
            _feed.Clear();
            _shown.Clear();

            if (_last != null)
                SetInfo(_last);
        };

        root.AddChild(settings);
        root.AddChild(Commands);
        root.AddChild(_squads);
        root.AddChild(_alert);
        root.AddChild(_commander);
        root.AddChild(_decision);
        root.AddChild(new Label { Text = Loc.GetString("soldier-info-thoughts") });
        root.AddChild(_feed);

        Contents.AddChild(root);
    }

    /// <summary>
    /// Shows what the server has said.
    /// </summary>
    public void SetInfo(SoldierInfoEvent info)
    {
        _last = info;
        Commands.SetInfo(info.Squads);

        if (info.Squads.Count == 0)
        {
            _alert.Text = Loc.GetString("soldier-info-no-squads");
            _commander.Text = string.Empty;
            _decision.Text = string.Empty;
            _squads.Visible = false;
            return;
        }

        UpdateSquads(info);

        var squad = info.Squads.Find(candidate => candidate.Squad == _selected) ?? info.Squads[0];
        _selected = squad.Squad;

        _alert.Text = Loc.GetString("soldier-info-alert", ("level", squad.Alert));
        _alert.FontColorOverride = SeverityColor(squad.Severity);
        _commander.Text = squad.Commander;
        _decision.Text = squad.Decision.Length == 0
            ? Loc.GetString("soldier-info-decision-none")
            : Loc.GetString("soldier-info-decision", ("decision", squad.Decision));

        if (_shown.Count > FeedCapacity)
        {
            _feed.Clear();
            _shown.Clear();
        }

        foreach (var thought in squad.Thoughts)
        {
            if (!_shown.Add((squad.Squad, thought.Time, thought.Text)))
                continue;

            var message = new FormattedMessage();
            message.PushColor(Color.Gray);
            message.AddText($"[{TimeSpan.FromSeconds(thought.Time):h\\:mm\\:ss}] ");
            message.Pop();
            message.AddText(thought.Text);

            _feed.AddMessage(message);
        }
    }

    /// <summary>
    /// The squads to choose from, if there are several.
    /// </summary>
    private void UpdateSquads(SoldierInfoEvent info)
    {
        var changed = info.Squads.Count != _squadIds.Count;

        for (var i = 0; !changed && i < info.Squads.Count; i++)
        {
            changed = info.Squads[i].Squad != _squadIds[i];
        }

        if (!changed)
            return;

        _squadIds.Clear();
        _squads.Clear();

        foreach (var squad in info.Squads)
        {
            _squads.AddItem(squad.Name, _squadIds.Count);
            _squadIds.Add(squad.Squad);
        }

        var index = _selected is { } selected ? _squadIds.IndexOf(selected) : -1;
        _squads.SelectId(index >= 0 ? index : 0);
        _squads.Visible = info.Squads.Count > 1;
    }

    private static Color SeverityColor(byte severity)
    {
        // The levels in the order of their severity: calm, caution, suspicion, search, alert.
        return severity switch
        {
            0 => Color.LightGreen,
            1 => Color.Yellow,
            2 => Color.Orange,
            3 => Color.OrangeRed,
            _ => Color.Red,
        };
    }
}
