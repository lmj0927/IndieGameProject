using System;
using System.Collections.Generic;

public static class MentCandidateSelector
{
    // 유효한 CARD_REQUEST 중 confidence가 가장 높은 결과의 인덱스를 반환한다.
    // 동률은 random으로 균등하게 고르고, 후보가 없으면 -1을 반환한다.
    public static int Select(IReadOnlyList<MentAnalysisResult> results, Random random)
    {
        int selected = -1;
        int bestConfidence = int.MinValue;
        int tieCount = 0;

        for (int i = 0; i < results.Count; i++)
        {
            MentAnalysisResult result = results[i];
            if (!result.IsValid || result.Analysis.Intent != MentIntent.CardRequest) continue;

            int confidence = result.Analysis.Confidence;
            if (confidence > bestConfidence)
            {
                bestConfidence = confidence;
                selected = i;
                tieCount = 1;
            }
            else if (confidence == bestConfidence)
            {
                tieCount++;
                if (random.Next(tieCount) == 0) selected = i;
            }
        }
        return selected;
    }
}
