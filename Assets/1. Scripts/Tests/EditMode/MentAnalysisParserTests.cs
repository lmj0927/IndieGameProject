using NUnit.Framework;
using Newtonsoft.Json.Linq;
using Poker.Llm;

namespace Poker.Foundation.Tests
{
    public sealed class MentAnalysisParserTests
    {
        private static MentAnalysis Parse(string json)
        {
            Assert.That(MentAnalysisParser.TryParse(json, out var analysis, out string error), Is.True, error);
            return analysis;
        }

        private static void Reject(string json)
            => Assert.That(MentAnalysisParser.TryParse(json, out _, out string error) ? null : error, Is.Not.Null.And.Not.Empty);

        [Test]
        public void ExactRequestMapsTokensToFoundationCards()
        {
            var analysis = Parse("{\"intent\":\"CARD_REQUEST\",\"rank\":\"7\",\"suit\":\"HEART\",\"confidence\":4,\"explicitness\":4}");
            Assert.That(analysis.Intent, Is.EqualTo(MentIntent.CardRequest));
            Assert.That(analysis.Rank, Is.EqualTo(Rank.Seven));
            Assert.That(analysis.Suit, Is.EqualTo(Suit.Hearts));
            Assert.That(analysis.Confidence, Is.EqualTo(4));
            Assert.That(analysis.Explicitness, Is.EqualTo(4));
        }

        [TestCase("A", Rank.Ace)] [TestCase("2", Rank.Two)] [TestCase("10", Rank.Ten)]
        [TestCase("J", Rank.Jack)] [TestCase("Q", Rank.Queen)] [TestCase("K", Rank.King)]
        public void RankTokensRoundTrip(string token, Rank rank)
        {
            Assert.That(MentAnalysisContract.TryParseRank(token, out var parsed), Is.True);
            Assert.That(parsed, Is.EqualTo(rank));
            Assert.That(MentAnalysisContract.RankToToken(rank), Is.EqualTo(token));
        }

        [Test]
        public void PartialRequestsKeepTheMissingDimensionNull()
        {
            var suitOnly = Parse("{\"intent\":\"CARD_REQUEST\",\"rank\":null,\"suit\":\"DIAMOND\",\"confidence\":3,\"explicitness\":1}");
            Assert.That(suitOnly.Rank, Is.Null); Assert.That(suitOnly.Suit, Is.EqualTo(Suit.Diamonds));
            var rankOnly = Parse("{\"intent\":\"CARD_REQUEST\",\"rank\":\"A\",\"suit\":null,\"confidence\":2,\"explicitness\":1}");
            Assert.That(rankOnly.Rank, Is.EqualTo(Rank.Ace)); Assert.That(rankOnly.Suit, Is.Null);
        }

        [Test]
        public void NoneRequiresNullCardAndZeroLevels()
        {
            var none = Parse("{\"intent\":\"NONE\",\"rank\":null,\"suit\":null,\"confidence\":0,\"explicitness\":0}");
            Assert.That(none.Intent, Is.EqualTo(MentIntent.None));
            Reject("{\"intent\":\"NONE\",\"rank\":\"7\",\"suit\":null,\"confidence\":0,\"explicitness\":0}");
            Reject("{\"intent\":\"NONE\",\"rank\":null,\"suit\":null,\"confidence\":1,\"explicitness\":0}");
        }

        [TestCase("{\"intent\":\"CARD_REQUEST\",\"rank\":null,\"suit\":null,\"confidence\":3,\"explicitness\":1}")]
        [TestCase("{\"intent\":\"CARD_REQUEST\",\"rank\":\"7\",\"suit\":null,\"confidence\":0,\"explicitness\":1}")]
        [TestCase("{\"intent\":\"CARD_REQUEST\",\"rank\":\"7\",\"suit\":null,\"confidence\":2,\"explicitness\":0}")]
        public void InconsistentCardRequestIsAValidationFailure(string json) => Reject(json);

        [TestCase("{\"intent\":\"WANT\",\"rank\":null,\"suit\":null,\"confidence\":0,\"explicitness\":0}")]
        [TestCase("{\"intent\":\"CARD_REQUEST\",\"rank\":\"1\",\"suit\":null,\"confidence\":2,\"explicitness\":1}")]
        [TestCase("{\"intent\":\"CARD_REQUEST\",\"rank\":7,\"suit\":null,\"confidence\":2,\"explicitness\":1}")]
        [TestCase("{\"intent\":\"CARD_REQUEST\",\"rank\":null,\"suit\":\"RED\",\"confidence\":2,\"explicitness\":1}")]
        [TestCase("{\"intent\":\"CARD_REQUEST\",\"rank\":\"7\",\"suit\":null,\"confidence\":5,\"explicitness\":1}")]
        [TestCase("{\"intent\":\"CARD_REQUEST\",\"rank\":\"7\",\"suit\":null,\"confidence\":2.5,\"explicitness\":1}")]
        [TestCase("{\"intent\":\"CARD_REQUEST\",\"rank\":\"7\",\"suit\":null,\"confidence\":2,\"explicitness\":-1}")]
        [TestCase("{\"intent\":\"CARD_REQUEST\",\"rank\":\"7\",\"confidence\":2,\"explicitness\":1}")]
        public void UnknownValuesMissingKeysAndRangesAreRejected(string json) => Reject(json);

        [TestCase("")] [TestCase("   ")] [TestCase("{\"intent\":")] [TestCase("하트 7")] [TestCase("[]")]
        public void BrokenResponsesAreRejected(string json) => Reject(json);

        [Test]
        public void SchemaRequiresEveryContractKeyAndForbidsExtras()
        {
            var schema = JObject.Parse(MentAnalysisPrompt.JsonSchema);
            Assert.That(schema["required"].ToObject<string[]>(),
                Is.EquivalentTo(new[] { "intent", "rank", "suit", "confidence", "explicitness" }));
            Assert.That((bool)schema["additionalProperties"], Is.False);
            Assert.That(schema["properties"]["color"], Is.Null);
        }
    }
}
