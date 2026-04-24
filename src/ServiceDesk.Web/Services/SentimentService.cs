namespace ServiceDesk.Web.Services;

/// <summary>
/// Lightweight keyword-based urgency detector.
/// Used to boost AI priority suggestions when urgent language is detected.
/// No ML libraries required — pure keyword matching against two weighted lists.
/// </summary>
public static class SentimentService
{
    // Single-word signals — each match adds 1 point
    private static readonly string[] UrgencyWords =
    [
        "urgent", "urgently", "critical", "emergency", "emergencies",
        "outage", "down", "offline", "unresponsive", "crash", "crashed",
        "broken", "failure", "failed", "fails", "failing",
        "immediately", "asap", "blocker", "blocked", "blocking",
        "production", "prod", "ransomware", "breach", "hack", "hacked",
        "virus", "malware", "lockout", "locked", "exposed",
        "corrupted", "corruption"
    ];

    // Phrase signals — each match adds 2 points (higher confidence)
    private static readonly string[] UrgencyPhrases =
    [
        "cannot work", "can't work", "cannot login", "can't login",
        "not working", "doesn't work", "wont start", "wont open",
        "site down", "server down", "system down", "network down",
        "all users", "entire office", "entire company", "everyone affected",
        "data loss", "data lost", "lost data", "deleted data",
        "password reset", "locked out", "security incident",
        "right now", "as soon as possible", "by end of day", "by eod",
        "complete outage", "full outage", "no access"
    ];

    /// <summary>
    /// Computes a raw urgency score (0+).
    /// Score ≥ 2 is treated as urgent; score ≥ 4 triggers a two-level priority boost.
    /// </summary>
    public static int ComputeUrgencyScore(string title, string description)
    {
        var text = ((title ?? string.Empty) + " " + (description ?? string.Empty))
            .ToLowerInvariant();

        var score = 0;
        foreach (var word in UrgencyWords)
            if (text.Contains(word, StringComparison.Ordinal)) score++;

        foreach (var phrase in UrgencyPhrases)
            if (text.Contains(phrase, StringComparison.Ordinal)) score += 2;

        return score;
    }

    /// <summary>Returns true when strong urgency signals are present.</summary>
    public static bool IsUrgent(string title, string description)
        => ComputeUrgencyScore(title, description) >= 2;

    /// <summary>
    /// Suggests a boosted priority if urgency keywords are found.
    /// Returns null when no boost is warranted (score &lt; 2 or already Critical).
    /// Boosts by one level for score 2–3, two levels for score ≥ 4.
    /// </summary>
    public static Core.Enums.TicketPriority? GetBoostPriority(
        string title,
        string description,
        Core.Enums.TicketPriority? currentSuggestion)
    {
        if (currentSuggestion == null) return null;

        var score = ComputeUrgencyScore(title, description);
        if (score < 2) return null;

        var current   = (int)currentSuggestion.Value;
        var maxEnum   = (int)Core.Enums.TicketPriority.Critical;
        var boostLevels = score >= 4 ? 2 : 1;
        var boosted   = Math.Min(current + boostLevels, maxEnum);

        return boosted != current ? (Core.Enums.TicketPriority)boosted : null;
    }
}
