using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using Poker.Foundation;

namespace Poker.Application
{
    /// <summary>
    /// Host-only structured-response boundary. The host supplies the source ID; the model supplies
    /// features, never Level, a card-change verdict or player identity. Invalid output is not NoRequest.
    /// Adapter wire vocabulary: none=[]; rank=["2".."10","J","Q","K","A"]; suit=["C"|"D"|"H"|"S"];
    /// color=["RED"|"BLACK"]; card=[rank,suit]. Each response describes one target range.
    /// </summary>
    public static class DealerResponseParser
    {
        public const int MaximumResponseBytes = 4096;

        public static bool TryParse(Guid sourceId, string json, out HoldemDealerFeatures features)
        {
            features = null;
            if (sourceId == Guid.Empty || string.IsNullOrWhiteSpace(json)
                || json.Length > MaximumResponseBytes || !HasSingleObjectEnvelope(json)) return false;
            try
            {
                byte[] bytes = new UTF8Encoding(false, true).GetBytes(json);
                if (bytes.Length > MaximumResponseBytes) return false;
                var quotas = new XmlDictionaryReaderQuotas {
                    MaxDepth = 4, MaxStringContentLength = MaximumResponseBytes,
                    MaxArrayLength = 2, MaxBytesPerRead = MaximumResponseBytes * 4,
                    MaxNameTableCharCount = 256
                };
                using var reader = JsonReaderWriterFactory.CreateJsonReader(bytes, quotas);
                if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "root"
                    || reader.GetAttribute("type") != "object" || reader.GetAttribute("__type") != null) return false;
                reader.ReadStartElement();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                string targetType = null, directness = null;
                var target = new List<string>();
                bool request = false;
                double confidence = -1;
                while (reader.MoveToContent() == XmlNodeType.Element)
                {
                    string key = reader.NamespaceURI == "item" ? reader.GetAttribute("item") : reader.LocalName;
                    if (!seen.Add(key)) return false;
                    switch (key)
                    {
                        case "target_type": targetType = ReadString(reader); break;
                        case "directness": directness = ReadString(reader); break;
                        case "association":
                            if (ReadString(reader).Length > 256) return false;
                            break; // Explanation is not a scoring input and is never retained in the domain.
                        case "target":
                            RequireType(reader, "array");
                            if (reader.IsEmptyElement) { reader.Read(); break; }
                            reader.ReadStartElement();
                            while (reader.MoveToContent() == XmlNodeType.Element)
                            {
                                if (reader.LocalName != "item" || target.Count == 2) return false;
                                target.Add(ReadString(reader));
                            }
                            reader.ReadEndElement();
                            break;
                        case "is_request":
                            RequireType(reader, "boolean");
                            string boolean = reader.ReadElementContentAsString();
                            if (boolean != "true" && boolean != "false") return false;
                            request = boolean == "true";
                            break;
                        case "confidence":
                            RequireType(reader, "number");
                            if (!double.TryParse(reader.ReadElementContentAsString(), NumberStyles.Float,
                                CultureInfo.InvariantCulture, out confidence)) return false;
                            break;
                        default: return false;
                    }
                }
                reader.ReadEndElement();
                if (reader.MoveToContent() != XmlNodeType.None || seen.Count != 6
                    || double.IsNaN(confidence) || double.IsInfinity(confidence)
                    || confidence < 0 || confidence > 1) return false;
                HoldemDealerDirectness strength;
                switch (directness)
                {
                    case "none": strength = HoldemDealerDirectness.None; break;
                    case "far_association": strength = HoldemDealerDirectness.FarAssociation; break;
                    case "near_association": strength = HoldemDealerDirectness.NearAssociation; break;
                    case "direct_mention": strength = HoldemDealerDirectness.DirectMention; break;
                    default: return false;
                }
                Rank? rank = null; Suit? suit = null; HoldemDealerColor? color = null;
                switch (targetType)
                {
                    case "none": if (target.Count != 0) return false; break;
                    case "rank": if (target.Count != 1) return false; rank = ParseRank(target[0]); break;
                    case "suit": if (target.Count != 1) return false; suit = ParseSuit(target[0]); break;
                    case "card":
                        if (target.Count != 2) return false;
                        rank = ParseRank(target[0]); suit = ParseSuit(target[1]); break;
                    case "color":
                        if (target.Count != 1 || target[0] != "RED" && target[0] != "BLACK") return false;
                        color = target[0] == "RED" ? HoldemDealerColor.Red : HoldemDealerColor.Black; break;
                    default: return false;
                }
                features = new HoldemDealerFeatures(sourceId, rank, suit, color, strength, request, confidence);
                return true;
            }
            catch (XmlException) { return false; }
            catch (SerializationException) { return false; }
            catch (FormatException) { return false; }
            catch (ArgumentException) { return false; }
        }

        // The framework reader accepts a trailing object/comma. Check just the envelope here;
        // decoding, value syntax and types remain the framework reader's responsibility.
        private static bool HasSingleObjectEnvelope(string json)
        {
            var nesting = new char[4];
            int depth = 0;
            bool quoted = false, escaped = false, started = false, ended = false;
            char previous = '\0';
            foreach (char c in json)
            {
                if (quoted)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') { quoted = false; previous = '"'; }
                    continue;
                }
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') continue;
                if (ended || !started && c != '{') return false;
                started = true;
                if (c == '"') { quoted = true; continue; }
                if (c == '{' || c == '[')
                {
                    if (depth == nesting.Length) return false;
                    nesting[depth++] = c;
                }
                else if (c == '}' || c == ']')
                {
                    if (depth == 0 || previous == ',' || nesting[--depth] != (c == '}' ? '{' : '[')) return false;
                    ended = depth == 0;
                }
                previous = c;
            }
            return ended && !quoted && depth == 0;
        }

        private static string ReadString(XmlReader reader)
        { RequireType(reader, "string"); return reader.ReadElementContentAsString(); }
        private static void RequireType(XmlReader reader, string type)
        { if (reader.GetAttribute("type") != type) throw new FormatException("Unexpected feature type."); }
        private static Rank ParseRank(string text)
        {
            string[] ranks = { "2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K", "A" };
            int index = Array.IndexOf(ranks, text);
            if (index < 0) throw new FormatException("Unknown rank.");
            return (Rank)(index + 2);
        }
        private static Suit ParseSuit(string text)
        {
            switch (text)
            {
                case "C": return Suit.Clubs;
                case "D": return Suit.Diamonds;
                case "H": return Suit.Hearts;
                case "S": return Suit.Spades;
                default: throw new FormatException("Unknown suit.");
            }
        }
    }
}
