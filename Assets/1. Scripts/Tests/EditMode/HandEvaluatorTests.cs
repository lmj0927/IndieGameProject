using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Poker.Foundation.Tests
{
    public class HandEvaluatorTests
    {
        [TestCase("As Jd 9h 6c 3s", HandCategory.HighCard, 14, 11, 9, 6, 3)]
        [TestCase("As Ad 9h 6c 3s", HandCategory.OnePair, 14, 9, 6, 3, 0)]
        [TestCase("As Ad 9h 9c 3s", HandCategory.TwoPair, 14, 9, 3, 0, 0)]
        [TestCase("As Ad Ah 6c 3s", HandCategory.ThreeOfAKind, 14, 6, 3, 0, 0)]
        [TestCase("9s 8d 7h 6c 5s", HandCategory.Straight, 9, 0, 0, 0, 0)]
        [TestCase("As Js 9s 6s 3s", HandCategory.Flush, 14, 11, 9, 6, 3)]
        [TestCase("As Ad Ah 6c 6s", HandCategory.FullHouse, 14, 6, 0, 0, 0)]
        [TestCase("As Ad Ah Ac 3s", HandCategory.FourOfAKind, 14, 3, 0, 0, 0)]
        [TestCase("9s 8s 7s 6s 5s", HandCategory.StraightFlush, 9, 0, 0, 0, 0)]
        [TestCase("Ac 2d 3h 4s 5c", HandCategory.Straight, 5, 0, 0, 0, 0)]
        [TestCase("Ac Kd Qh Js Tc", HandCategory.Straight, 14, 0, 0, 0, 0)]
        [TestCase("Ac Kd Qh 2s 3c", HandCategory.HighCard, 14, 13, 12, 3, 2)]
        [TestCase("As 2s 3s 4s 5s", HandCategory.StraightFlush, 5, 0, 0, 0, 0)]
        [TestCase("As Ks Qs Js Ts", HandCategory.StraightFlush, 14, 0, 0, 0, 0)]
        public void CategoryAndEveryTieBreakerMatch(string text, HandCategory category,
            int a, int b, int c, int d, int e)
        {
            HandValue value = HandEvaluator.Evaluate(Parse(text));
            Assert.That(value.IsValid, Is.True);
            Assert.That(value.Category, Is.EqualTo(category));
            int[] expected = { a, b, c, d, e };
            int count = Array.FindIndex(expected, rank => rank == 0);
            if (count < 0) count = 5;
            Assert.That(value.TieBreakerCount, Is.EqualTo(count));
            for (int i = 0; i < count; i++) Assert.That(value.GetTieBreaker(i), Is.EqualTo(expected[i]));
            Assert.Throws<ArgumentOutOfRangeException>(() => value.GetTieBreaker(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => value.GetTieBreaker(count));
        }

        // Synthetic comparisons may share cards: this tests the evaluator, not a dealt table.
        [TestCase("As Ad Kh Qc 3s", "As Ad Kh Qc 2s")]
        [TestCase("As Ad Kh Qc 2s", "Ks Kd Ah Qc Js")]
        [TestCase("8s 8d Ah Qc 2s", "8s 8d Kh Qc Js")]
        [TestCase("8s 8d Ah Qc 2s", "8s 8d Ah Jc Ts")]
        [TestCase("As Ad 9h 9c 2s", "Ks Kd Qh Qc As")]
        [TestCase("As Ad 9h 9c 2s", "As Ad 8h 8c Ks")]
        [TestCase("As Ad 9h 9c 3s", "As Ad 9h 9c 2s")]
        [TestCase("8s 8h 8d 3c 2s", "7s 7h 7d Ac Kh")]
        [TestCase("7s 7h 7d Ac 2h", "7s 7h 7d Kc Qh")]
        [TestCase("7s 7h 7d Ac 5h", "7s 7h 7d Ac 4h")]
        [TestCase("8s 8h 8d 2c 2d", "7s 7h 7d Ac Ad")]
        [TestCase("7s 7h 7d Ac Ad", "7s 7h 7d Kc Kd")]
        [TestCase("4s 4h 4d 4c 2h", "3s 3h 3d 3c Ah")]
        [TestCase("3s 3h 3d 3c Ah", "3s 3h 3d 3c Kh")]
        [TestCase("As Kd 9h 7c 3s", "As Kd 9h 7c 2s")]
        [TestCase("As Ks 9s 7s 3s", "As Ks 9s 7s 2s")]
        [TestCase("6s 5h 4d 3c 2h", "As 5h 4d 3c 2h")]
        [TestCase("6s 5s 4s 3s 2s", "As 5s 4s 3s 2s")]
        public void TieBreakersDecideInPriorityOrder(string strong, string weak)
        {
            HandValue a = HandEvaluator.Evaluate(Parse(strong));
            HandValue b = HandEvaluator.Evaluate(Parse(weak));
            Assert.That(a > b, Is.True);
            Assert.That(b < a, Is.True);
            Assert.That(a.Equals(b), Is.False);
        }

        [Test]
        public void CategoryAlwaysPrecedesKickers()
        {
            string[] ascending = { "As Kd Qh Jc 9s", "2s 2d 4h 5c 7s", "2s 2d 3h 3c 5s",
                "2s 2d 2h 4c 5s", "As 2d 3h 4c 5s", "2s 4s 6s 8s Ts",
                "2s 2d 2h 3c 3s", "2s 2d 2h 2c 3s", "As 2s 3s 4s 5s" };
            for (int i = 1; i < ascending.Length; i++)
                Assert.That(HandEvaluator.Evaluate(Parse(ascending[i])) >
                    HandEvaluator.Evaluate(Parse(ascending[i - 1])), Is.True);
        }

        [Test]
        public void NullInputIsRejected() => Assert.Throws<ArgumentNullException>(
            () => HandEvaluator.Evaluate(null));

        [TestCase(0)]
        [TestCase(4)]
        [TestCase(6)]
        public void WrongCardCountIsRejected(int count) => Assert.Throws<ArgumentException>(
            () => HandEvaluator.Evaluate(new Card[count]));

        [Test]
        public void DuplicateAndDefaultCardsAreRejectedWithoutChangingInput()
        {
            foreach (string text in new[] { "As As 9h 6c 3s", "As Ad 9h 6c 3s" })
            {
                Card[] cards = Parse(text);
                if (text.StartsWith("As Ad")) cards[4] = default;
                Card[] before = (Card[])cards.Clone();
                Assert.Throws<ArgumentException>(() => HandEvaluator.Evaluate(cards));
                Assert.That(cards, Is.EqualTo(before));
            }
        }

        [Test]
        public void DefaultHandValueCannotBeRanked()
        {
            HandValue value = default;
            Assert.That(value.IsValid, Is.False);
            Assert.Throws<InvalidOperationException>(() => value.CompareTo(default));
            Assert.Throws<InvalidOperationException>(() => value.GetTieBreaker(0));
        }

        [Test]
        public void EvaluationNeitherMutatesNorRetainsInput()
        {
            Card[] cards = Parse("3s As 6c Ad 9h");
            Card[] before = (Card[])cards.Clone();
            HandValue value = HandEvaluator.Evaluate(cards);
            int hash = value.GetHashCode();
            Assert.That(cards, Is.EqualTo(before));
            Array.Reverse(cards);
            cards[0] = default;
            Assert.That(value, Is.EqualTo(HandEvaluator.Evaluate(before)));
            Assert.That(value.GetHashCode(), Is.EqualTo(hash));
        }

        [TestCase("As Ad 9h 6c 3s")]
        [TestCase("Ac 2d 3h 4s 5c")]
        [TestCase("Ks Kh Kd 3s 3h")]
        public void All120InputOrdersHaveTheSameValue(string text)
        {
            Card[] cards = Parse(text);
            HandValue expected = HandEvaluator.Evaluate(cards);
            int count = 0;
            VisitPermutations(cards, 0, permutation =>
            {
                HandValue actual = HandEvaluator.Evaluate(permutation);
                Assert.That(actual, Is.EqualTo(expected));
                Assert.That(actual.GetHashCode(), Is.EqualTo(expected.GetHashCode()));
                count++;
            });
            Assert.That(count, Is.EqualTo(120));
        }

        [TestCase("As Ks 9s 6s 3s")]
        [TestCase("As Ad 9h 6c 3s")]
        [TestCase("Ac 2d 3h 4s 5c")]
        [TestCase("Ks Kh Kd 3s 3h")]
        public void All24SuitRenamingsAreEqual(string text)
        {
            Card[] cards = Parse(text);
            HandValue expected = HandEvaluator.Evaluate(cards);
            int count = 0;
            for (int a = 1; a <= 4; a++)
                for (int b = 1; b <= 4; b++)
                    for (int c = 1; c <= 4; c++)
                        for (int d = 1; d <= 4; d++)
                        {
                            var suits = new HashSet<int> { a, b, c, d };
                            if (suits.Count != 4) continue;
                            int[] map = { a, b, c, d };
                            var renamed = new Card[cards.Length];
                            for (int i = 0; i < cards.Length; i++)
                                renamed[i] = new Card(cards[i].Rank, (Suit)map[(int)cards[i].Suit - 1]);
                            HandValue actual = HandEvaluator.Evaluate(renamed);
                            Assert.That(actual.CompareTo(expected), Is.Zero);
                            Assert.That(actual == expected, Is.True);
                            Assert.That(actual.GetHashCode(), Is.EqualTo(expected.GetHashCode()));
                            count++;
                        }
            Assert.That(count, Is.EqualTo(24));
        }

        [Test]
        public void ValidValuesObeyComparisonLawsOnSeededSamples()
        {
            var random = new Random(20260910);
            for (int sample = 0; sample < 500; sample++)
            {
                HandValue a = RandomHand(random), b = RandomHand(random), c = RandomHand(random);
                int ab = a.CompareTo(b), ba = b.CompareTo(a);
                Assert.That(Math.Sign(ab), Is.EqualTo(-Math.Sign(ba)));
                Assert.That(ab == 0, Is.EqualTo(a.Equals(b)));
                if (a.Equals(b)) Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
                Assert.That(a.CompareTo(a), Is.Zero);
                if (a > b && b > c) Assert.That(a > c, Is.True);
                if (a < b && b < c) Assert.That(a < c, Is.True);
            }
        }

        private static HandValue RandomHand(Random random)
        {
            var ids = new HashSet<int>();
            var cards = new Card[5];
            for (int i = 0; i < cards.Length;)
            {
                int id = random.Next(52);
                if (ids.Add(id)) cards[i++] = Card.FromId(id);
            }
            return HandEvaluator.Evaluate(cards);
        }

        internal static Card[] Parse(string text)
        {
            const string ranks = "23456789TJQKA", suits = "cdhs";
            string[] tokens = text.Split(' ');
            var cards = new Card[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                if (tokens[i].Length != 2) throw new ArgumentException("Test fixture must use two-character cards.");
                int rank = ranks.IndexOf(tokens[i][0]), suit = suits.IndexOf(tokens[i][1]);
                if (rank < 0 || suit < 0) throw new ArgumentException("Invalid test fixture card.");
                cards[i] = new Card((Rank)(rank + 2), (Suit)(suit + 1));
            }
            return cards;
        }

        private static void VisitPermutations(Card[] cards, int start, Action<Card[]> visit)
        {
            if (start == cards.Length) { visit(cards); return; }
            for (int i = start; i < cards.Length; i++)
            {
                Card temp = cards[start]; cards[start] = cards[i]; cards[i] = temp;
                VisitPermutations(cards, start + 1, visit);
                temp = cards[start]; cards[start] = cards[i]; cards[i] = temp;
            }
        }
    }
}
