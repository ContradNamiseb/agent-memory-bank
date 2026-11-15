namespace AgentMemoryBank.Verification;

/// <summary>
/// Pure domain rules governing whether a proposed claim is falsifiable.
/// </summary>
public static class ClaimValidator
{
    /// <summary>Words that promise a claim nothing can falsify.</summary>
    private static readonly string[] Hedges =
        ["might", "may be", "possibly", "perhaps", "probably", "seems", "appears to", "could be"];

    /// <summary>
    /// A claim has to be able to turn out false. This check is deliberately weak —
    /// it catches emptiness and pure hedging, not bad judgement — because a gate
    /// that rejects good notes teaches authors to route around the store.
    /// </summary>
    public static bool IsFalsifiable(string claim, out string why)
    {
        why = "";
        if (claim.Length < 12)
        {
            why = "the claim is too short to state anything checkable";
            return false;
        }
        if (claim.EndsWith('?'))
        {
            why = "a question is not a claim; record what you established, not what you asked";
            return false;
        }
        if (Hedges.Any(h => claim.Contains(h, StringComparison.OrdinalIgnoreCase)))
        {
            why = "the claim hedges ('might', 'seems', 'probably'), so nothing could contradict it; " +
                  "state what was observed, and put the uncertainty in confidence instead";
            return false;
        }
        return true;
    }
}
