using System;
using System.Collections.Generic;

public class Deck
{
    public const int FullSize = 52;

    private readonly List<Card> _cards = new List<Card>(FullSize);

    public int Count => _cards.Count;

    public Deck()
    {
        Reset();
    }

    public void Reset()
    {
        _cards.Clear();

        foreach (Suit suit in Enum.GetValues(typeof(Suit)))
        {
            for (int rank = Card.MinRank; rank <= Card.MaxRank; rank++)
            {
                _cards.Add(new Card(rank, suit));
            }
        }
    }

    public void Shuffle(Random random)
    {
        if (random == null)
        {
            throw new ArgumentNullException(nameof(random));
        }

        for (int i = _cards.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (_cards[i], _cards[j]) = (_cards[j], _cards[i]);
        }
    }

    public Card Draw()
    {
        if (_cards.Count == 0)
        {
            throw new InvalidOperationException("Deck is empty.");
        }

        int last = _cards.Count - 1;
        Card card = _cards[last];
        _cards.RemoveAt(last);
        return card;
    }

    public bool Contains(Card card) => _cards.Contains(card);
}
