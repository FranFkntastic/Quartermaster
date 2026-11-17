using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Windows;
using Franthropy.Filtering.Compilation;
using Franthropy.Filtering.Documentation;
using Franthropy.Filtering.Semantics;

namespace RQ.UI;

internal static class SearchReference
{
    private static readonly Vector4 CodeColor = new(.65f, .82f, 1f, 1f);
    private static readonly string QuestionIcon = FontAwesomeIcon.Question.ToIconString();
    private sealed record Row(string Code, string Description, string Value);
    private sealed record FieldRow(string Name, string Operators, string Values, string Description);
    private static readonly Row[] PlanRows =
    [
        new("cotton", "Add one item", ""),
        new("@Group name", "Add every item in a group", "@"),
        new("#Ingredient", "Pick items from a category", "#"),
        new(">Item name x3", "Add materials for 3 crafts", ">"),
    ];

    internal sealed class Button
    {
        private readonly string id;
        private readonly string label;
        private readonly Func<System.Text.Json.JsonElement?, AgentBridgeUiActionResult> open;
        private bool requested;
        private object? cachedContext;
        private FilterReferenceModel? reference;
        private Row[] examples = [];
        private FieldRow[] fields = [];
        private string fieldsHeading = string.Empty;
        private float tableWidth;

        public Button(string id, string label)
        {
            this.id = id;
            this.label = label;
            open = _ =>
            {
                requested = true;
                return AgentBridgeUiActionResult.Ok("Search reference requested.");
            };
        }

        public bool IsOpen => ImGui.IsPopupOpen(id);

        public void Draw(AgentBridgeUiReviewRegistry registry)
        {
            var clicked = ImGuiComponents.IconButton(id, FontAwesomeIcon.Question);
            registry.RegisterLastAction(id, label, AgentBridgeUiControlKind.Button,
                true, false, null, null, open);
            if (clicked || requested)
            {
                requested = false;
                ImGui.OpenPopup(id);
            }
        }

        private bool Begin()
        {
            if (!IsOpen) return false;
            var anchor = ImGui.GetItemRectMax();
            var windowPos = ImGui.GetWindowPos();
            var width = Math.Min(540f, Math.Max(200f, ImGui.GetWindowSize().X));
            ImGui.SetNextWindowPos(new Vector2(Math.Max(windowPos.X, anchor.X - width), anchor.Y));
            ImGui.SetNextWindowSizeConstraints(new Vector2(width, 0), new Vector2(width, float.MaxValue));
            ImGui.SetNextWindowBgAlpha(0.98f);
            tableWidth = width - (ImGui.GetStyle().WindowPadding.X * 2f);
            var owner = DalamudOwnedWindowSurface.Capture();
            owner.ApplyToNextWindow();
            if (!ImGui.BeginPopup(id)) return false;
            owner.KeepCurrentWindowAboveOwner();
            return true;
        }

        public string? DrawStock<T>(FilterContext<T> context)
        {
            if (!Begin()) return null;
            if (!ReferenceEquals(cachedContext, context))
            {
                reference = FilterReferenceGenerator.Create(context);
                var available = reference.Fields.Where(field => field.IsAvailable).ToArray();
                fields = available.Select(field => new FieldRow(
                    string.Join(", ", new[] { field.PreferredName }.Concat(field.Aliases).Distinct()),
                    string.Join(" ", field.Operators.Where(op => op is not ("==" or "!==" or "!="))) +
                        (field.ValueKind is FilterValueKind.Integer or FilterValueKind.Decimal ? " .." : ""),
                    field.Key == "item.name" ? "item names"
                    : field.Values.Count > 0
                        ? string.Join(", ", field.Values.Take(4).Select(value =>
                              value.Aliases.Count > 0 ? value.Aliases[0] : value.DisplayName)) +
                          (field.Values.Count > 4 ? ", …" : "")
                        : field.ValueKind switch
                        {
                            FilterValueKind.Integer or FilterValueKind.Decimal => "number",
                            FilterValueKind.Boolean => "yes / no",
                            _ => field.ValueKind.ToString().ToLowerInvariant(),
                        }, field.Description)).ToArray();
                fieldsHeading = $"Fields ({fields.Length})";
                var rows = new List<Row>();
                Add("cotton", "Name contains \"cotton\"", "item.name");
                Add("ilvl>=690", "Item level 690 or higher", "item.itemLevel");
                Add("level:80..90", "Equip level 80 through 90", "item.equipLevel");
                if (available.Any(field => field.Key == "item.slot"))
                    Add("job:WHM slot:body", "White Mage body gear", "item.job");
                Add("job:(PLD|WAR)", "Either job", "item.job");
                if (reference.Predicates.Any(predicate => predicate.Qualifier == "is" && predicate.Specifier == "hq"))
                    Add("is:hq", "High quality only", null);
                var categories = available.FirstOrDefault(field => field.Key == "item.uiCategory");
                if (categories is { Values.Count: > 1 })
                    Add($"category:{Quote(categories.Values[0].DisplayName)} OR category:{Quote(categories.Values[1].DisplayName)}",
                        "Either category", "item.uiCategory");
                var retainer = available.FirstOrDefault(field => field.Key == "ownership.retainer");
                if (retainer is { Values.Count: > 0 })
                {
                    Add($"retainer:{Quote(retainer.Values[0].DisplayName, always: true)}", "Held by that retainer", "ownership.retainer");
                }
                Add("tradable:false", "Untradable items", "item.tradable");
                examples = rows.ToArray();
                cachedContext = context;

                void Add(string expression, string description, string? key)
                {
                    if (key is not null && !available.Any(field => field.Key == key)) return;
                    // Context resolvers may contain only the current stock's jobs/categories.
                    // Never offer a clickable expression that produces a diagnostic here.
                    if (FilterCompiler.Compile(expression, context).Diagnostics.Count == 0)
                        rows.Add(new(expression, description, expression));
                }
            }
            ImGui.TextColored(CodeColor, "Examples");
            var selected = DrawRows("examples", examples, tableWidth);
            ImGui.TextColored(CodeColor, "Combine");
            if (BeginTwoColumns("combine", tableWidth))
            {
                TextRow("a b   AND   &&", "Both");
                TextRow("OR   |", "Either");
                TextRow("NOT   !   -", "Exclude");
                TextRow("( )   \"…\"", "Group · exact phrase");
                ImGui.EndTable();
            }
            if (ImGui.CollapsingHeader(fieldsHeading) &&
                ImGui.BeginTable("fields", 3, ImGuiTableFlags.SizingFixedFit, new Vector2(tableWidth, 0)))
            {
                ImGui.TableSetupColumn("Field", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("Compare", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("Values", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableHeadersRow();
                foreach (var field in fields)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    var start = ImGui.GetCursorPos();
                    ImGui.TableSetColumnIndex(2);
                    var height = Math.Max(ImGui.GetTextLineHeight(),
                        ImGui.CalcTextSize(field.Values, false, Math.Max(1f, ImGui.GetContentRegionAvail().X)).Y);
                    ImGui.TableSetColumnIndex(0);
                    ImGui.PushID(field.Name);
                    ImGui.Selectable("##field", false,
                        ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.DontClosePopups, new Vector2(0, height));
                    Tooltip(field.Description);
                    ImGui.PopID();
                    ImGui.SetCursorPos(start);
                    ImGui.TextColored(CodeColor, field.Name);
                    ImGui.TableNextColumn();
                    ImGui.TextColored(CodeColor, field.Operators);
                    ImGui.TableNextColumn();
                    Wrapped(field.Values, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
                }
                ImGui.EndTable();
            }
            ImGui.EndPopup();
            return selected;
        }

        public string? DrawPlan()
        {
            if (!Begin()) return null;
            var selected = DrawRows("add", PlanRows, tableWidth);
            ImGui.EndPopup();
            return selected;
        }
    }

    public static float ButtonWidth
    {
        get
        {
            ImGui.PushFont(Dalamud.Interface.UiBuilder.IconFont);
            var width = ImGui.CalcTextSize(QuestionIcon).X + ImGui.GetStyle().FramePadding.X * 2;
            ImGui.PopFont();
            return width;
        }
    }

    private static bool BeginTwoColumns(string id, float width)
    {
        if (!ImGui.BeginTable(id, 2, ImGuiTableFlags.SizingFixedFit, new Vector2(width, 0))) return false;
        ImGui.TableSetupColumn("Code", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("Meaning", ImGuiTableColumnFlags.WidthStretch);
        return true;
    }

    private static string? DrawRows(string id, Row[] rows, float width)
    {
        string? selected = null;
        if (!BeginTwoColumns(id, width)) return null;
        foreach (var row in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.PushID(row.Code);
            var start = ImGui.GetCursorPos();
            if (ImGui.Selectable("##row", false, ImGuiSelectableFlags.SpanAllColumns))
            {
                selected = row.Value;
                ImGui.CloseCurrentPopup();
            }
            ImGui.PopID();
            ImGui.SetCursorPos(start);
            ImGui.TextColored(CodeColor, row.Code);
            ImGui.TableNextColumn();
            ImGui.TextDisabled(row.Description);
        }
        ImGui.EndTable();
        return selected;
    }

    private static void TextRow(string code, string description)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextColored(CodeColor, code);
        ImGui.TableNextColumn();
        ImGui.TextDisabled(description);
    }

    private static void Wrapped(string text, Vector4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.PushTextWrapPos(0);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }

    private static string Quote(string value, bool always = false) =>
        always || value.Any(c => char.IsWhiteSpace(c) || c is '"' or '(' or ')' or ':' or '|')
            ? $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\""
            : value;

    private static void Tooltip(string description)
    {
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(description);
    }
}
