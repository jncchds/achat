using System.Text.Json;
using AChat.Core.Models;
using AChat.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AChat.Api.Services;

/// <summary>One trait item as stored in a version snapshot.</summary>
public sealed record TraitItem(int Id, string Text);

/// <summary>Trait changes the bot asked for in its envelope (§4.7).</summary>
public sealed record TraitChanges(IReadOnlyList<string> Add, IReadOnlyList<int> Retire);

/// <summary>Limits for the bot's own trait changes (config <c>Turn:*</c>).</summary>
public sealed record TraitLimits(int MaxPerDay, int MaxItems, int MaxItemChars, int MaxTotalChars)
{
    public static TraitLimits From(IConfiguration? c) => From0(c ?? new ConfigurationBuilder().Build());

    private static TraitLimits From0(IConfiguration c) => new(
        c.GetValue("Turn:MaxTraitChangesPerDay", 3),
        c.GetValue("Turn:MaxTraits", 20),
        c.GetValue("Turn:MaxTraitChars", 200),
        c.GetValue("Turn:MaxTraitsTotalChars", 2000));
}

/// <summary>What survived the guardrails: texts to add, active ids to retire, and why the rest was dropped.</summary>
public sealed record TraitPlan(List<string> Add, List<int> Retire, List<string> Notes)
{
    public bool IsEmpty => Add.Count == 0 && Retire.Count == 0;
}

/// <summary>
/// The evolved layer as a list of short items (§2.4, §4.7). Every change — bot turn,
/// human edit, revert, consolidation — appends a <see cref="PersonaVersion"/> carrying a
/// snapshot of the active items plus the rendered text.
///
/// Legacy: a bot whose evolved layer was written as prose has no items. On first read
/// (<see cref="EnsureLegacyAsync"/>) the newest prose version becomes one legacy item and
/// that version gets the matching snapshot; older prose versions are read as one item too.
/// </summary>
public static class PersonaTraits
{
    // ── pure helpers ────────────────────────────────────────────────────────

    /// <summary>Traits are offered to, and accepted from, only the creator's turns with evolution on (§4.7).</summary>
    public static bool CanChange(Bot bot, int userId) => bot.OwnerId == userId && bot.EvolutionEnabled;

    /// <summary>The prose form kept in <c>PersonaVersion.Content</c> (and shown in the prompt without ids).</summary>
    public static string Render(IEnumerable<TraitItem> items) =>
        string.Join("\n", items.Select(i => "- " + i.Text));

    /// <summary>The evolved block of the prompt; ids are shown only when the bot may retire by id.</summary>
    public static string RenderForPrompt(IEnumerable<TraitItem> items, bool withIds) =>
        string.Join("\n", items.Select(i => withIds ? $"[{i.Id}] {i.Text}" : "- " + i.Text));

    public static string Serialize(IEnumerable<TraitItem> items) => JsonSerializer.Serialize(items);

    /// <summary>The items of a version; a version without a snapshot is one legacy item (id 0) or empty.</summary>
    public static List<TraitItem> ItemsOf(PersonaVersion v)
    {
        if (!string.IsNullOrEmpty(v.Snapshot))
        {
            try { return JsonSerializer.Deserialize<List<TraitItem>>(v.Snapshot) ?? []; }
            catch (JsonException) { /* fall through */ }
        }
        return string.IsNullOrWhiteSpace(v.Content) ? [] : [new TraitItem(0, v.Content.Trim())];
    }

    public static string Clip(string text, int max)
    {
        text = text.Trim();
        if (text.Length <= max) return text;
        var cut = text[..max];
        var sp = cut.LastIndexOf(' ');
        return (sp > max / 2 ? cut[..sp] : cut).TrimEnd();
    }

    /// <summary>
    /// The guardrails (§4.7) as a pure function. Retires are applied first (unknown or
    /// inactive ids ignored), then adds; every applied change spends the rolling daily
    /// budget. Adds are refused when the list is full or over the total size cap.
    /// A cooldown after a human edit or revert, or a non-creator / evolution-off turn,
    /// drops everything. Nothing is ever queued.
    /// </summary>
    public static TraitPlan Plan(TraitChanges req, IReadOnlyList<TraitItem> active, int changesLast24h,
        bool allowed, bool inCooldown, TraitLimits lim)
    {
        var plan = new TraitPlan([], [], []);
        if (!allowed) { plan.Notes.Add("TraitsIgnored: only the creator's turns with evolution on can change traits"); return plan; }
        if (inCooldown) { plan.Notes.Add("TraitsDropped: cooldown after a human edit or revert"); return plan; }

        var budget = Math.Max(0, lim.MaxPerDay - changesLast24h);
        var left = active.ToList();

        foreach (var id in req.Retire.Distinct())
        {
            var item = left.FirstOrDefault(i => i.Id == id);
            if (item is null) { plan.Notes.Add($"TraitRetireIgnored: no active trait [{id}]"); continue; }
            if (budget == 0) { plan.Notes.Add($"TraitRetireDropped: daily limit ({lim.MaxPerDay}) reached for [{id}]"); continue; }
            budget--;
            plan.Retire.Add(id);
            left.Remove(item);
        }

        var total = left.Sum(i => i.Text.Length);
        foreach (var raw in req.Add)
        {
            var text = Clip(raw, lim.MaxItemChars);
            if (text.Length == 0) continue;
            if (left.Any(i => string.Equals(i.Text, text, StringComparison.OrdinalIgnoreCase))
                || plan.Add.Any(a => string.Equals(a, text, StringComparison.OrdinalIgnoreCase)))
            { plan.Notes.Add($"TraitAddIgnored: already present '{Clip(text, 40)}'"); continue; }
            if (budget == 0) { plan.Notes.Add($"TraitAddDropped: daily limit ({lim.MaxPerDay}) reached '{Clip(text, 40)}'"); continue; }
            if (left.Count + plan.Add.Count >= lim.MaxItems)
            { plan.Notes.Add($"TraitAddDropped: {lim.MaxItems} traits active — retire one first '{Clip(text, 40)}'"); continue; }
            if (total + text.Length > lim.MaxTotalChars)
            { plan.Notes.Add($"TraitAddDropped: over {lim.MaxTotalChars} characters — retire one first '{Clip(text, 40)}'"); continue; }
            budget--;
            total += text.Length;
            plan.Add.Add(text);
        }
        return plan;
    }

    /// <summary>
    /// The same size caps for a human add (<paramref name="replacingId"/> null) or edit as the bot's
    /// envelope gets (§4.7): per-item length, item count, total characters. Returns the 400 message,
    /// or null when fine. An edit that does not grow the list or the total is always allowed, so a
    /// list that is already over a cap (legacy, or after a limit change) can still be trimmed.
    /// </summary>
    public static string? CheckHumanEdit(IReadOnlyList<TraitItem> active, int? replacingId, string text, TraitLimits lim)
    {
        text = text.Trim();
        if (text.Length == 0) return replacingId is null ? "The trait is empty." : "The trait is empty — remove it instead.";
        var old = replacingId is null ? null : active.FirstOrDefault(i => i.Id == replacingId);
        if (text.Length > lim.MaxItemChars && text != old?.Text)
            return $"A trait is at most {lim.MaxItemChars} characters.";
        if (replacingId is null && active.Count >= lim.MaxItems)
            return $"The list is full ({lim.MaxItems} traits) — retire or merge one first.";
        var total = active.Sum(i => i.Text.Length) - (old?.Text.Length ?? 0) + text.Length;
        var before = active.Sum(i => i.Text.Length);
        if (total > lim.MaxTotalChars && total > before)
            return $"The traits together are at most {lim.MaxTotalChars} characters ({total} with this change) — retire or shorten one first.";
        return null;
    }

    /// <summary>Consolidation trigger: more active items than the threshold and the cooldown elapsed (newest version of any source).</summary>
    public static bool ShouldConsolidate(int activeCount, int above, DateTime? lastVersionAt, DateTime cooldownCutoff) =>
        activeCount > above && (lastVersionAt is null || lastVersionAt <= cooldownCutoff);

    /// <summary>A consolidation result must be a non-empty JSON array of non-blank strings (fence tolerated).</summary>
    public static List<string>? ParseConsolidation(string? raw, int maxItemChars)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s.StartsWith("```"))
        {
            var nl = s.IndexOf('\n');
            if (nl >= 0) s = s[(nl + 1)..];
            var end = s.LastIndexOf("```", StringComparison.Ordinal);
            if (end >= 0) s = s[..end];
        }
        var start = s.IndexOf('[');
        var last = s.LastIndexOf(']');
        if (start < 0 || last < start) return null;
        try
        {
            var arr = JsonSerializer.Deserialize<List<JsonElement>>(s[start..(last + 1)]);
            if (arr is null) return null;
            var result = new List<string>();
            foreach (var el in arr)
            {
                if (el.ValueKind != JsonValueKind.String) return null;
                var t = Clip(el.GetString() ?? "", maxItemChars);
                if (t.Length == 0) continue;
                if (!result.Contains(t, StringComparer.OrdinalIgnoreCase)) result.Add(t);
            }
            return result.Count > 0 ? result : null;
        }
        catch (JsonException) { return null; }
    }

    // ── database side ───────────────────────────────────────────────────────

    public static async Task<List<BotTrait>> ActiveRowsAsync(AppDbContext db, int botId, CancellationToken ct = default)
    {
        await EnsureLegacyAsync(db, botId, ct);
        return await db.BotTraits.Where(t => t.BotId == botId && t.RetiredAt == null)
            .OrderBy(t => t.Id).ToListAsync(ct);
    }

    public static async Task<List<TraitItem>> ActiveAsync(AppDbContext db, int botId, CancellationToken ct = default) =>
        (await ActiveRowsAsync(db, botId, ct)).Select(t => new TraitItem(t.Id, t.Text)).ToList();

    /// <summary>Turns a prose-only evolved layer into one legacy item (once, on first read).</summary>
    public static async Task EnsureLegacyAsync(AppDbContext db, int botId, CancellationToken ct = default)
    {
        if (await db.BotTraits.AnyAsync(t => t.BotId == botId, ct)) return;
        var latest = await db.PersonaVersions.Where(v => v.BotId == botId)
            .OrderByDescending(v => v.Id).FirstOrDefaultAsync(ct);
        if (latest is null || latest.Snapshot is not null || string.IsNullOrWhiteSpace(latest.Content)) return;
        var trait = new BotTrait { BotId = botId, Text = latest.Content.Trim(), CreatedAt = latest.CreatedAt };
        db.BotTraits.Add(trait);
        await db.SaveChangesAsync(ct);
        latest.Snapshot = Serialize([new TraitItem(trait.Id, trait.Text)]);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Appends a version for the current active set (call after the item rows are saved).</summary>
    public static async Task<PersonaVersion> RecordVersionAsync(AppDbContext db, int botId,
        PersonaVersionSource source, CancellationToken ct = default)
    {
        var items = (await db.BotTraits.Where(t => t.BotId == botId && t.RetiredAt == null)
            .OrderBy(t => t.Id).ToListAsync(ct)).Select(t => new TraitItem(t.Id, t.Text)).ToList();
        var v = new PersonaVersion
        {
            BotId = botId, Content = Render(items), Snapshot = Serialize(items),
            Source = source, CreatedAt = DateTime.UtcNow,
        };
        db.PersonaVersions.Add(v);
        await db.SaveChangesAsync(ct);
        return v;
    }

    /// <summary>Bot-made changes (adds + retires) in the rolling 24 h.</summary>
    public static async Task<int> BotChangesSinceAsync(AppDbContext db, int botId, DateTime since, CancellationToken ct = default) =>
        await db.BotTraits.CountAsync(t => t.BotId == botId && t.SourceMessageId != null && t.CreatedAt > since, ct)
        + await db.BotTraits.CountAsync(t => t.BotId == botId && t.RetiredByMessageId != null && t.RetiredAt > since, ct);

    /// <summary>True while a human edit or revert is younger than the cooldown.</summary>
    public static async Task<bool> InCooldownAsync(AppDbContext db, int botId, DateTime cutoff, CancellationToken ct = default) =>
        await db.PersonaVersions.AnyAsync(v => v.BotId == botId && v.CreatedAt > cutoff
            && (v.Source == PersonaVersionSource.UserEdited || v.Source == PersonaVersionSource.Revert), ct);

    /// <summary>Texts of the traits the bot added / retired in a turn (creator only).</summary>
    public sealed record Summary(IReadOnlyList<string> Added, IReadOnlyList<string> Retired);

    /// <summary>
    /// Applies a complete envelope's trait changes through the guardrails (<see cref="Plan"/>)
    /// and records a <c>BotTurn</c> version. Dropped changes only leave a note.
    /// </summary>
    public static async Task<(Summary?, PersonaVersion?)> ApplyBotChangesAsync(
        AppDbContext db, Bot bot, int userId, TraitChanges req, int messageId,
        TraitLimits limits, int cooldownHours, List<string> notes, CancellationToken ct = default)
    {
        var allowed = CanChange(bot, userId);
        var now = DateTime.UtcNow;
        var active = allowed ? await ActiveAsync(db, bot.Id, ct) : [];
        var used = allowed ? await BotChangesSinceAsync(db, bot.Id, now.AddHours(-24), ct) : 0;
        var cooldown = allowed && await InCooldownAsync(db, bot.Id, now.AddHours(-cooldownHours), ct);
        var plan = Plan(req, active, used, allowed, cooldown, limits);
        notes.AddRange(plan.Notes);
        if (plan.IsEmpty) return (null, null);

        var retiredTexts = new List<string>();
        foreach (var row in await db.BotTraits.Where(t => t.BotId == bot.Id && plan.Retire.Contains(t.Id) && t.RetiredAt == null).ToListAsync(ct))
        {
            row.RetiredAt = now;
            row.RetiredByMessageId = messageId;
            retiredTexts.Add(row.Text);
        }
        foreach (var text in plan.Add)
            db.BotTraits.Add(new BotTrait { BotId = bot.Id, Text = text, CreatedAt = now, SourceMessageId = messageId });
        await db.SaveChangesAsync(ct);
        var version = await RecordVersionAsync(db, bot.Id, PersonaVersionSource.BotTurn, ct);
        notes.Add($"TraitsChanged: +{plan.Add.Count} −{plan.Retire.Count} (version {version.Id})");
        return (new Summary(plan.Add, retiredTexts), version);
    }

    /// <summary>Restores the active set to a version's snapshot (ids kept); the caller then records a Revert version.</summary>
    public static async Task RestoreAsync(AppDbContext db, int botId, PersonaVersion target, CancellationToken ct = default)
    {
        var items = ItemsOf(target);
        var now = DateTime.UtcNow;
        var all = await db.BotTraits.Where(t => t.BotId == botId).ToListAsync(ct);
        var keep = items.Where(i => i.Id > 0).Select(i => i.Id).ToHashSet();
        foreach (var t in all.Where(t => t.RetiredAt == null && !keep.Contains(t.Id)))
        { t.RetiredAt = now; t.RetiredByMessageId = null; }
        foreach (var i in items)
        {
            var row = i.Id > 0 ? all.FirstOrDefault(t => t.Id == i.Id) : null;
            if (row is null) db.BotTraits.Add(new BotTrait { BotId = botId, Text = i.Text, CreatedAt = now });
            else { row.Text = i.Text; row.RetiredAt = null; row.RetiredByMessageId = null; }
        }
        await db.SaveChangesAsync(ct);
    }
}
