// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only.

internal sealed class RoguelikeInteraction
{
    private const int MapWidth = 15;
    private const int MapHeight = 9;
    private const int MaxHealth = 12;
    private const string CssRoute = "roguelike-styles.css";
    private const string JsRoute = "roguelike-controls.js";

    private readonly IResource _parentResource;
    private readonly IDistributedApplicationBuilder _builder;
    private readonly object _gameLock = new();
    private readonly SemaphoreSlim _gameChanged = new(0);
    private readonly Random _random = new();
    private readonly Dictionary<string, string> _resourceColors = new(StringComparer.OrdinalIgnoreCase);

    private readonly Tile[,] _tiles = new Tile[MapWidth, MapHeight];
    private readonly List<Monster> _monsters = [];
    private readonly List<string> _combatLog = [];

    private ResourceCommandService? _commandService;
    private Cell _player;
    private Cell? _potion;
    private int _health;
    private int _level;
    private int _turn;

    public RoguelikeInteraction(IResourceBuilder<ProjectResource> parentResource)
    {
        _parentResource = parentResource.Resource;
        _builder = parentResource.ApplicationBuilder;
        AssignResourceColors();
        StartNewRun(notify: false);
    }

    public void Register(IDistributedApplicationBuilder builder)
    {
        AddCommands(builder);

        builder.OnBeforeStart((@event, ct) =>
        {
            var interactionService = @event.Services.GetRequiredService<IInteractionService>();
            _commandService = @event.Services.GetRequiredService<ResourceCommandService>();
            RegisterPage(interactionService);

            return Task.CompletedTask;
        });
    }

    private void RegisterPage(IInteractionService interactionService)
    {
        var css = LoadEmbeddedTextResource("RoguelikeStyles.css");
        interactionService.RegisterAsset(CssRoute, "text/css", Encoding.UTF8.GetBytes(css));

        var js = LoadEmbeddedTextResource("RoguelikeControls.js");
        interactionService.RegisterAsset(JsRoute, "application/javascript", Encoding.UTF8.GetBytes(js));

        interactionService.RegisterPage("roguelike", new PageContext
        {
            Title = "Roguelike",
            EnableHtml = true,
            StyleIncludes = [CssRoute],
            ScriptIncludes = [JsRoute],
            OnVisit = async visitContext =>
            {
                while (!visitContext.CancellationToken.IsCancellationRequested)
                {
                    await visitContext.SendMarkdownAsync(BuildHtml(), visitContext.CancellationToken);
                    await _gameChanged.WaitAsync(visitContext.CancellationToken);
                }
            }
        });

        interactionService.RegisterMenuButton(new MenuButtonOptions
        {
            IconName = "Shield",
            Text = "Roguelike",
            Tooltip = "Explore a tiny dungeon",
            Url = "/pages/roguelike"
        });
    }

    private void AddCommands(IDistributedApplicationBuilder builder)
    {
        var commands = builder.AddCommandGroup("roguelike-commands", _parentResource);

        AddMoveCommand(commands, "move-up", "Move up", 0, -1, "ChevronUp");
        AddMoveCommand(commands, "move-down", "Move down", 0, 1, "ChevronDown");
        AddMoveCommand(commands, "move-left", "Move left", -1, 0, "ChevronLeft");
        AddMoveCommand(commands, "move-right", "Move right", 1, 0, "ChevronRight");

        commands.WithCommand(
            name: "new-run",
            displayName: "New run",
            executeCommand: _ =>
            {
                StartNewRun(notify: true);
                return Task.FromResult(CommandResults.Success("Started a new run."));
            },
            commandOptions: new CommandOptions
            {
                Description = "Start a new dungeon run.",
                IconName = "ArrowReset"
            });
    }

    private void AddMoveCommand(IResourceBuilder<CommandGroupResource> commands, string name, string displayName, int dx, int dy, string iconName)
    {
        commands.WithCommand(
            name,
            displayName,
            executeCommand: _ => Task.FromResult(Move(dx, dy)),
            commandOptions: new CommandOptions
            {
                Description = displayName,
                IconName = iconName
            });
    }

    private ExecuteCommandResult Move(int dx, int dy)
    {
        string message;

        lock (_gameLock)
        {
            if (_health <= 0)
            {
                AddLog("💀 You are dead. Start a new run to try again.");
                message = "You are dead.";
            }
            else
            {
                _turn++;
                var target = new Cell(_player.X + dx, _player.Y + dy);

                if (!IsInBounds(target) || _tiles[target.X, target.Y] == Tile.Wall)
                {
                    AddLog("🧱 You bump into a wall.");
                    message = "You bump into a wall.";
                }
                else if (FindMonster(target) is { } monster)
                {
                    message = Attack(monster);
                }
                else
                {
                    _player = target;

                    if (_potion == _player)
                    {
                        _health = Math.Min(MaxHealth, _health + 3);
                        _potion = null;
                        AddLog("💖 You drink a glowing potion and recover three hearts.");
                    }

                    if (_tiles[_player.X, _player.Y] == Tile.Stairs)
                    {
                        if (_monsters.Count == 0)
                        {
                            _level++;
                            _health = Math.Min(MaxHealth, _health + 1);
                            GenerateMap();
                            AddLog("🚪 You descend to the next dungeon level.");
                        }
                        else
                        {
                            AddLog("🔒 The exit is sealed while monsters remain.");
                        }
                    }

                    message = "You move.";
                }

                // Monsters take their turn after the player (if still alive).
                if (_health > 0)
                {
                    MoveMonsters();
                }
            }
        }

        NotifyChanged();
        return CommandResults.Success(message);
    }

    private string Attack(Monster monster)
    {
        var damage = _random.Next(2, 5);
        monster.Health -= damage;
        AddMonsterLog(monster, $"⚔️ You hit the {monster.Name} for {damage}.");

        if (monster.Health <= 0)
        {
            _monsters.Remove(monster);
            AddMonsterLog(monster, $"☠️ The {monster.Name} falls.");

            // If this monster is bound to a resource, stop that resource.
            if (monster.ResourceName is not null)
            {
                _ = StopResourceAsync(monster.ResourceName);
            }

            if (_monsters.Count == 0)
            {
                AddLog("✨ The dungeon goes quiet. Find the door to descend.");
            }

            return $"Defeated {monster.Name}.";
        }

        return $"Hit {monster.Name}.";
    }

    private async Task StopResourceAsync(string resourceName)
    {
        if (_commandService is { } commandService)
        {
            await commandService.ExecuteCommandAsync(resourceName, "stop").ConfigureAwait(false);
        }
    }

    private void MoveMonsters()
    {
        // Iterate over a snapshot so removals during iteration are safe.
        foreach (var monster in _monsters.ToArray())
        {
            var dist = Math.Abs(monster.Position.X - _player.X) + Math.Abs(monster.Position.Y - _player.Y);

            Cell target;
            if (dist <= 2)
            {
                // Adjacent or nearly adjacent — move towards the player.
                var stepX = Math.Sign(_player.X - monster.Position.X);
                var stepY = Math.Sign(_player.Y - monster.Position.Y);

                // Prefer the axis with the larger gap; if equal, pick one randomly.
                var dx = Math.Abs(_player.X - monster.Position.X);
                var dy = Math.Abs(_player.Y - monster.Position.Y);
                if (dx > dy || (dx == dy && _random.Next(2) == 0))
                {
                    target = new Cell(monster.Position.X + stepX, monster.Position.Y);
                }
                else
                {
                    target = new Cell(monster.Position.X, monster.Position.Y + stepY);
                }
            }
            else
            {
                // Random movement — pick a cardinal direction.
                var direction = _random.Next(4);
                target = direction switch
                {
                    0 => new Cell(monster.Position.X, monster.Position.Y - 1),
                    1 => new Cell(monster.Position.X, monster.Position.Y + 1),
                    2 => new Cell(monster.Position.X - 1, monster.Position.Y),
                    _ => new Cell(monster.Position.X + 1, monster.Position.Y)
                };
            }

            if (!IsInBounds(target) || _tiles[target.X, target.Y] == Tile.Wall)
            {
                continue;
            }

            if (target == _player)
            {
                // Monster attacks the player.
                _health -= monster.Attack;
                AddMonsterLog(monster, $"🩸 The {monster.Name} attacks you for {monster.Attack}.");

                if (_health <= 0)
                {
                    _health = 0;
                    AddLog("💀 You were killed.");
                    break;
                }
            }
            else if (FindMonster(target) is null)
            {
                monster.Position = target;
            }
        }
    }

    private void StartNewRun(bool notify)
    {
        lock (_gameLock)
        {
            _level = 1;
            _turn = 0;
            _health = MaxHealth;
            _combatLog.Clear();
            GenerateMap();
            AddLog("🎮 A new dungeon opens before you.");
        }

        if (notify)
        {
            NotifyChanged();
        }
    }

    private void GenerateMap()
    {
        _monsters.Clear();
        _potion = null;

        for (var y = 0; y < MapHeight; y++)
        {
            for (var x = 0; x < MapWidth; x++)
            {
                var border = x == 0 || y == 0 || x == MapWidth - 1 || y == MapHeight - 1;
                _tiles[x, y] = border || _random.NextDouble() < 0.13 ? Tile.Wall : Tile.Floor;
            }
        }

        _player = new Cell(1, 1);
        _tiles[_player.X, _player.Y] = Tile.Floor;

        var stairs = PickOpenCell();
        _tiles[stairs.X, stairs.Y] = Tile.Stairs;

        _potion = PickOpenCell();

        // Get resource names to use as monster labels.
        // Exclude the parent resource, aspire-prefixed resources, and hidden resources.
        var resourceNames = _builder.Resources
            .Where(r => r != _parentResource
                && !r.Name.StartsWith("aspire", StringComparison.OrdinalIgnoreCase)
                && !r.Annotations.Any(a => a.GetType().Name == "HiddenAnnotation"))
            .Select(r => r.Name)
            .ToList();

        // Shuffle resource names so different ones appear each run.
        for (var i = resourceNames.Count - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (resourceNames[i], resourceNames[j]) = (resourceNames[j], resourceNames[i]);
        }

        var resourceIndex = 0;
        var monsterCount = Math.Min(6, 3 + _level);
        for (var i = 0; i < monsterCount; i++)
        {
            var template = MonsterTemplate.All[_random.Next(MonsterTemplate.All.Length)];
            string? resourceName = null;
            string displayName;

            if (resourceIndex < resourceNames.Count)
            {
                resourceName = resourceNames[resourceIndex++];
                displayName = $"{template.Name} ({resourceName})";
            }
            else
            {
                displayName = template.Name;
            }

            _monsters.Add(new Monster(displayName, template.Emoji, template.Health + (_level / 3), template.Attack, PickOpenCell(), resourceName));
        }
    }

    private Cell PickOpenCell()
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            var cell = new Cell(_random.Next(1, MapWidth - 1), _random.Next(1, MapHeight - 1));
            if (_tiles[cell.X, cell.Y] == Tile.Floor && cell != _player && _potion != cell && FindMonster(cell) is null)
            {
                return cell;
            }
        }

        for (var y = 1; y < MapHeight - 1; y++)
        {
            for (var x = 1; x < MapWidth - 1; x++)
            {
                var cell = new Cell(x, y);
                if (_tiles[x, y] == Tile.Floor && cell != _player && FindMonster(cell) is null)
                {
                    return cell;
                }
            }
        }

        return _player;
    }

    private string BuildHtml()
    {
        lock (_gameLock)
        {
            var sb = new StringBuilder();
            sb.AppendLine("## 🗡️ Roguelike Dungeon");
            sb.Append("<div class=\"roguelike\">");
            sb.Append("<div class=\"roguelike-layout\">");

            // Map (top-left)
            sb.Append("<div class=\"roguelike-map\">");
            for (var y = 0; y < MapHeight; y++)
            {
                sb.Append("<div class=\"row\">");
                for (var x = 0; x < MapWidth; x++)
                {
                    sb.Append(GetCellEmoji(new Cell(x, y)));
                }
                sb.Append("</div>");
            }
            sb.Append("</div>");

            // Sidebar
            sb.Append("<div class=\"roguelike-sidebar\">");
            sb.Append(CultureInfo.InvariantCulture, $"<div><span class=\"label\">Health:</span> <span class=\"hearts\">{RenderHearts()}</span></div>");
            sb.Append(CultureInfo.InvariantCulture, $"<div><span class=\"label\">Level:</span> {_level}</div>");
            sb.Append(CultureInfo.InvariantCulture, $"<div><span class=\"label\">Turn:</span> {_turn}</div>");
            sb.Append(CultureInfo.InvariantCulture, $"<div><span class=\"label\">Enemies:</span> {_monsters.Count}</div>");
            sb.Append("<hr/>");
            sb.Append("<div class=\"legend\">🧙 you &nbsp; 🧱 wall &nbsp; 🚪 exit &nbsp; 💖 potion</div>");
            sb.Append("<hr/>");

            if (_monsters.Count > 0)
            {
                var nearest = _monsters
                    .OrderBy(m => Math.Abs(m.Position.X - _player.X) + Math.Abs(m.Position.Y - _player.Y))
                    .First();
                sb.Append(CultureInfo.InvariantCulture, $"<div><span class=\"label\">Nearest:</span> {nearest.Emoji} {RenderMonsterNameHtml(nearest)} ({nearest.Health} hp)</div>");
            }
            else
            {
                sb.Append("<div><strong>All clear</strong> — find the 🚪 exit</div>");
            }

            sb.Append("<hr/>");
            sb.Append("<div class=\"label\">Combat log:</div>");
            sb.Append("<div class=\"roguelike-log\">");
            foreach (var entry in _combatLog)
            {
                sb.Append(CultureInfo.InvariantCulture, $"<div class=\"entry\">{entry}</div>");
            }
            sb.Append("</div>");
            sb.Append("</div>"); // sidebar

            // Controls (bottom-left)
            sb.Append("<div class=\"roguelike-controls\">");
            sb.Append("<span class=\"empty\"></span>");
            sb.Append("<a data-command=\"move-up\" data-resource=\"roguelike-commands\">⬆️</a>");
            sb.Append("<span class=\"empty\"></span>");
            sb.Append("<a data-command=\"move-left\" data-resource=\"roguelike-commands\">⬅️</a>");
            sb.Append("<a data-command=\"move-down\" data-resource=\"roguelike-commands\">⬇️</a>");
            sb.Append("<a data-command=\"move-right\" data-resource=\"roguelike-commands\">➡️</a>");
            sb.Append("</div>");

            // New Game button (bottom-right)
            sb.Append("<div class=\"roguelike-newgame\">");
            sb.Append("<a data-command=\"new-run\" data-resource=\"roguelike-commands\" class=\"newgame-btn\">🔄 New Game</a>");
            sb.Append("</div>");

            sb.Append("</div>"); // layout
            sb.Append("</div>"); // roguelike
            return sb.ToString();
        }
    }

    private string GetCellEmoji(Cell cell)
    {
        if (cell == _player)
        {
            return _health <= 0 ? "💀" : "🧙";
        }

        if (FindMonster(cell) is { } monster)
        {
            return monster.Emoji;
        }

        if (_potion == cell)
        {
            return "💖";
        }

        return _tiles[cell.X, cell.Y] switch
        {
            Tile.Wall => "🧱",
            Tile.Stairs => "🚪",
            _ => "⬜"
        };
    }

    private string RenderHearts()
    {
        return string.Concat(Enumerable.Repeat("❤️", _health)) +
               string.Concat(Enumerable.Repeat("🖤", MaxHealth - _health));
    }

    private Monster? FindMonster(Cell cell)
    {
        return _monsters.FirstOrDefault(m => m.Position == cell);
    }

    private static bool IsInBounds(Cell cell)
    {
        return cell.X >= 0 && cell.X < MapWidth && cell.Y >= 0 && cell.Y < MapHeight;
    }

    private void AddLog(string message)
    {
        _combatLog.Insert(0, HtmlEncode(message));
        if (_combatLog.Count > 5)
        {
            _combatLog.RemoveAt(_combatLog.Count - 1);
        }
    }

    private void AddLog(IFormatProvider provider, FormattableString message)
    {
        AddLog(message.ToString(provider));
    }

    /// <summary>
    /// Adds a combat log entry that includes the monster's name rendered with a colored resource badge.
    /// The message should contain the monster's plain-text Name; it will be replaced with the HTML version.
    /// </summary>
    private void AddMonsterLog(Monster monster, string message)
    {
        var htmlName = RenderMonsterNameHtml(monster);
        var htmlMessage = HtmlEncode(message).Replace(HtmlEncode(monster.Name), htmlName, StringComparison.Ordinal);
        _combatLog.Insert(0, htmlMessage);
        if (_combatLog.Count > 5)
        {
            _combatLog.RemoveAt(_combatLog.Count - 1);
        }
    }

    private static string HtmlEncode(string value)
    {
        return System.Net.WebUtility.HtmlEncode(value);
    }

    /// <summary>
    /// Renders a monster's name as HTML. If the monster is bound to a resource,
    /// the resource name is displayed in a badge with the resource's dashboard color.
    /// </summary>
    private string RenderMonsterNameHtml(Monster monster)
    {
        if (monster.ResourceName is null)
        {
            return HtmlEncode(monster.Name);
        }

        // Name format is "creature (resourceName)" — render the resource part as a colored badge.
        var creatureName = monster.Name.AsSpan(0, monster.Name.IndexOf('(') - 1);
        var color = _resourceColors.GetValueOrDefault(monster.ResourceName, "var(--accent-teal)");
        return string.Create(CultureInfo.InvariantCulture, $"{HtmlEncode(creatureName.ToString())} <span class=\"resource-badge\" style=\"background:{color}\">{HtmlEncode(monster.ResourceName)}</span>");
    }

    /// <summary>
    /// Assigns colors to resource names using the same palette order as the dashboard's ColorGenerator.
    /// </summary>
    private void AssignResourceColors()
    {
        // Same palette used by Aspire.Dashboard's ColorGenerator for visual consistency.
        string[] palette =
        [
            "var(--accent-teal)", "var(--accent-marigold)", "var(--accent-brass)",
            "var(--accent-peach)", "var(--accent-coral)", "var(--accent-royal-blue)",
            "var(--accent-orchid)", "var(--accent-brand-blue)", "var(--accent-seafoam)",
            "var(--accent-mink)", "var(--accent-cyan)", "var(--accent-gold)",
            "var(--accent-bronze)", "var(--accent-orange)", "var(--accent-rust)",
            "var(--accent-navy)", "var(--accent-berry)", "var(--accent-ocean)",
            "var(--accent-jade)", "var(--accent-olive)"
        ];

        var names = _builder.Resources
            .Where(r => !r.Annotations.Any(a => a.GetType().Name == "HiddenAnnotation"))
            .Select(r => r.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var i = 0; i < names.Count; i++)
        {
            _resourceColors[names[i]] = palette[i % palette.Length];
        }
    }

    private static string LoadEmbeddedTextResource(string fileName)
    {
        var resourceName = $"Stress.AppHost.Resources.{fileName}";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"{fileName} embedded resource not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void NotifyChanged()
    {
        _gameChanged.Release();
    }

    private enum Tile
    {
        Floor,
        Wall,
        Stairs
    }

    private readonly record struct Cell(int X, int Y);

    private sealed class Monster(string name, string emoji, int health, int attack, Cell position, string? resourceName)
    {
        public string Name { get; } = name;
        public string Emoji { get; } = emoji;
        public int Attack { get; } = attack;
        public Cell Position { get; set; } = position;
        public int Health { get; set; } = health;
        public string? ResourceName { get; } = resourceName;
    }

    private sealed record MonsterTemplate(string Name, string Emoji, int Health, int Attack)
    {
        public static readonly MonsterTemplate[] All =
        [
            new("goblin", "👺", 3, 2),
            new("bat", "🦇", 2, 1),
            new("slime", "🟢", 2, 1),
            new("orc", "👹", 5, 2),
            new("spider", "🕷️", 2, 1),
            new("snake", "🐍", 3, 2),
            new("ghost", "👻", 3, 1),
            new("wolf", "🐺", 3, 2),
            new("rat", "🐀", 2, 1),
            new("dragon", "🐉", 6, 3),
            new("skeleton", "🦴", 3, 2),
            new("mushroom", "🍄", 2, 1),
            new("scorpion", "🦂", 3, 2),
            new("lizard", "🦎", 3, 1),
            new("eagle", "🦅", 4, 2),
            new("bear", "🐻", 6, 3),
            new("demon", "😈", 5, 3),
            new("alien", "👾", 3, 2),
            new("troll", "🧌", 6, 2),
            new("zombie", "🧟", 4, 2),
            new("vampire", "🧛", 4, 2),
            new("beetle", "🪲", 2, 1),
            new("crow", "🐦‍⬛", 2, 1),
            new("boar", "🐗", 4, 2)
        ];
    }
}
