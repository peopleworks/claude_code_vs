namespace TestLab;

/// <summary>
/// Demo fixture for the vs-test fix-verify loop (spike_test_runner). Three outcomes the loop is built
/// around: a passing test, a FAILED ASSERTION, and an UNHANDLED EXCEPTION. The two failing ones are the
/// "run just this test under the debugger and stop at the fault with locals live" targets.
/// </summary>
public class ScoreTests
{
    [Fact] // passing baseline
    public void Add_TwoPositives_Sums()
    {
        Assert.Equal(5, Scorer.Add(2, 3));
    }

    [Fact] // FAILS on an assertion: the flat per-round bonus is accumulated instead of reset
    public void ScoreRounds_FlatBonusPerRound()
    {
        int total = Scorer.ScoreRounds(new[] { 10, 10, 10 });
        Assert.Equal(45, total); // bug makes it 60 (bonus compounds) -> break here, watch `bonus`
    }

    [Fact] // FAILS by throwing: DivideByZeroException -> the vs_break_on_thrown target
    public void Ratio_ByZero_Throws()
    {
        int r = Scorer.Ratio(10, 0);
        Assert.True(r >= 0);
    }
}

/// <summary>Intermittent failures — the target for vs_hunt_flaky (the flaky/transient hunter).</summary>
public class FlakyTests
{
    [Fact] // FLAKY: fails ~1 in 3 runs via an assertion — a transient bug to force-reproduce.
    public void Flaky_IntermittentAssert()
    {
        int roll = Random.Shared.Next(3);
        Assert.True(roll != 0, $"Flaky failure: roll landed on {roll} (fails ~1 in 3 runs)");
    }

    [Fact] // FLAKY: throws intermittently — the red-handed-under-debugger target.
    public void Flaky_IntermittentThrow()
    {
        if (Random.Shared.Next(3) == 0)
            throw new InvalidOperationException("Flaky throw: hit the 1-in-3 path.");
    }
}

/// <summary>Buggy code under test.</summary>
internal static class Scorer
{
    public static int Add(int a, int b) => a + b;

    // BUG: `bonus` should be a flat 5 per round; `+=` compounds it across rounds.
    public static int ScoreRounds(int[] rounds)
    {
        int total = 0;
        int bonus = 0;
        foreach (int r in rounds)
        {
            bonus += 5;          // intended: bonus = 5;
            total += r + bonus;
        }
        return total;
    }

    public static int Ratio(int a, int b) => a / b; // throws when b == 0
}
