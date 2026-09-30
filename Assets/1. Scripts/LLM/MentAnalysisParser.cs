using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static class MentAnalysisParser
{
    public static bool TryParse(string json, out MentAnalysis analysis, out string error)
    {
        analysis = default;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Empty response.";
            return false;
        }

        JObject root;
        try
        {
            root = JObject.Parse(json);
        }
        catch (JsonException ex)
        {
            error = $"Invalid JSON: {ex.Message}";
            return false;
        }

        if (!TryReadIntent(root, out MentIntent intent, out error)) return false;
        if (!TryReadRank(root, out int? rank, out error)) return false;
        if (!TryReadSuit(root, out Suit? suit, out error)) return false;
        if (!TryReadLevel(root, "confidence", out int confidence, out error)) return false;
        if (!TryReadLevel(root, "explicitness", out int explicitness, out error)) return false;
        if (!CheckConsistency(intent, rank, suit, confidence, explicitness, out error)) return false;

        analysis = new MentAnalysis(intent, rank, suit, confidence, explicitness);
        return true;
    }

    private static bool TryReadIntent(JObject root, out MentIntent intent, out string error)
    {
        intent = default;
        if (!TryGetToken(root, "intent", out JToken token, out error)) return false;

        if (token.Type != JTokenType.String || !MentAnalysisContract.TryParseIntent((string)token, out intent))
        {
            error = $"Invalid intent: {token.ToString(Formatting.None)}";
            return false;
        }
        return true;
    }

    private static bool TryReadRank(JObject root, out int? rank, out string error)
    {
        rank = null;
        if (!TryGetToken(root, "rank", out JToken token, out error)) return false;
        if (token.Type == JTokenType.Null) return true;

        if (token.Type != JTokenType.String || !MentAnalysisContract.TryParseRank((string)token, out int value))
        {
            error = $"Invalid rank: {token.ToString(Formatting.None)}";
            return false;
        }
        rank = value;
        return true;
    }

    private static bool TryReadSuit(JObject root, out Suit? suit, out string error)
    {
        suit = null;
        if (!TryGetToken(root, "suit", out JToken token, out error)) return false;
        if (token.Type == JTokenType.Null) return true;

        if (token.Type != JTokenType.String || !MentAnalysisContract.TryParseSuit((string)token, out Suit value))
        {
            error = $"Invalid suit: {token.ToString(Formatting.None)}";
            return false;
        }
        suit = value;
        return true;
    }

    private static bool TryReadLevel(JObject root, string key, out int level, out string error)
    {
        level = 0;
        if (!TryGetToken(root, key, out JToken token, out error)) return false;

        if (token.Type != JTokenType.Integer)
        {
            error = $"Invalid {key}: {token.ToString(Formatting.None)}";
            return false;
        }

        long value = (long)token;
        if (value < MentAnalysis.MinLevel || value > MentAnalysis.MaxLevel)
        {
            error = $"{key} out of range: {value}";
            return false;
        }
        level = (int)value;
        return true;
    }

    private static bool CheckConsistency(MentIntent intent, int? rank, Suit? suit, int confidence, int explicitness, out string error)
    {
        error = null;

        if (intent == MentIntent.None)
        {
            if (rank.HasValue || suit.HasValue || confidence != MentAnalysis.MinLevel || explicitness != MentAnalysis.MinLevel)
            {
                error = "NONE must have null rank/suit and confidence/explicitness 0.";
                return false;
            }
            return true;
        }

        if (confidence == MentAnalysis.MinLevel || explicitness == MentAnalysis.MinLevel)
        {
            error = "CARD_REQUEST must have confidence/explicitness 1~4.";
            return false;
        }

        if (!rank.HasValue && !suit.HasValue)
        {
            error = "CARD_REQUEST must specify rank or suit.";
            return false;
        }
        return true;
    }

    private static bool TryGetToken(JObject root, string key, out JToken token, out string error)
    {
        if (!root.TryGetValue(key, out token))
        {
            error = $"Missing '{key}'.";
            return false;
        }
        error = null;
        return true;
    }
}
